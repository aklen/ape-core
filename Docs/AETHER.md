# Aether reducer

Normative for the code under `Aether/`. Trees and checkpoints are not implemented here. Local save is outside the frame. One registered entity type can be created and its label can travel on the network. Working notes outside this repository are not the contract.

## What this slice does

One reducer folds writes from any peer. A frozen frame calls that same reducer. Tests call it directly, through the frame, and through several peers that only call this reducer and the archive. They assert handwritten results. The peers do not resolve on their own. Each new simulator operation takes the next sequence for its actor. During a partition both peers can write. Merging those snapshots in either order keeps distinct fields and each writer's contribution. A tombstone and a withdraw stay in place. That merged state can be saved, loaded, and followed by a new local write without losing the clock or the actor sequence.

A record key is `(entityId, writerId, fieldId)`.

| Resolver | Store | View |
| --- | --- | --- |
| `lww` | One value per writer, with version `(lamport, actorId)`. | The greatest version across writers. |
| `sumContributions` | One raw fixed-point contribution per writer. | Sum in ordinal `writerId` order, then clamp to the field minimum and maximum. |

A greater Lamport wins. Equal Lamport keeps the greater `actorId`, compared as raw UTF-8 bytes. A writer's newer contribution replaces their own record. It does not add another term.

The dedup window holds a bounded number of recent operation ids and their digests. A digest is the SHA-256 of a length-prefixed, type-tagged encoding, written as 64 hexadecimal characters. Strings are UTF-8 with a 4-byte big-endian length. A fixed-point value and a label stay distinct, including zero and an empty label. The window stores that hash. While an id is in the window, the same digest is a no-op and a different digest is rejected. The store does not change on either result.

An operation copies its field values into a read-only map before it hashes them. A later edit of the caller's dictionary leaves the operation and its digest unchanged.

A sum field accepts a fixed-point contribution. An LWW field accepts a label. The other shape is rejected before any record, clock, or digest changes. Defining a field twice is rejected. A minimum above the maximum is rejected.

Dropping an id from the window is local cache cleanup. It does not close that operation, and it does not close any other operation with a smaller Lamport. A smaller Lamport can still be the first write of another field or another writer. Both arrival orders of such a set produce the same field view.

Once an id has left the window, the reducer no longer knows whether a later envelope with that id is new or already folded. `Remembers` is false. An unchanged replay still does not change the stored contribution or a sum: the field version is equal or older, so the record stays. A rewritten payload for that same id is not detected in this slice.

A later checkpoint proves which operations its coverage includes. It can reject an id already inside that coverage without storing the original digest. Coverage is not a comparison against a forgotten payload, so it does not by itself show that the payload was rewritten.

`Remembers` is digest memory. It is not a delta. A sender may return one operation only when `CanReplay` is true, which means the original payload is still stored. This slice stores digests only, so `CanReplay` is false for every id, including one still in the window. A replay request is answered with the current snapshot from `Capture`. Window capacity does not start that answer. This slice has no transport, so it only exposes the two predicates.

`StampLocal` assigns the next Lamport by incrementing the local clock, and a per-actor sequence. It is not a received operation. A local write whose actor id is empty, whose fields do not match the schema, or whose admission the reducer would reject, is rejected before that clock or sequence changes. `Apply` checks an actor sequence of at least 1 before it changes the clock, the records, or the frontier, then records that sequence with its Lamport and operation id. The same sequence is accepted only with that same Lamport and operation id. A greater sequence needs a greater Lamport. A missing actor id or a sequence below 1 is rejected before any change. A later sequence-only restore does not replace a frontier already recorded. An actor sequence already at `long.MaxValue` is rejected before the clock or the sequence changes. `Apply` returns that operation paired with the clock after the call, and with its place in the save sequence. A later write does not change the pair. The save sequence is not a Lamport value. `Apply` with clock observation, on a newly admitted operation, sets:

```text
clock = max(clock, receivedLamport) + 1
```

That includes a received Lamport that is already behind the local clock. The addition is rejected when the clock is already `long.MaxValue` or the received Lamport is `long.MaxValue`. Rejection happens before the clock, the window, or any record changes. `StampLocal` uses the same check. An exact duplicate still in the window does not move the clock. An evicted id is no longer a duplicate, so a replay observes the clock again, and the field version still decides the stored value. The clock is not part of the resolved-view convergence claim. Field versions are. Two peers that receive the same new operations in a different order can stamp their next local write at different clocks.

## Snapshot

`Capture` returns the clock, the raw records, the dedup window, tombstones, visibility, and publication membership. It does not return a resolved sum or a rendered label.

`MergeImage` checks the whole image before it changes the clock, the window, or any record. That check includes a digest mismatch, a value of the wrong shape, an unknown field, and every `(key, version)` collision against the store or against another row in the same image. Visibility and membership use that same rule. A higher row does not hide a conflict on a lower version. Row order does not change the result. A rejected image leaves the reducer unchanged. After the check, `MergeImage` raises the clock to the image clock when that is greater. It does not add one to the restored clock. It then writes the highest version of each key from that same checked set, unions tombstones, and unions the window, trimming back to capacity. A tombstone is only added. An image that lacks one does not clear it. A newer value wins whether it arrives in the image or in a later operation. The destination must already define every field in the image. An id still in either window keeps the same-digest rule after the merge.

## Lifecycle

`CreateEntity` stores one immutable creation record for the natural `entityId`: registered `entityTypeId`, `creatorId`, initial `ownerId`, version, operation id, and actor sequence. The creator is the principal that issued Create, independent of when peers first see the entity. In this slice the initial owner and writer must be that same authenticated principal. A second envelope may repeat those facts, but a different type, creator, or owner for the same entity id is rejected before mutation. Ownership transfer is not implemented, so the stored owner does not change. An Update that arrives before Create may remain as raw field state, but it does not create a scene object. Once the valid Create is present, the frame emits the entity creation before its field projection.

`DeleteEntity` stores a tombstone for that `entityId`. A later field write, `HideShared`, `RestoreShared`, `WithdrawPublication`, `Publish`, or an older snapshot leaves the tombstone in place. The raw field values stay. The shared view does not.

`HideShared` and `RestoreShared` are one visibility record per entity. The greater `(lamport, actorId)` wins. Equal version with the opposite flag is rejected before any change. Hiding leaves the raw contribution and the sum in the store. With no visibility record, the entity is shared-visible.

`Publish` and `WithdrawPublication` are one membership record per `(entityId, publicationId)`. The greater version wins. Republishing is a `Publish` with a higher version. A snapshot taken while the publication was a member does not undo a later withdraw. Withdrawing one publication leaves the others, the fields, and the entity.

This slice authenticates the initial Create claim. It does not yet authorize delete, hide, membership, or owner transfer. `writerId` on those other operations is whoever built the operation.

## Frame

`AetherFrame.Compile` freezes three stages, `apply`, then `resolve`, then `commit`, on one host tick. Apply and resolve call the reducer. The frame does not grow per entity. Disk and sockets are outside the tick.

`AetherHost` is the input queue in front of that tick. Every pending request costs at least one byte, and the queue also has a maximum number of requests. Both limits include a request already drained and not yet committed. `TryAcceptLocal`, `TryAcceptRemote`, and `TryAcceptContribution` either queue the request and return a new request id, or refuse it because the queue is full. Full is not a protocol rejection, it does not take a request id, and the reducer stays unchanged. `Drain` then reports every request it folded. A call can also stop at an operation budget or a byte budget. The next request stays queued for a later call. A request already in the queue is still folded when it is the first request of that call, even if it alone is larger than the byte budget, so one accepted request cannot stall. A protocol rejection names its reason and is raised before the clock or the actor sequence changes. The next request is still folded when the budget allows it. A rejection spends budget the same way an applied write does. Any other exception stops the drain and leaves that request in place. Results already produced by that call ride on `AetherInterruptedException` instead of disappearing with the throw. Those lists are fixed copies, and the inner exception is the original failure. A later drain repeats a request only when that request has not changed the reducer. A local stamp that already moved the clock is applied on retry, not stamped again. An applied result means the reducer took the write. It does not mean the archive has it. `Commit` appends the outbox and returns those request ids as durable. A request id is durable only after that append returns. If a later append throws, the ids already appended ride on the same exception, and the failed request stays in the outbox. An empty partial list does not wrap the original exception. `Save` covers that same outbox with the snapshot and returns those request ids as durable. The host does not keep the results after the call returns. One call reports at most as many as the queue's count cap, and it does not grow a second log. A remote operation that remains is applied with clock observation, then its actor sequence is restored. A contribution merge is folded on that same drain. The same key and version must carry the same value, operation id, and sequence. A mismatch is rejected in either order. Only a record that is stored moves that actor's cursor. An unchanged merge is a `NoChange` result: it leaves the queue, it does not change the reducer, and it is not appended. A merge that stores a newer record does not build an operation and does not enter the dedup window. `Commit` appends that changed merge as its own record. A local write that remains is stamped and applied without observing the clock again. `Save` and `Commit` are the only calls that touch the archive, and `Drain` does not call them.

When a host is attached, `apply` calls that host's `Drain` with the frame operation budget and byte budget. It keeps that call's outcomes on the scratch and replaces them on the next tick. It does not also fold the inbox list, and it does not call `Commit` or `Save`. Without a host, `apply` still folds the inbox list in that one tick and leaves the outcome list empty. It skips a missing actor sequence, and a protocol rejection, before the actor cursor moves. `commit` is the only stage that writes the scene. It clears its commit list first. With a scene entity key and a creation record, it first ensures the registered scene entity exists with that type and owner. A conflicting existing type or owner is an error. It then copies the resolved label and sum onto the named entity properties. A blank shared view is written as an empty label and a zero sum. With no scene entity key the list stays empty, so an earlier frame's requests are not written again. It does not touch the archive. `AetherHost.Commit` stays the durable log, and the caller runs it outside the tick.

The resolve stage keeps three answers apart. `ResolvedDeleted` is the tombstone. `ResolvedSharedVisible` is the entity Hide/Restore record. `ResolvedMember` is membership of the selected `PublicationId`. The shared label and sum require a publication id, membership in it, a living entity, and shared visibility. A missing or empty `PublicationId` leaves that shared output blank. It does not show every field. Raw fields stay in the reducer.

## Local save

`AetherArchive` stores one binary snapshot and a short recovery log beside the frame. `Save` captures the raw reducer: schema, clock, records, dedup window, creation records, tombstones, visibility, membership, and actor frontiers. A frontier is the greatest sequence that actor has shown, with that sequence's Lamport and operation id. It does not store a resolved sum or a rendered label, and it does not change membership or open a publication epoch.

The new bytes are flushed to a temporary file. The current file is replaced only after that flush. A temporary file left behind is not loaded. The snapshot and the recovery log both carry format version `6`, type tags, and a SHA-256 over the header and another over the body. The header hash covers the generation. Versions `1` through `5` are rejected. Version `4` stored an actor cursor as an actor id and a sequence only. Version `5` added the Lamport and operation id beside it. Version `6` adds immutable creation records and creation metadata to operation log entries. A truncated snapshot or a bad checksum is rejected. Each stored field keeps the actor on its version and the sequence beside the record. The sequence is not read back from the operation id.

After the snapshot is in place, the log for the previous generation is replaced. The snapshot records how far along the save sequence it covers. An application already inside that coverage is not written to the new log, even if its clock is the clock from that earlier application. Coverage is not decided by Lamport. A later application with an older Lamport is still logged. After that coverage, the log accepts only the next save sequence. A gap is rejected before the file changes, so the previous save still loads. A sequence that is already durable is not written again. A log is ignored only when its header checksum matches and its generation differs, so an older tail cannot move the clock or the fields. A bad header is rejected. Matching log records are applied in order through the same reducer. Each record stores the operation together with the clock returned for that same application, or the contribution fields when the request was a merge. The clock is not read again when the record is written, so a later write cannot replace it. Load applies an operation record without treating it as a newly received Lamport, and merges a contribution record without building an operation, then restores that clock. A record whose clock is behind the restored reducer is rejected. A torn final record is dropped on load. The writer checks the log and cuts a torn tail back to the last complete record when it opens that log, and again when the generation changes. It then remembers the valid end, so later records append there without rereading the earlier log. If the file length no longer matches that end, the writer repairs before appending. A finished record is not written again. A torn tail is cut back. A throw from the write or the flush does not by itself show how many of those bytes reached disk. A complete record found while repairing is flushed before it counts as durable. Reading it is not enough. A checksum failure on a complete record is rejected. Reading a generation uses the fixed header, not the rest of the file.

`Load` builds a fresh reducer. It does not merge into a live one. The frame does not read or write these files.

## Replacing Replica sync

The code stays under `Aether/` until the current Replica sync path is gone. A rename can wait until then. The engine name and the module name do not have to match.

```text
Existing Ape scene objects
        ↕
Aether: field versions, membership, lifecycle, media references
        ↕
Network: state sync, resumable file transfer, live audio and video
```

Scene commit stays the boundary that applies a resolved value. The contribution store stays too. It holds each writer's versioned value, which is not the displayed object.

Replacing Replica sync does not remove the other two paths. They stay beside the state engine:

| Path | What it carries |
| --- | --- |
| State sync | Scene fields, versions, membership, lifecycle, and references to media. |
| File transfer | A full or partial download, chunks, a checksum, and resume after a break. |
| Live byte stream | Audio and video, with ordering, buffering, delay, and rules for dropping data. |

A video object records which media it uses, who may read it, and any durable playback state. The file service carries the bytes. The stream path carries live frames. Those payloads do not enter the reducer, the dedup window, or the scene log. Those stores keep an identifier, a hash, and the metadata the field needs.

The same network gives each path its own limit and schedule. A large file copy must not starve a scene update or live audio. A file asks again for the missing pieces. Live media drops samples that are already too old.

New work serves this replacement and writes those same scene objects. It does not grow a second scene graph.

A field or entity that has moved is handled only here. The old deserialize path must not write it. Projecting a resolved value into the scene is not a local edit, so that write must not be sent again.

A current Create, Update, or FullSync blob is not an operation. It does not carry field versions, the original writer, or the rule version. Those travel in a versioned wire format. The receiver does not invent them from arrival order.

A delete on the old path must not drop an entity this reducer handles unless the reducer also stores the tombstone. A subscription may still filter what the transport delivers. It does not grant publication membership or authority.

The label and `CreateEntity` use the current wire. A local request is queued on the host. After the frame drains it, wire version `1` carries either the label's rule, writer, actor, sequence, Lamport, and text, or Create's registered type, creator, and initial owner. A checksum covers the packet. The receiver does not invent a missing version from arrival order. A version it does not know is refused and is not queued. Create is admitted only after the signed connection identifies the same principal as writer, actor peer, creator, and owner. The receiver's host queue then takes the operation. The frame resolves it and the scene commit first creates the object, then writes its property. That write is not submitted again. A full queue returns a full result, for a local edit and for a packet that does not fit, and the refused state is not treated as sent. An applied operation waits in a bounded outgoing queue until `Broadcast` returns. A throw leaves it there for the next pump. That return means the transport took the packet, not that the other peer applied it. Stopping the link unsubscribes it, so a later packet does not enter the old queue.

A broken connection does not drop the label or this peer's own contribution. While the peers are apart, both may edit. Reconnect, on that same link, sends that peer's own current contribution for this label, not another writer's, and not an older one it has replaced. A contribution is remembered only after the reducer has applied it. The same packet is used. The greater version wins on both sides. An older contribution does not replace it. If the receiver's host queue is full, that contribution stays held and the next pump tries again. If the outgoing queue has no room, the missed own contribution stays due and a later pump sends it. A later reconnect can send the current contribution again. Catch-up advances only while the connection works, a pump runs, and a slot frees. A packet dropped because the held queue is full is not kept. It arrives only when the sender transmits it again.

Restart is a different case. Loading the reducer does not rebuild the link. A new link does not rebuild an operation. It sends a contribution packet for this peer's own saved label. The receiver queues that packet on the host. The frame drains it under the same operation and byte budgets. `Commit` appends the merge as its own record. Load merges that record. It does not admit an operation, and it does not put a digest in the window. A write that was not applied is not in the save, so the new link cannot send it. An operation is resent only when this process still holds the original envelope. A later field value stays in its own record, so catch-up sends both current values without a history and without reusing the old operation id for a smaller payload.

A contribution merge also checks an actor frontier, separately from which field wins. Each actor keeps the greatest sequence it has shown, together with that sequence's Lamport and operation id. The same sequence is accepted only with that same operation id and Lamport. A greater sequence must carry a greater Lamport. A field can lose the version race and still move the frontier. A claim that breaks those rules is rejected before the clock, the records, or the frontier change. The snapshot stores the frontier beside the actor sequence. A changed merge record keeps every claim that proved it, including a field that was not stored, so a later load does not hand that sequence out again. A packet is admitted only when `writerId` and the actor's peer are the principal bound to the connection that delivered it. The actor is a length-prefixed principal and session, so one principal cannot pass as a prefix of another. A contribution may carry only that principal's own records. Relay is refused before the request is queued. Direct apply, archive replay, and a load of already accepted state do not ask the socket again. The socket slot only addresses the current connection. LiteNetLib uses the local peer slot. Quic uses an incrementing slot, and a client names the server `0`. On connect, each side sends a fresh nonce and its public key. The process instance id stays separate and can change on every start. The receiver binds the principal to the slot after the signature checks, and the binding does not change for the life of the connection. A later connection may bind the same principal after the previous slot is gone. The profile stores a key pair, and one process holds that profile. One profile is that whole configuration directory: two configuration files in the same folder share it, so the second process stops. The principal id is the SHA-256 fingerprint of the public key. The file carries a magic, a version, and a checksum. A damaged file does not open. Format version 2 does not open a version 1 file: that random id is not this key's fingerprint, so a new profile is required. The key file is readable only by its owner, and the profile directory is private before the key is written. The temporary key is flushed, then renamed, and the profile directory is flushed so the new name survives power loss. A recovered temporary file is opened exclusively, checked as a canonical P-256 key, flushed again, and only then renamed. An existing principal is accepted only after that directory is flushed again. The parent of the profile directory must already exist. Open may create the profile directory, and it flushes that parent before it accepts the principal. The checksum shows integrity, not secrecy. A handshake public key must be the canonical P-256 encoding, with no extra bytes. Both sides exchange a fresh nonce and sign one versioned transcript. `IsAuthenticated` becomes true only after that signature checks. A remote state packet is admitted only from an authenticated peer. An unauthenticated claim is a development switch and stays off unless the caller asks for it. A bad signature, a replay, a reflection, a key that does not match the principal, or an expired handshake is dropped before the Aether queue. A replay fails because the transcript includes this connection's fresh nonce. A handshake send that the transport does not accept stays pending as the same bytes. The proof shows that the same key is present. Initial Create uses that binding only for self-creation; owner transfer and delete authorization are separate. A state packet larger than the protocol maximum, and a contribution count the remaining bytes cannot hold, is rejected before a field array is built. Packets waiting for that binding are capped by count, by bytes, and by time.

Still not on this packet:

- Deletion
- Owner transfer and authority after initial authenticated creation
- Subscription and publication

## Guarantee and fault model, version 1

This version names the label path and authenticated initial creation. A guarantee holds only for the fault named beside it. Anything else is a refusal or a separate contract. Sensor samples, machine-control commands, and critical configuration are not this version.

| Word | Means | Does not mean |
| --- | --- | --- |
| Queued | The host accepted the request into its bounded queue. | The reducer has changed. |
| Applied | The reducer took the write. | The archive has it, or another peer has it. |
| Durable | `Commit` or `Save` returned that request id. | A peer received it. |
| Handed off | `Broadcast` returned. | The other peer queued, applied, or executed it. |
| Remotely queued | The receiver's host accepted it. | It has been applied or executed. |
| Executed | A command's effect was confirmed by its own contract. | A result this version can produce. |

| Fault | Label path | Refusal |
| --- | --- | --- |
| Connection loss | The same link keeps the label and its own applied contribution. Reconnect sends that contribution again. | The sender does not learn that the other peer applied it. This row is not a restart. |
| Delay and reordering | The greater `(lamport, actorId)` wins. The same valid writes converge in either order. | Arrival order is not the rule. |
| Duplication | The same id and digest, while the id is in the window, leaves the store unchanged. | After the id leaves the window, a rewritten payload is not detected. |
| Overload | Catch-up advances only while the connection works, a pump runs, and a slot frees. A contribution held for a full host queue, and a due own send, are tried on a later pump. | A packet dropped because the held queue is full is not kept. It arrives only when the sender transmits it again. A refused label is not treated as sent. |
| Restart | `Load` rebuilds a reducer from the snapshot and the log. A new link sends this peer's own saved records. The receiver merges them. The actor and the sequence travel on the record. | `Load` does not rebuild the link. The packet is not an operation, and it does not enter the dedup window. An operation is resent only from an envelope this process still holds. A write that was not applied is not in the save, so it cannot be sent. An applied result is not durable until `Commit` or `Save` returns. A torn tail is dropped. |
| Corrupt data | A bad checksum, an unknown wire or archive version, or the wrong field shape is rejected before the clock, the queue, or a record changes. | The packet is not repaired into a write. |
| Unauthorized sender | A direct packet is queued only when its writer and actor peer equal the durable principal bound to that connection. Initial Create additionally requires that principal as creator and owner. Anything else is refused before the queue, the clock, the frontier, or a record changes. | The principal is the fingerprint of the profile's public key, not the process instance and not a granted role. One process holds that profile. One profile is the whole configuration directory. The file has a magic, a version, and a checksum, and a damaged file does not open. `IsAuthenticated` is true only after the signed handshake. The self-create rule does not grant owner transfer or delete authority. The socket slot only routes. Relay is refused. A load does not re-check the socket. |

| Data | Contract it needs | This version |
| --- | --- | --- |
| Shared scene state | Convergent resolution, offline edits, and catch-up. | The label rows above. |
| Sensor sample | Source, order, a validity interval, quality, and freshness. | Not started. A Lamport value does not show that a sample is still fresh. |
| Machine-control command | An authorized issuer, a deadline, replay protection, and an execution acknowledgement. | Not started. The greater actor id is not that decision. Transport handoff is not execution. |
| Critical configuration | Checked, coordinated activation, with several fields changing together when the configuration requires it. | Not started. One frame is not that transaction. |

The bounds this version names are the host byte cap and count cap, the frame operation budget and byte budget, the outgoing and held-catch-up count caps, and the dedup-window capacity. Tests exercise those caps. Memory, message size, processing time, and overload behavior on the target hardware are not measured here.

A claim is accepted only when the requirement, the invariant, the code, and a test name the same fault and the same result. The evidence for this version is those handwritten tests. Model checking, fuzzing, and fault injection are not evidence yet. A green suite does not extend a guarantee past the tests that name it. Suitability for a safety-critical system needs system-level evidence and an independent review. This version does not claim that review.

While communication is uncertain, the label path keeps the last applied local state, refuses a corrupt or unknown packet, and retries a full queue. It does not invent a command fallback. A control system names its own predetermined behavior before a command exists. That behavior is outside this version.

Resending an operation sends the original envelope unchanged. Catching up state sends the current records, each with its own version and with the actor and sequence stored beside that record. Neither send needs a full history. The label link still catch-up sends its one field. The contribution packet can carry every current field of one writer.

## What this slice does not do

- Relay of another writer is refused. A direct packet binds `writerId` and the actor peer to the durable principal claimed for that connection, before the queue. That principal is the fingerprint of a profile key, held by one process. One profile is the whole configuration directory. The file has a magic, a version, and a checksum, and a damaged file does not open. The process instance id and the socket slot stay separate. `IsAuthenticated` is true only after both sides sign a fresh transcript. A failed handshake does not enter the queue. Initial self-create is bound to that principal; owner transfer and delete authority remain unsupported. A local write is rejected before it is applied when its fields would not fit in one contribution packet, or when its one label would not fit in one state packet. The encoded size is counted before the payload is copied. A packet or contribution count past the protocol maximum is rejected before the field array is built. The scene commit projects the resolved view. Disk save stays outside the tick. Sensor samples, machine-control commands, and critical configuration are outside guarantee and fault model version 1.
- No retired-id table outside the snapshot. A load installs the tombstones the snapshot holds.
- No owner, delete, or hide lists, policy cutover, or parent edges.
- `keepConflicts`, `coordinate`, and `treeMove` are rejected at field definition.
- No shared checkpoint yet. After an id leaves the dedup window, a rewritten payload is not detected. A checkpoint later proves coverage and can reject a covered id without the original digest. An unchanged replay is still held by the field version.
- No retained operation payloads, so `CanReplay` is false. Digest memory does not become a delta. A contribution merge does not become that history. An operation whose envelope is gone is not rebuilt from one field.
