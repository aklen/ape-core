# Networking — Replication, Access Control, and Large Files

**Status:** Living document

Covers replica broadcast and subscriptions, config-driven access control, and content-addressed file chunk transfer.

**Related:** [ARCHITECTURE.md](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md) · [DETERMINISM.md](./DETERMINISM.md)

---

# Part I — Replication design and roadmap

This document maps the current replication and delta transmission mechanism, identifies gaps, and proposes subscription-based and other extensions for future implementation.

---

## 1. Current Architecture Overview

### 1.1 Data Flow

```
┌─────────────────────────────────────────────────────────────────────────────┐
│  ReplicaManager.Tick() (called ~10 Hz from Program.cs main loop)           │
│                                                                             │
│  - For each replica we OWN:                                                 │
│    - if HasChanges() → BuildPacket(Update) → Broadcast(packet)              │
│    - Broadcast = Send to ALL connected peers                                 │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  NetworkManager.Broadcast(data)                                              │
│                                                                             │
│  foreach (peerId in GetConnectedPeers())                                    │
│      Send(peerId, data, reliable: true)                                     │
└─────────────────────────────────────────────────────────────────────────────┘
                                    │
                                    ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│  INetworkTransport (LiteNetLib / QUIC)                                       │
│  - SendReliable(peerId, data)                                                │
│  - SendUnreliable(peerId, data)                                             │
└─────────────────────────────────────────────────────────────────────────────┘
```

### 1.2 Broadcast Model (Current)

| Event | Target | Method |
|-------|--------|--------|
| **Register** (Create) | All peers | `Broadcast` |
| **Update** (Tick) | All peers | `Broadcast` |
| **Unregister** (Delete) | All peers | `Broadcast` |
| **Initial sync** (peer connected) | Single peer | `Send(peerId, ...)` |

**Result:** Every change goes to every connected instance. No filtering or subscription.

### 1.3 Change Detection (Current)

| Level | Mechanism | Status |
|-------|-----------|--------|
| **Object-level** | `HasChanges()` vs snapshot | ✅ Implemented |
| **Property-level** | `HasChanges()` compares all properties | ✅ Implemented |
| **Delta serialization** | Only changed properties in packet | ❌ Not implemented |

**Note:** `ReplicaPacketBuilder` comment says "Contains only changed property data" for Update, but `BuildPacket` calls `replica.Serialize()` which serializes the **full object**. The "delta" is only at the decision level (we skip unchanged objects), not at the payload level.

### 1.4 Packet Types

| Type | When | Payload |
|------|------|---------|
| `FullSync` | Peer connected | Full replica |
| `Create` | Replica registered | Full replica |
| `Update` | Tick + HasChanges() | Full replica (current) |
| `Delete` | Replica unregistered | Replica ID only |

---

## 2. Identified Gaps & Pain Points

### 2.1 Broadcast-Only Model

- **Problem:** Every peer receives every change, regardless of interest.
- **Impact:** Wasted bandwidth, CPU, and battery when many peers exist but only a subset care about specific replicas.

### 2.2 No Subscription / Interest Management

- **Problem:** No way to express "I only care about replicas in room X" or "I only need replicas near my avatar".
- **Impact:** Cannot scale to large worlds or many participants without sending everything to everyone.

### 2.3 Full Object Serialization on Update

- **Problem:** Update packets contain the full object, not just changed properties.
- **Impact:** Extra bandwidth when only small properties change (e.g. Position.X).

### 2.4 No Peer-Specific Filtering

- **Problem:** `Broadcast` is all-or-nothing; no `SendToSubscribers(replicaId, data)`.
- **Impact:** Cannot implement subscription without changing the transport layer.

---

## 3. Proposed Mechanisms

### 3.1 Subscription / Interest Management

**Goal:** Allow peers to declare what they care about; only send updates to interested peers.

#### Option A: Replica-Level Subscription

```
Peer subscribes to: replica IDs, paths, or types
Example: Subscribe("ReplicaId", "abc-123") or Subscribe("Path", "/Root/Players/*")
```

- **Pros:** Simple, explicit.
- **Cons:** Client must know IDs/paths upfront; dynamic scenes need re-subscription.

#### Option B: Spatial / Region-Based

```
Peer subscribes to: region (e.g. AABB, sphere)
Only replicas within region are sent.
```

- **Pros:** Natural for games/VR; "near me" semantics.
- **Cons:** Requires spatial data; replicas need Position; server needs to maintain spatial index.

#### Option C: Layer / Channel

```
Replicas belong to layers (e.g. "world", "ui", "debug")
Peers subscribe to layers.
```

- **Pros:** Clean separation (e.g. UI vs world); easy to add/remove.
- **Cons:** Coarse; not spatial.

#### Option D: Hybrid

- Layers for coarse filtering (e.g. "world", "ui").
- Within "world": spatial or path-based subscription.

### 3.2 Implementation Sketch: ReplicaAccessControlService

See **[NETWORKING.md#part-ii--replica-access-control-config](NETWORKING.md#part-ii--replica-access-control-config)** for the full design. The service is named **ReplicaAccessControlService** and covers subscription, authentication, authorization, permissions, and privileges.

```csharp
// IReplicaAccessControlService (simplified)
// peerId: string (logical ID, aligns with participantId, OwnerId)
public interface IReplicaAccessControlService
{
    IReadOnlyList<string>? GetSubscribedPeers(string replicaPath);  // lookup only, no per-send auth
    bool TrySubscribe(string peerId, string pathPattern, string privilege, out string? denyReason);
    void UnsubscribeAll(string peerId);  // on disconnect
    // ...
}

// INetworkManager
IReadOnlyList<string> GetConnectedPeers();  // logical IDs, for broadcast fallback
void Send(string peerId, byte[] data, bool reliable = true);
```

**ReplicaManager change:** Permission checked at Subscribe; at send time only lookup:

```csharp
var recipients = _accessControlService?.GetSubscribedPeers(replica.UniquePath);
if (recipients != null && recipients.Count > 0)
{
    foreach (var peerId in recipients)
        _networkManager.Send(peerId, packet, reliable: true);
}
else
{
    // Fallback: broadcast to all connected
    _networkManager.Broadcast(packet, reliable: true);
}
```

### 3.3 Delta Serialization (Property-Level)

**Goal:** Send only changed properties in Update packets, not the full object.

**Approach:**

1. Extend `Replica.HasChanges()` to return which properties changed (or add `GetChangedProperties()`).
2. Add `ReplicaPacketBuilder.BuildDeltaPacket(replica, changedPropertyNames)`.
3. Add `ReplicaPacketType.DeltaUpdate` (or extend Update to support delta payload).
4. Deserializer applies only changed properties.

**Pros:** Smaller packets, less bandwidth.  
**Cons:** More complex serialization; need to handle partial updates on receiver.

### 3.4 Priority / Update Rate

**Goal:** Not all replicas need the same update rate.

- **High priority:** Player position, critical UI → 20–30 Hz.
- **Low priority:** Distant objects, ambient → 1–2 Hz.

**Approach:** Add `Replica.UpdatePriority` or `Replica.UpdateIntervalMs`; ReplicaManager.Tick() uses per-replica intervals instead of global tick rate.

### 3.5 Distance-Based LOD (Long-Term)

- Far replicas: lower update rate or coarser representation.
- Very far: stop sending until peer gets closer.

Requires spatial index and distance calculation.

---

## 4. Recommended Roadmap

| Phase | Mechanism | Effort | Impact |
|-------|-----------|--------|--------|
| **1** | Layer/channel subscription | Medium | Enables coarse filtering (world vs UI vs debug) |
| **2** | Path-based subscription | Medium | "Subscribe to /Root/Players/*" |
| **3** | Delta serialization (property-level) | High | Bandwidth reduction |
| **4** | Per-replica update rate | Low | Fine-grained control |
| **5** | Spatial/region subscription | High | Scale to large worlds |

---

## 5. Summary

| Current | Possible Extensions |
|---------|---------------------|
| Broadcast to all | Subscription (layer, path, replica ID) |
| Full object serialization | Delta (property-level) serialization |
| Single tick rate | Per-replica update rate |
| No spatial filtering | Region-based (AABB/sphere) subscription |

The subscription model is the main enabler for "don't send every change if not necessary." Layers and path-based subscription are the most practical first steps; spatial subscription can follow for larger worlds.

---

## 6. Config-Driven Path Subscription & Access Control

Path-based subscription, authentication, and authorization are handled by **ReplicaAccessControlService**. See **[NETWORKING.md#part-ii--replica-access-control-config](NETWORKING.md#part-ii--replica-access-control-config)** for the full schema.

**Quick reference:**
- `subscription.paths`: Path patterns (e.g. `["/Root/Players/*"]`) this instance subscribes to.
- `subscription.mode`: `include` (only these) or `exclude` (all except these).
- `authentication.rules`: Per-path whitelist/blacklist with `read`/`write` privileges.

---

# Part II — Replica access control config

This document defines the config-driven **ReplicaAccessControlService**, which handles:

- **Subscription** – which replica paths a peer cares about
- **Authentication** – identity (participant ID)
- **Authorization** – who is allowed to access what
- **Permissions / Privileges** – read, write, and future extensions

All replica access policies (subscription, auth, permissions) are configured via JSON and enforced before sending updates over the network.

---

## 0. Architecture & Integration

### 0.1 Where Does It Live?

**ReplicaAccessControlService** as **ICoreService** in **Ape.Core**:

- **Location:** `src/Ape.Core/Replication/ReplicaAccessControlService.cs`
- **Namespace:** `Ape.Core.Replication`
- **Scope:** Subscription, authentication, authorization, permissions, privileges – all replica access policies
- Same tier as ConfigManager, ReplicaManager, NetworkManager
- Always registered at startup
- When no config: **no-op** (allow all = broadcast, current behavior)
- When config present: filters who receives replica updates; enforces auth and privileges

```
Ape.Core (Tier 1)
├── Config/
├── Event/
├── Network/
├── Replication/
│   ├── ReplicaManager
│   ├── ReplicaPacketBuilder
│   ├── Replica
│   └── ReplicaAccessControlService   ← NEW: subscription, auth, permissions, privileges
├── Scene/
└── ...
```

### 0.2 Connection & Subscription Flow

**Key principle:** Permission is checked **once at Subscribe time**, not on every send.

```
1. Peer connects
   └─ Connection-level auth (optional) → accept / reject

2. Peer subscribes (explicit Subscribe command OR implicit from config at connect)
   └─ IReplicaAccessControlService.TrySubscribe(peerId, pathPattern, "read")
   └─ Check: HasPrivilege(peerId, path, "read") → if OK, add to subscription table
   └─ If deny → reject subscribe, optional event

3. ReplicaManager sends update for path P
   └─ GetSubscribedPeers(path)  ← lookup only, NO permission re-check
   └─ Send(peerId, packet) to each subscribed peer

4. Peer disconnects OR sends Unsubscribe
   └─ UnsubscribeAll(peerId)  ← remove from subscription table
```

**Performance:** No auth check on every packet – only a fast lookup at send time.

### 0.3 Who Uses It?

| Component | Role |
|-----------|------|
| **ReplicaManager** | Knows replica path; calls `GetSubscribedPeers(path)`; sends to each. On peer connect: implicit subscribe from config. On peer disconnect: `UnsubscribeAll`. |
| **ReplicaAccessControlService** | Subscription state table; permission check at Subscribe; `GetSubscribedPeers(path)` for send-time lookup. |
| **NetworkManager** | `GetConnectedPeers()` – all connected peers. `Send(peerId, data)` – transport. No replica knowledge. |

**Both GetConnectedPeers and GetSubscribedPeers are needed:**
- **GetConnectedPeers()** (INetworkManager): All peers currently connected (logical IDs). Used for broadcast fallback, connection management.
- **GetSubscribedPeers(path)** (IReplicaAccessControlService): Peers who subscribed to this path and passed auth. Used for filtered send.

**Peer ID type:** Use **string** (logical ID, e.g. LocalPeerId) for consistency with participantId and OwnerId. Transport may keep internal int; mapping layer converts.

### 0.4 Data Flow (With Filtering)

```
ReplicaManager.Tick() / Register() / Unregister()
    │
    ├─ replica.UniquePath = "/Root/Players/Alice"
    │
    ▼
IReplicaAccessControlService.GetSubscribedPeers(path: "/Root/Players/Alice")
    │
    ├─ Lookup subscription table (who subscribed to this path?)
    ├─ NO permission re-check – already validated at Subscribe time
    │
    ▼
Returns: [peerId1, peerId2]   or   null (= broadcast to all connected, backward compat)
    │
    ▼
foreach (peerId in recipients)
    NetworkManager.Send(peerId, packet, reliable: true)
// peerId: string (logical ID)
```

### 0.5 Interface Sketch

```csharp
// Ape.Abstractions
// peerId: string (logical ID, e.g. LocalPeerId from handshake) – aligns with participantId, OwnerId
public interface IReplicaAccessControlService
{
    /// <summary>
    /// Get peer IDs subscribed to this path. No permission re-check – already validated at Subscribe.
    /// Returns null to indicate broadcast (no filtering, send to all connected).
    /// </summary>
    IReadOnlyList<string>? GetSubscribedPeers(string replicaPath);
    
    /// <summary>
    /// Subscribe peer to path pattern. Permission checked HERE (once).
    /// Returns true if allowed; false if denied (with reason).
    /// </summary>
    bool TrySubscribe(string peerId, string pathPattern, string requiredPrivilege = "read", out string? denyReason);
    
    /// <summary>
    /// Unsubscribe peer from path pattern.
    /// </summary>
    void Unsubscribe(string peerId, string pathPattern);
    
    /// <summary>
    /// Remove peer from all subscriptions (call on disconnect).
    /// </summary>
    void UnsubscribeAll(string peerId);
    
    /// <summary>
    /// Check if a peer has the required privilege for a path (used by TrySubscribe; also for write validation).
    /// </summary>
    bool HasPrivilege(string peerId, string replicaPath, string privilege);
    
    /// <summary>
    /// Register peer. peerId = logical ID (e.g. LocalPeerId from handshake). Required before TrySubscribe.
    /// </summary>
    void RegisterPeer(string peerId);
    
    void UnregisterPeer(string peerId);
}
```

### 0.6 INetworkManager: GetConnectedPeers

```csharp
// INetworkManager (extend existing interface)
// peerId: string for consistency; transport may need internal int→string mapping
public interface INetworkManager
{
    // ... existing members ...
    
    /// <summary>
    /// Get all currently connected peer IDs (logical IDs). Used for broadcast fallback and connection management.
    /// </summary>
    IReadOnlyList<string> GetConnectedPeers();
    
    /// <summary>
    /// Send to peer by logical ID.
    /// </summary>
    void Send(string peerId, byte[] data, bool reliable = true);
}
```

### 0.7 Control Plane: Subscribe / Unsubscribe

Subscribe and Unsubscribe are **control plane commands** – either:
- **Packet types:** New `ReplicaPacketType.Subscribe`, `ReplicaPacketType.Unsubscribe` (path pattern in payload).
- **Handshake extension:** Client sends subscription paths in initial handshake; server applies them at connect.

**Implicit subscribe:** When peer connects with config, server can apply `subscription.paths` from config as initial subscriptions (with auth check).

### 0.8 Peer ID: int vs string (Design Decision)

**Current state:** Transport layer uses **int** (LiteNetLib `NetPeer.Id`, QuicTransport auto-increment 0,1,2...).

**Logical layer:** `LocalPeerId`, `participantId`, `OwnerId` are **string** (e.g. `"main-server-server-0-12345-abc-def"`).

| Option | peerId type | Pros | Cons |
|--------|-------------|------|------|
| **A) int** | Transport connection ID | Matches current transport; fast lookup | Two ID types: int for transport, string for auth |
| **B) string** | Logical ID (e.g. remote's LocalPeerId from handshake) | One ID type; aligns with participantId, OwnerId | Transport must map string→connection; breaking change |

**Recommendation:** Prefer **string** for the subscription/auth layer and long-term API:
- ReplicaAccessControlService: `TrySubscribe(string peerId, ...)`, `GetSubscribedPeers(path)` → `IReadOnlyList<string>`
- Transport keeps internal int; NetworkManager/ReplicaManager use string
- Handshake: client sends its LocalPeerId; server uses that as peerId (string) and maps to transport connection

**Migration:** If transport stays int for now, add `peerIdToString` / `stringToPeerId` mapping in NetworkManager or ReplicaAccessControlService. Later, transport can expose string directly.

---

## 1. Config Structure Overview

```json
{
  "subscription": {
    "paths": [
      "/Root/Players/*",
      "/Root/World/entities/*",
      "/Root/UI/scoreboard"
    ],
    "mode": "include"
  },
  "authentication": {
    "rules": [
      {
        "path": "/Root/path1/entity1/*",
        "whitelist": {
          "participantID1": {
            "privileges": ["read", "write"]
          },
          "participantID2": {
            "privileges": ["read"]
          }
        },
        "blacklist": ["participantID3", "participantID4"],
        "security": {
          "type": "ED25519",
          "publicKey": "publicKey1",
          "privateKey": "privateKey1"
        }
      }
    ]
  }
}
```

---

## 2. Subscription Section

**Purpose:** Declare which replica paths this instance cares about. Only replicas matching these patterns receive updates.

### 2.1 Schema

| Key | Type | Required | Description |
|-----|------|----------|-------------|
| `subscription.paths` | `string[]` | No | Path patterns to subscribe to. Empty or absent = subscribe to all (broadcast mode, backward compat). |
| `subscription.mode` | `"include"` \| `"exclude"` | No | `include` = only these paths (default). `exclude` = all except these paths. |

### 2.2 Path Pattern Syntax

| Pattern | Meaning | Example match |
|---------|---------|---------------|
| `/Root/Players/*` | Single wildcard – one path segment | `/Root/Players/Alice`, `/Root/Players/Bob` |
| `/Root/**` | Double wildcard – zero or more segments | `/Root`, `/Root/X`, `/Root/X/Y` |
| `/Root/UI/scoreboard` | Exact path | `/Root/UI/scoreboard` only |

**Matching:** Replica `UniquePath` is matched against each pattern. First match wins.

### 2.3 Examples

**Subscribe to players and world entities only:**
```json
{
  "subscription": {
    "paths": [
      "/Root/Players/*",
      "/Root/World/*"
    ]
  }
}
```

**Exclude debug objects:**
```json
{
  "subscription": {
    "paths": ["/Root/Debug/*"],
    "mode": "exclude"
  }
}
```

**No subscription config = broadcast (current behavior):**
```json
{
  "network": { "enabled": true },
  "modules": {
    "Ape.Module.Example": { "plugins": { "Demo": {} } }
  }
}
```

---

## 3. Authentication Section

**Purpose:** Per-path access control. Defines who can read/write which replicas.

### 3.1 Rule Schema

| Key | Type | Description |
|-----|------|-------------|
| `path` | `string` | Path pattern (same syntax as subscription). |
| `whitelist` | `object` | Map of `participantId` → `{ privileges: ["read","write"] }`. |
| `blacklist` | `string[]` | Participant IDs explicitly denied. |
| `security` | `object` | Optional: `type`, `publicKey`, `privateKey` for signing/verification. |

### 3.2 Privileges

- **read:** Receive Create/Update/Delete for replicas under this path.
- **write:** Authorize local changes (ownership, mutations) for replicas under this path.

### 3.3 Evaluation Order

1. If participant in `blacklist` → deny.
2. If participant in `whitelist` with required privilege → allow.
3. Otherwise → deny (or fallback per deployment policy).

---

## 4. Interaction: Subscription + Authentication

| Scenario | At Subscribe | At Send | Result |
|----------|--------------|---------|--------|
| Client subscribes to `/Root/Players/*` | HasPrivilege OK → add to table | GetSubscribedPeers → [peerId] | ✅ Receives updates |
| Client subscribes to `/Root/Players/*` | HasPrivilege deny → reject | - | ❌ Not in table, no updates |
| No subscription config | - | GetSubscribedPeers returns null | Broadcast to GetConnectedPeers() |
| Peer disconnects | - | UnsubscribeAll(peerId) | Removed from table |

**peerId:** string (logical ID, e.g. LocalPeerId from handshake).

**Server logic (ReplicaManager):**
1. On peer connect: RegisterPeer; optionally implicit subscribe from config (TrySubscribe for each path).
2. On send: `GetSubscribedPeers(replicaPath)` → if null, `Broadcast`; else `Send` to each.
3. On peer disconnect: `UnsubscribeAll(peerId)`.

---

## 5. Config Loading (ConfigManager)

### 5.1 Reading Subscription

```csharp
// In Program.cs or SubscriptionService
var subscriptionConfig = coreConfig?["subscription"];
var paths = subscriptionConfig?.GetStringArray("paths") ?? new List<string>();
var mode = subscriptionConfig?.GetString("mode", "include");

// If paths empty → broadcast mode (no filtering)
```

### 5.2 Reading Auth Rules

```csharp
var authConfig = coreConfig?["authentication"];
var rulesArray = authConfig?.GetArray("rules") ?? new List<ConfigNode>();

foreach (var rule in rulesArray)
{
    var path = rule.GetString("path");
    var whitelist = rule.GetObject("whitelist");
    var blacklist = rule.GetStringArray("blacklist");
    // ... build AuthRule structure
}
```

### 5.3 Path Matching Helper

```csharp
public static bool PathMatches(string replicaPath, string pattern)
{
    // Simple impl: * = one segment, ** = many
    // "/Root/Players/*" matches "/Root/Players/Alice"
    var patternParts = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var pathParts = replicaPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
    
    if (patternParts.Length > pathParts.Length && 
        patternParts[^1] != "**") return false;
    
    for (int i = 0; i < patternParts.Length; i++)
    {
        if (i >= pathParts.Length) return false;
        if (patternParts[i] == "*" || patternParts[i] == "**") continue;
        if (patternParts[i] == "**") return true; // rest matches
        if (patternParts[i] != pathParts[i]) return false;
    }
    return patternParts.Length == pathParts.Length;
}
```

---

## 6. Participant ID Mapping

**Question:** How does a peer get its `participantId` for auth lookup?

Options:
1. **Config:** `network.participantId` or `network.peerName` used as participant ID.
2. **Handshake:** Client sends participant ID during connection; server validates.
3. **Transport:** Use `LocalPeerId` / transport peer ID as fallback when no auth config.

**Recommendation:** Use `peerName` from config as `participantId` for auth. If absent, use `LocalPeerId`. Auth rules reference these IDs.

---

## 7. Example Full Config

```json
{
  "modules": {
    "Ape.Module.Example": { "plugins": { "Demo": {} } }
  },
  "network": {
    "enabled": true,
    "transport": "quic",
    "role": "client",
    "peerName": "participantID2",
    "host": "127.0.0.1",
    "port": 5000
  },
  "subscription": {
    "paths": [
      "/Root/Players/*",
      "/Root/World/entities/*"
    ],
    "mode": "include"
  },
  "authentication": {
    "rules": [
      {
        "path": "/Root/Players/*",
        "whitelist": {
          "participantID1": { "privileges": ["read", "write"] },
          "participantID2": { "privileges": ["read"] }
        },
        "blacklist": []
      },
      {
        "path": "/Root/World/*",
        "whitelist": {
          "participantID1": { "privileges": ["read", "write"] },
          "participantID2": { "privileges": ["read"] }
        }
      }
    ]
  }
}
```

---

## 8. Implementation Phases

| Phase | Scope | Deliverable |
|-------|-------|-------------|
| **1** | Core service | Add `ReplicaAccessControlService` in `Ape.Core/Replication/`; implement ICoreService; register before ReplicaManager. |
| **2** | GetConnectedPeers | Add `GetConnectedPeers()` to INetworkManager. Return `IReadOnlyList<string>` (logical IDs). |
| **2b** | peerId mapping | Transport uses int internally; add mapping layer (transport int ↔ logical string) if needed. |
| **3** | Subscription table | In-memory table: peerId (string) → list of path patterns. TrySubscribe, Unsubscribe, UnsubscribeAll. |
| **4** | Path matching | Robust `PathMatches(replicaPath, pattern)` with `*` and `**`. |
| **5** | GetSubscribedPeers | For replica path P, return peerIds whose subscribed patterns match P. |
| **6** | ReplicaManager integration | Replace Broadcast with GetSubscribedPeers + Send loop; null = Broadcast to GetConnectedPeers. |
| **7** | Auth at Subscribe | HasPrivilege; TrySubscribe checks auth before adding to table. Load `authentication.rules`. |
| **8** | Peer mapping | RegisterPeer(peerId, participantId) on connect; handshake or config. |
| **9** | Control plane | Subscribe/Unsubscribe packet types or handshake extension. |
| **10** | Security | ED25519 signing/verification (optional, later). |

---

## 9. Summary

| Concept | Purpose |
|---------|---------|
| **GetConnectedPeers()** | All connected peers (INetworkManager). Returns string IDs. Broadcast fallback. |
| **GetSubscribedPeers(path)** | Peers subscribed to path, auth checked at Subscribe (IReplicaAccessControlService). Returns string IDs. |
| **TrySubscribe** | Permission check once; add to subscription table. |
| **UnsubscribeAll** | On disconnect; remove from table. |
| `subscription.paths` | Path patterns for implicit subscribe at connect. |
| `authentication.rules` | Per-path whitelist/blacklist; checked at Subscribe. |

**Flow:** Connect → Subscribe (auth check once) → Send to GetSubscribedPeers (lookup only) → Disconnect/Unsubscribe.

---

# Part III — Large file chunk transfer (CAS)


## Overview

The `FileChunkTransfer` system provides automatic chunk-based transfer of large files (meshes, textures, audio) across the network. Files are split into chunks, transferred via `INetworkTransport`, and stored in content-addressed storage (CAS) for automatic deduplication.

**Storage:** chunk bytes live on **disk** (`FileChunkStore` / `IChunkStore`), not an in-memory database. RAM is for manifests, peer availability, and in-flight buffers only.

## Architecture

### Key Components

1. **`[FileChunkTransfer]` Attribute** - Decorator marking properties requiring chunk transfer
2. **`INetworkTransport`** - Transport layer with chunk transfer methods (QUIC, LiteNetLib)
3. **`IChunkStore`** - Content-addressed storage for chunks (SHA256 hashing)
4. **`ILargeFileTransferService`** - High-level API for upload/download operations
5. **`ApeReplica`** - Auto-detection logic for `[FileChunkTransfer]` properties

### Protocol

All chunk messages use a binary protocol over `INetworkTransport`:

**Message Types:**
- `0` - Normal app data on a **persistent unidirectional stream** (Phase C): first byte `0`, then repeating `[Length:4 LE][Payload:N]` until the stream closes. `SendReliable` and `SendUnreliable` each keep a dedicated outbound stream per peer (unreliable is still a QUIC stream today — `System.Net.Quic` has no datagram API yet).
- `1` - FileChunk (one-shot stream): `[MessageType:1][FileId:16][ChunkIndex:4][DataLength:4][Data:N]`
- `2` - ManifestRequest (one-shot): `[MessageType:1][FileId:16]`
- `3` - ChunkRequest (one-shot): `[MessageType:1][FileId:16][ChunkIndex:4]`
- `4` - ManifestResponse (one-shot): `[MessageType:1][FileId:16][TotalChunks:4][TotalSize:8][ChunkHashCount:4][ChunkHashes:N*64]`

> **Breaking (Phase C):** peers must both run the framed type-`0` protocol. Pre-C one-stream-per-message `[0][payload][FIN]` is no longer accepted.

## Usage Example

### 1. Mark Property with `[FileChunkTransfer]`

The `Geometry` class in `Geometry.cs` already has `[FileChunkTransfer]` attributes:

```csharp
using Ape.Abstractions;
using Ape.Abstractions.Attributes;
using MessagePack;

[MessagePackObject]
public class Geometry : Node
{
    private string _meshPath = string.Empty;
    private string _materialPath = string.Empty;
    
    [Key(10)]
    [FileChunkTransfer(ChunkSize = 64 * 1024, MaxParallelChunks = 4, AutoDownload = true)]
    public string MeshPath
    {
        get => _meshPath;
        set => SetProperty(ref _meshPath, value); // Auto-triggers chunk transfer!
    }
    
    [Key(11)]
    [FileChunkTransfer(ChunkSize = 64 * 1024, MaxParallelChunks = 4, AutoDownload = true)]
    public string MaterialPath
    {
        get => _materialPath;
        set => SetProperty(ref _materialPath, value); // Auto-triggers chunk transfer!
    }
    
    [Key(12)]
    public bool CastShadows { get; set; }
}
```

### 2. Set Property to Trigger Transfer

```csharp
var geometry = new Geometry();
geometry.MeshPath = "/path/to/large_mesh.obj"; // Automatically uploads chunks!
geometry.MaterialPath = "/path/to/material.mat"; // Also triggers chunk transfer!
```

### 3. Auto-Detection Flow

When `MeshPath` or `MaterialPath` changes:
1. `SetProperty()` is called by the setter
2. `OnPropertyChanged()` is invoked by `SetProperty()`
3. `ApeReplica` detects `[FileChunkTransfer]` attribute via reflection
4. `TriggerLargeFileTransferAsync()` is invoked
5. `ILargeFileTransferService.UploadFileAsync()` splits file into chunks
6. Chunks are sent to all connected peers via `INetworkTransport.SendFileChunk()`
7. Peers store chunks in `IChunkStore` (content-addressed storage)

## FileChunkTransfer Attribute Properties

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ChunkSize` | `int` | `64 * 1024` (64KB) | Size of each chunk in bytes |
| `MaxParallelChunks` | `int` | `4` | Maximum parallel chunk transfers (Phase 2) |
| `AutoDownload` | `bool` | `true` | Auto-start download when property set remotely (Phase 2) |

## Content-Addressed Storage (CAS)

Chunks are stored using SHA256 hash as identifier:

**Storage Structure:**
```
{baseDir}/chunks/{hash[0..2]}/{hash}.chunk
```

**Example:**
```
chunks/ab/abc123...def456.chunk
chunks/cd/cdef78...901234.chunk
```

**Benefits:**
- Automatic deduplication (same chunk stored once)
- Integrity verification (hash matches content)
- Efficient storage (shared chunks)

## Phase Implementation

### Phase 1 (Current) ✅
- Sequential chunk transfer (0, 1, 2, 3...)
- Single peer selection (always use first peer)
- QUIC and LiteNetLib transport support
- Content-addressed storage (SHA256)
- Auto-detection via `[FileChunkTransfer]` attribute

### Phase 2 (Future) — content-addressed swarm (optional)
- Rarest-first chunk selection (torrent-style piece picking, not BitTorrent wire)
- Fastest peer selection (prefer low-latency peers)
- Parallel chunk downloads (`MaxParallelChunks`)
- Resume capability for interrupted transfers
- Bandwidth throttling
- Optional wire band `0x11`–`0x1F` aligned with QUIC chunk message types 1–4

### Phase 3+ (Future)
- NATS JetStream integration
- Redis metadata caching
- CRDT conflict resolution
- Global file registry

## Integration with SceneGraph

The SceneGraph objects (Node, Geometry, Light) are in separate files (`Node.cs`, `Geometry.cs`, `Light.cs`) and extend `ApeReplica`, maintaining Single Source of Truth:

```csharp
// SceneGraph object
var geometry = new Geometry 
{ 
    Name = "MyMesh",
    Position = new Vector3(0, 0, 0)
};

// Setting MeshPath triggers chunk transfer
geometry.MeshPath = "/path/to/mesh.obj";

// Property change event fired via SetProperty()
// EventManager receives ApePropertyChangedEvent
// Chunks automatically uploaded to peers

// MaterialPath also supports chunk transfer
geometry.MaterialPath = "/path/to/material.mat";
```

## Transport Independence

The system works with any `INetworkTransport` implementation:

**Supported Transports:**
- ✅ `QuicTransport` - QUIC streams (TLS 1.3, reliable)
- ✅ `LiteNetLibTransport` - UDP with reliable delivery

**Adding New Transports:**
Implement `INetworkTransport` chunk methods:
- `SendFileChunk(peerId, fileId, chunkIndex, data)`
- `RequestFileManifest(peerId, fileId)`
- `RequestFileChunk(peerId, fileId, chunkIndex)`
- Fire events: `OnFileChunkReceived`, `OnFileManifestReceived`, `OnFileChunkRequested`

## Performance Considerations

**Chunk Size:**
- Smaller chunks (16KB): More overhead, better progress tracking
- Larger chunks (256KB): Less overhead, faster transfer, coarser progress
- **Recommended:** 64KB (good balance)

**Deduplication:**
- Identical chunks stored only once (automatic via SHA256)
- Mesh variants with shared geometry save storage
- Example: 10 variants of same base mesh = 1x storage + deltas

## Future Enhancements

- [ ] Chunk integrity verification (per-chunk SHA256 check)
- [ ] Transfer resume after disconnect
- [ ] Bandwidth throttling (bytes/sec limit)
- [ ] Priority-based chunk selection
- [ ] Compression (LZ4, Zstd) before chunking
- [ ] Manifest caching (avoid re-requesting metadata)
- [ ] Garbage collection (delete unused chunks)

## Troubleshooting

**Problem:** Chunk transfer not triggered
- **Solution:** Ensure property has `[FileChunkTransfer]` attribute
- **Solution:** Verify `OnPropertyChanged()` is called in setter
- **Solution:** Check `LargeFileTransferService` is injected into `ApeReplica`

**Problem:** File not found error
- **Solution:** Verify file path is absolute and file exists
- **Solution:** Check file permissions (read access)

**Problem:** Chunks not received on remote peer
- **Solution:** Ensure `INetworkTransport` is connected (peers exist)
- **Solution:** Check `OnFileChunkReceived` event is subscribed
- **Solution:** Verify transport supports chunk methods (QUIC/LiteNetLib)

## Related Documentation

- [ARCHITECTURE.md](ARCHITECTURE.md) - Overall system design
- [ARCHITECTURE.md#scene-entity-migration](ARCHITECTURE.md#scene-entity-migration) - Scene entities and registry
