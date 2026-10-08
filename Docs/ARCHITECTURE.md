# Ape Architecture

## Overview: 3-Tier Event-Driven System

**Ape** is a modular platform built on **Ape.Core** (framework), **Ape.Launcher** (launcher), and **Ape.Module.*** (feature verticals). The runtime uses a **3-tier architecture** with clear separation of concerns:

1. **Tier 1: Core** - Built-in services (ILogger, IEventManager, ISceneManager, etc.)
2. **Tier 2: Services** - Infrastructure services (IService implementations, configurable)
3. **Tier 3: Plugins** - Application logic (IPlugin implementations, configurable)

Ape is a **deterministic, graph-native, misuse-resistant** application framework: it moves errors from runtime into compile/freeze-time graph validation.

> **Ape makes execution explicit and misuse difficult:** structure, control flow, data dependencies, time semantics, and effects are compiler-visible rather than hidden in runtime behavior.

> **No hidden execution. No hidden state. No hidden time.**

> **Make invalid programs difficult to express, and detectable before execution.**

Use **misuse-resistant**, **fail-fast**, **determinism-oriented**, and **compiler-enforced invariants**. Do not call the stack **correct-by-construction** yet: permissive `Describe()` means `Compile()` proves the *enforced* invariants, not that every undeclared access is safe. That stronger claim waits for Strict mode, Effect IR (`CG311`), and temporal / freshness validation.

### Source tree (current migration)

**Intended end state** under `src/`:

- `Ape.Core/` — Everything needed to build **any** program against the framework: interfaces, implementations, plugin/service loaders, networking, scene, replication, config, **component graphs**. (The old separate `Ape.Abstractions` project is merged into Core.)
- `Ape.Launcher/` — Thin executable: `Program.cs` calls `ApeSystem.Start`. No domain logic. Network **host** (scene participant/server) is a Core role, not this process name.
- `Ape.Modules/` — Feature and product verticals (`Ape.Module.*`). Each module may ship **services**, **plugins**, **scene/replica types**, and **samples**. Modules reference `Ape.Core` and may reference **other modules**.

**Transitional** (until migration finishes):

- Sample host JSON configs live under each module’s `Samples/` folder, or under `Ape.Core/*/Samples/` for core demos (e.g. replica subscription under `Ape.Core/Replication/Samples/`).

For the **recommended folder layout inside a module** (`Common`, `Plugins`, `Services`, `Samples`), see [Module folder layout](#module-folder-layout) below.

### Architecture Flow

```
┌────────────────────────────────────────────────────────────────────┐
│  Tier 1: Core (Built-in)                                           │
│  ───────────────────────                                           │
│                                                                    │
│  - ILogger, IEventManager, ISceneManager                           │
│  - IReplicaManager, INetworkManager, IConfigManager                │
│  - ComponentGraph / FrozenPlan (module execution model)            │
│  - Always available in DI container                                │
│  - Cannot be disabled or replaced                                  │
└────────────────────────────────────────────────────────────────────┘
                            ↓
┌────────────────────────────────────────────────────────────────────┐
│  Tier 2: Services (Infrastructure)                                 │
│  ─────────────────────────────────                                 │
│                                                                    │
│  - IService implementations (pluggable, e.g. GestureManager)       │
│  - Loaded from DLLs: root + modules[*].services (Ape.Service.*)    │
│  - Initialize() → Register in DI → Start()                         │
│  - Run background tasks (device monitoring, etc.)                  │
│  - Reusable across multiple plugins                                │
└────────────────────────────────────────────────────────────────────┘
                            ↓
┌────────────────────────────────────────────────────────────────────┐
│  Tier 3: Plugins (Application Logic)                               │
│  ────────────────────────────────────                              │
│                                                                    │
│  - IPlugin implementations                                         │
│  - Loaded from DLLs: modules[*].plugins (see Module-Configuration) │
│  - OnInit() → Request services from DI → OnRun()                   │
│  - Each runs in dedicated thread                                   │
│  - Application-specific logic                                      │
└────────────────────────────────────────────────────────────────────┘
```



### Modules: plugins, services, and scene entities

Every feature ships as an `Ape.Module.*` project. A module is **not** only a folder name — it is a composable unit that can:


| Capability             | How                                                                                                                     |
| ---------------------- | ----------------------------------------------------------------------------------------------------------------------- |
| **Plugins**            | One or more `IPlugin` assemblies under `Plugins/` — application logic, often one thread per plugin                      |
| **Pluggable services** | `IPluggableService` (or module `IService`) DLLs under `Services/` — long-running infrastructure shared by plugins       |
| **Scene entities**     | Domain `SceneEntities` / `Common` types + `ISceneEntityRegistry` registration via module `ICoreService` at host startup |
| **Component graphs**   | Nested `ComponentGraph<TScratch>` under module `Graph/` — frozen at init into a `FrozenPlan` ticked once per host frame |
| **Samples**            | JSON configs and demos under `Samples/`                                                                                 |
| **Documentation**      | Module-local `Docs/`                                                                                                    |


**Cross-module dependencies:** a module references `Ape.Core` and may reference **other modules** at **source level** (project reference). Example: `Ape.Module.RestApi` may use scene types from `Ape.Module.Example`; `Ape.Module.Audio` stays independent unless it explicitly references another module.

**Config wiring:** host JSON `modules["Ape.Module.XY"].plugins` / `.services` declares what to load. Plugin assembly names are merged and sorted deterministically (see [DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md)).

**Scene entity rule:** `SceneManager` in Core does **not** hard-code domain types. Each module registers `typeId` → factory hooks; remote peers need the same module DLLs for MessagePack union / `Type.GetType` resolution.

```
Ape.Module.Example/
├── Common/           # shared Models, Interfaces, Utils
├── Graph/            # optional: ComponentGraph + stages
├── Plugins/          # IPlugin assemblies
├── Services/         # background services
├── SceneEntities/    # optional: replica types + registration service
├── Docs/
├── Samples/
└── Tests/
```



### Lifecycle Flow

```
Program.cs startup sequence:

1. Register Core services (Tier 1)
   └─→ ILogger, IEventManager, ISceneManager, etc.

2. Load & Initialize Services (Tier 2)
   └─→ `Ape.Core.Runtime.Service.ServiceLoader.LoadServicesFromConfig()`
       ├─→ Load module / service DLLs from beside the process
       ├─→ Call IService.Initialize(services)
       │   └─→ Services register themselves in DI
       └─→ Call IService.Start()
           └─→ Background tasks begin (e.g., device polling)

3. Load & Initialize Plugins (Tier 3)
   └─→ PluginManager.LoadPluginsFromConfig()
       ├─→ Load IPlugin types from module DLLs beside the process
       ├─→ Call IPlugin.OnInit(services)
       │   └─→ Plugins request services from DI (GetService<IExampleService>())
       └─→ Spawn threads & call IPlugin.OnRun()

4. Graceful shutdown (Ctrl+C)
   ├─→ Plugins: OnShutdown() + cancel threads
   └─→ Services: Stop()
```

---



## Modular JSON config (`modules`)

Runtime config uses a top-level `modules` object so settings are grouped by logical module id. Plugin and pluggable-service DLL discovery uses `modules[*].plugins` and `modules[*].services` only (see `[Module-Configuration.md](../src/Ape.Core/Config/Docs/Module-Configuration.md)`). Legacy root `network` may still appear in older samples; prefer `modules["Ape.Core.Network"]`.

### Network (`Ape.Core.Network`)

If `modules["Ape.Core.Network"]` exists, it **replaces** the legacy root `network` object for transport, `enabled`, `role`, `port`, `peerName`, and `host`.

### Plugins per module

Each module entry may define `plugins` as an object: values are plugin-specific options (use `{}` when there is no config).

- **Keys without** `.` are **short** names: the host loads matching `IPlugin` types from `{moduleId}.dll` beside the process (legacy sidecar `{moduleId}.Plugin.{key}.dll` is still probed).
- **Keys that contain** `.` are **full** assembly base names (e.g. a fully qualified assembly name inside a module block).

The effective plugin list is the **sorted union** of resolved names from every `modules[*].plugins` entry that is not disabled by `enabled` at module or plugin level. Use a dedicated module id (e.g. `Host.Plugins`) with **full assembly base names** as keys for plugins that are not scoped under an `Ape.Module.`* id.

### Services per module

Each module entry may define `services` as an object: keys identify services; values are options objects.

- Keys `Ape.Service.*` (e.g. `Ape.Service.GestureManager`) are loaded from DLLs beside the process when listed under `modules[*].services` and not disabled.
- Other keys (e.g. `ExampleService` or the fully qualified type-style name) are **declarative**: they document or future-proof options for services wired inside the host process; they are **not** loaded as DLLs.



### Example

```json
{
  "modules": {
    "Ape.Core.Network": {
      "enabled": false,
      "transport": "quic",
      "role": "server",
      "port": 5000,
      "peerName": "server"
    },
    "Ape.Module.Example": {
      "services": {
        "ExampleService": {}
      },
      "plugins": {
        "ExamplePlugin": {}
      }
    }
  }
}
```

---



## Core Principle: Events Through Setters Only

In Ape, **replica property notifications** are triggered automatically through property setters (which publish `PropertyChangedEvent` via `IEventManager`). Application code should not manually publish those property-change events. This keeps behavior consistent with replication.

---



## 📐 Property Change Architecture Flow

```
┌─────────────────────────────────────────────────────────────┐
│  Plugin / User Code                                         │
│  ─────────────────                                          │
│                                                             │
│  var cube = sceneManager.GetNodeByPath("/Cube");           │
│  cube.Position = new Vector3(10, 0, 0);  ←─── SETTER       │
│                           ↓                                 │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ SetProperty<T>() called internally
                            ↓
┌─────────────────────────────────────────────────────────────┐
│  ApeReplica.SetProperty()                                   │
│  ────────────────────────                                   │
│                                                             │
│  1. Compare old vs new value                                │
│  2. Update backing field                                    │
│  3. Call OnPropertyChanged()  ←─── AUTO EVENT TRIGGER       │
│                           ↓                                 │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ IEventManager.Publish(PropertyChangedEvent)
                            ↓
┌─────────────────────────────────────────────────────────────┐
│  IEventManager                                              │
│  ─────────────                                              │
│                                                             │
│  - Queues event for all subscribers                         │
│  - Each plugin has its own queue                            │
│  - Thread-safe, lock-free                                   │
│                           ↓                                 │
└─────────────────────────────────────────────────────────────┘
                            │
                            │ DrainEventsFor(pluginId)
                            ↓
┌─────────────────────────────────────────────────────────────┐
│  Plugin Subscribers                                         │
│  ──────────────────                                         │
│                                                             │
│  OnPropertyChanged(PropertyChangedEvent evt) {              │
│      // evt.UniquePath = "/Cube"                            │
│      // evt.PropertyName = "Position"                       │
│      // Read current value via sceneManager.GetNodeByPath   │
│                                                             │
│      var obj = sceneManager.GetNodeByPath(evt.UniquePath);  │
│      // React to change...                                  │
│  }                                                          │
└─────────────────────────────────────────────────────────────┘
```

---



## 🔧 Implementation Details



### 1. Replica base and `PropertyChangedEvent`

In `Ape.Core.Replication`, `Replica` (and scene types such as `Node`) use `SetProperty<T>()` in setters. On change, `OnPropertyChanged(string propertyName)` publishes a **notification-only** `PropertyChangedEvent` via `IEventManager` — it carries `ReplicaId`, `UniquePath`, and `PropertyName`; subscribers read current values from the scene/replica, not from the event payload.

### 2. Scene objects with auto-event setters

```csharp
// Simplified — see Ape.Core.Scene.Models.Node
public Vector3 Position
{
    get => _position;
    set => SetProperty(ref _position, value);
}
```



### 3. Event shape (actual)

```csharp
// Ape.Core.Replication.PropertyChangedEvent — notification-only
public class PropertyChangedEvent : IEvent
{
    public string ReplicaId { get; init; }
    public string UniquePath { get; init; }
    public string PropertyName { get; init; }
    // … EventId, Timestamp (see source)
}
```



### 4. Plugin: Modify Objects

Plugins **read** through `ISceneRead` and **write** by enqueueing commit requests on the frame-scoped `IFrameCommitBatch`. Do not resolve `ISceneManager`, and do not assign `node.Position` from the plugin thread.

Use the generic property-set request (`PropertyName` + value), not a new commit type per field.

```csharp
public class ModifierPlugin : IPlugin, IDeterministicFrameParticipant
{
    private IFrameParticipantRegistry? _participants;

    public string ParticipantId => "modifier";
    public FramePhase Phase => FramePhase.Publish;
    public int Order => 100;

    public void OnInit(IServiceProvider services)
    {
        _participants = services.GetRequiredService<IFrameParticipantRegistry>();
        _participants.Register(this);
        // services.GetService<ISceneManager>() is null in plugin DI
    }

    public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
    {
        commits.Enqueue(
            new SetSceneNodePropertyCommitRequest(
                "/Cube",
                nameof(INode.Position),
                new Vector3(10, 20, 30)));
    }
}
```

The Core applicator applies the batch after all participants return; property setters (and `PropertyChangedEvent`) run there, not in the plugin.



### 5. Plugin: React to Changes

```csharp
public class ListenerPlugin : IPlugin
{
    private ISceneRead? _scene;
    private IEventManager? _eventManager;

    public void OnInit(IServiceProvider services)
    {
        _scene = services.GetService<ISceneRead>();
        _eventManager = services.GetService<IEventManager>();

        _eventManager?.Subscribe<PropertyChangedEvent>(
            PluginId, OnPropertyChanged);
    }
    
    private void OnPropertyChanged(PropertyChangedEvent evt)
    {
        // Filter for specific objects/properties
        if (evt.UniquePath == "/Cube" && 
            evt.PropertyName == nameof(Node.Position))
        {
            // Retrieve the full object if needed
            var cube = _scene?.GetNodeByPath(evt.UniquePath);
            
            _logger?.LogInfo(
                $"Cube moved to {cube?.Position}");
        }
    }
}
```

---



## 🌐 Network Synchronization

When networked:

```
Client A                    Network                     Client B
────────                    ───────                     ────────
cube.Position = <10,0,0>
    │
    ├─→ PropertyChangedEvent (local)
    │
    └─→ Serialize() → UDP → Deserialize()
                                            │
                                            ├─→ cube.Position = <10,0,0>
                                            │       (setter called!)
                                            │
                                            └─→ PropertyChangedEvent (remote)
```

**Both local and remote changes go through setters** → Consistent event triggering!

---



## ✅ Benefits



### 1. **Consistency**

- Events ALWAYS fire when properties change
- No forgotten `Publish()` calls
- Local and network changes handled identically



### 2. **Discoverability**

- `UniquePath` makes objects easy to find
- `GetNodeByPath("/Root/Player")` is intuitive
- No GUID lookups needed in plugin code



### 3. **Decoupling**

- Modifier plugins don't know about listeners
- Listener plugins don't know about modifiers
- Pure event-driven communication



### 4. **Thread-Safety**

- Each plugin has its own event queue
- No shared mutable state between plugins
- Lock-free event delivery



### 5. **Network Transparency**

- Deserialization calls setters automatically
- Events fire on remote clients too
- No special network handling code

---



## 🚫 Anti-Patterns (DON'T DO THIS)



### ❌ Manual Event Publishing

```csharp
// WRONG!
cube.Position = new Vector3(10, 0, 0);
eventBus.Publish(new SomeCustomEvent());  // ← NO!
```

**Why wrong:** Events should ONLY come from property changes.

### ❌ Direct Property Access Without Setter

```csharp
// WRONG!
cube._position = new Vector3(10, 0, 0);  // ← NO! Bypasses event!
```

**Why wrong:** No event fired, breaks synchronization.

### ❌ GUID-Based Lookups in Plugins

```csharp
// DISCOURAGED
var cube = sceneManager.GetNode(someGuid);  // ← Hard to maintain

// BETTER
var cube = sceneManager.GetNodeByPath("/Cube");  // ← Clear & readable
```

---



## 📋 Checklist for Plugin Developers

When creating a plugin that modifies scene objects:

- [ ] Resolve **`ISceneRead`** in `OnInit` (not `ISceneManager` — plugin DI returns null)
- [ ] Register as **`IDeterministicFrameParticipant`** and enqueue **`ISceneCommitRequest`** on `IFrameCommitBatch` in `OnHostFrame`
- [ ] Prefer generic `SetSceneNodePropertyCommitRequest` / `SetSceneEntityPropertyCommitRequest` over a new type per property
- [ ] Do **not** assign `node.Position` (throws outside Core apply scope)
- [ ] **Never** call `IEventManager.Publish()` manually for property-change notifications (applicator setters do that)
- [ ] Subscribe to `PropertyChangedEvent` to react to changes
- [ ] Use `evt.UniquePath` to identify which object changed
- [ ] Call `DrainEventsFor()` in plugin loop
- [ ] Filter events by `UniquePath` and `PropertyName` if needed
- [ ] (Optional) Request infrastructure services via `GetService<IExampleService>()`
- [ ] Stream/ingest plugins: use a `ComponentGraph` (SAMPLE → TICK → COMMIT), not callback-driven parse
- [ ] Callbacks enqueue on `IIngress<T>` (source sequence when fusing)

---



## 🎯 Summary


| Aspect                    | Implementation                               |
| ------------------------- | -------------------------------------------- |
| **Architecture**          | 3-Tier: Core → Services → Plugins            |
| **Service Registration**  | Services register in DI during Initialize()  |
| **Service Lifecycle**     | Start() before plugins, Stop() after plugins |
| **Plugin Service Access** | Core allowlist + trusted `Ape.Module.*` (not foolproof) |
| **Event Source**          | Property setters ONLY                        |
| **Object Lookup**         | `ISceneRead.GetNodeByPath(string)`           |
| **Event Type**            | `PropertyChangedEvent`                       |
| **Auto-Trigger**          | `SetProperty<T>()` helper                    |
| **Network Sync**          | Deserialize → Setter → Event                 |
| **Thread Model**          | Plugin-specific event queues                 |
| **Module execution**      | `ComponentGraph` authored at init → frozen `FrozenPlan` per host frame |
| **Scene writes**          | `IFrameCommitBatch` in COMMIT (`OnHostFrame`) |
| **Async input**           | `IIngress<T>` (not the scene sink)           |
| **Manual Publish**        | ❌ NEVER for property changes                 |


---



## 🚀 Example: Complete Plugin with Service Usage

```csharp
using System.Numerics;
using Ape.Core.Determinism;
using Ape.Core.Event;
using Ape.Core.Logging;
using Ape.Core.Runtime.Plugin;
using Ape.Core.Replication;
using Ape.Core.Scene;
using Ape.Core.Scene.Commit;

public class ExamplePlugin : IPlugin, IDeterministicFrameParticipant
{
    public string PluginId => "example";
    public string Name => "Example Plugin";

    public string ParticipantId => PluginId;
    public FramePhase Phase => FramePhase.Publish;
    public int Order => 100;

    private IEventManager? _eventManager;
    private ISceneRead? _scene;
    private ILogger? _logger;

    public void OnInit(IServiceProvider services)
    {
        _eventManager = services.GetService(typeof(IEventManager)) as IEventManager;
        _scene = services.GetService(typeof(ISceneRead)) as ISceneRead;
        _logger = services.GetService(typeof(ILogger)) as ILogger;
        var participants = services.GetService(typeof(IFrameParticipantRegistry)) as IFrameParticipantRegistry;
        participants?.Register(this);

        // Optional: IExampleService from Ape.Module.Example (add project reference + using)
        // var deviceManager = services.GetService<IExampleService>();

        _eventManager?.Subscribe<PropertyChangedEvent>(PluginId, OnPropertyChanged);
    }

    public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
    {
        commits.Enqueue(
            new SetSceneNodePropertyCommitRequest(
                "/Player",
                nameof(INode.Position),
                new Vector3(0.1f * context.FrameId, 0, 0)));
    }

    public void OnRun(CancellationToken token)
    {
        _logger?.LogInfo($"{Name} thread started");

        while (!token.IsCancellationRequested)
        {
            _eventManager?.DrainEventsFor(PluginId);
            Thread.Sleep(100);
        }

        _logger?.LogInfo($"{Name} thread exiting");
    }

    private void OnPropertyChanged(PropertyChangedEvent evt)
    {
        if (evt.UniquePath == "/Enemy" && evt.PropertyName == "Health")
            _logger?.LogInfo($"Enemy health changed ({evt.UniquePath})");
    }

    public void OnShutdown()
    {
        _eventManager?.Unsubscribe<PropertyChangedEvent>(PluginId);
        _logger?.LogInfo($"{Name} shutdown");
    }
}
```

---

**This is the Ape pattern: events flow naturally from property changes, creating a consistent, network-transparent, event-driven system.**

---



## 🔧 Creating Infrastructure Services

Services are reusable infrastructure components that multiple plugins can access via DI.

### When to Create a Service vs Plugin

**Create a Service when:**

- ✅ Multiple plugins need the same functionality (e.g., device detection)
- ✅ Background tasks required (e.g., polling, monitoring)
- ✅ Resource management needed (e.g., hardware access)
- ✅ Lifecycle independent of plugins (start before plugins, stop after)

**Create a Plugin when:**

- ✅ Application-specific logic
- ✅ Reacts to events/services
- ✅ No other plugins depend on it



### IService Interface

```csharp
public interface IService
{
    /// <summary>
    /// Unique service identifier (e.g., "device-manager")
    /// </summary>
    string ServiceId { get; }
    
    /// <summary>
    /// Human-readable service name
    /// </summary>
    string Name { get; }
    
    /// <summary>
    /// Initialize and register service in DI container.
    /// Called BEFORE Start().
    /// </summary>
    void Initialize(IServiceProvider services);
    
    /// <summary>
    /// Start background tasks (e.g., device polling).
    /// Called AFTER all services Initialize(), BEFORE plugins load.
    /// </summary>
    void Start();
    
    /// <summary>
    /// Stop background tasks and cleanup.
    /// Called AFTER all plugins shutdown.
    /// </summary>
    void Stop();
}
```



### Device manager (module DLL)

`ExampleService` lives in `Ape.Module.Example`. `ApeSystem` loads it from `Ape.Module.Example.dll` beside the process when that module is enabled in config. Plugins resolve `IExampleService` from DI.

### Service Discovery in Plugins

```csharp
public class MyPlugin : IPlugin
{
    private IExampleService? _example;

    public void OnInit(IServiceProvider services)
    {
        _example = services.GetService<IExampleService>();
        if (_example != null)
            _example.Subscribe(OnItemsFound);
    }
}
```



### Example module layout (host code + contracts)

```
Ape.Modules/Ape.Module.Example/Services/Example/
├── IExampleService.cs, …              # contracts + helpers
├── ExampleService.cs                  # ICoreService (host registers)
```

---



## Module folder layout

Target internal structure for `Ape.Module.*` projects. Migration is gradual — older folder names (`SceneEntities`, flat `Models`, …) remain until refactored.

```
Ape.Module.XY/
├── Common/
│   ├── Models/          # DTOs, options, records
│   ├── Interfaces/      # Module contracts (parallel domain folders)
│   └── Utils/
├── Docs/
├── Graph/               # optional: ComponentGraph + IStage implementations
├── Plugins/<PluginName>/  # One folder per IPlugin assembly
├── Services/<ServiceName>/
└── Samples/
```

**Models / Interfaces:** separate top-level buckets; one level of domain subfolders (`Tracks`, `Sensors`, `Devices`, …). Reference: `Ape.Module.Example` and `Ape.Module.Example` for concrete layouts.

**Plugin-local vs module Common:** types used by two+ plugins belong in module `Common/`, not under a single plugin.

**Naming:** module id = `Ape.Module.Example` (config key); plugin assembly often `Ape.Module.XY.Plugin.<Name>`.

---



## Build output

Build artifacts live under `build/` (not `src/**/bin`). Configured via root `Directory.Build.props`.

```
build/bin/Ape.Launcher/<Configuration>/net10.0/
  ├── Ape.Launcher.dll
  ├── Ape.Core.dll
  └── Ape.Module.*.dll
```

```bash
./ape clean
./ape build
./ape run -c path/to/host.json
```

---



## Scene entity migration

Framework for splitting **scene graph DTOs and factories** between **Core** and **modules** without breaking MessagePack / replication.


| Package                      | Role                                                                                                                                                                                            |
| ---------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **Ape.Core**                 | Contracts (`IEntity`, `INode`, `ISceneManager`, `ISceneEntityRegistry`), `Node` / minimal `Entity`, replication infra. **No** domain-specific entity types; `typeId` constants live in modules. |
| **Ape.Module.Example**            | Domain entities + registry bootstrap                                                                                                                                                            |
| **Other modules**            | Register their own scene types the same way (private modules follow the same pattern)                                                                                                           |


**Registry pattern:** `SceneManager` does not hard-code domain types — modules register factories via `ICoreService` at host startup (`CreateRegisteredEntity`).

**Host JSON** `modules` **block:**

- `modules["Ape.Core.Network"]` — overrides root `network` when present
- `modules["Ape.Module.*"].services` / `.plugins` — per-module declarations
- Root `"services": []` and `"plugins": []` kept for backward compatibility (merged with `modules`)

**Migration steps (incremental):**

1. Registry + remote attach on one path (modules register at startup).
2. Pilot: register domain entities from the owning module.
3. Update MessagePack `[Union]` tags when moving types; test serialize roundtrip.
4. `typeId` string constants per module (e.g. `XrSceneEntityTypeIds`, `DeviceSceneEntityTypeIds`).

**Wire compatibility:** peers use assembly-qualified `TypeName`; mixed versions need unified deploy or type forwarding.

**Entry points:** `*SceneEntityRegistrationService` implementations in each module — loaded by `ApeSystem` from module DLLs beside the process.

---



## Component graphs (`Ape.Core.Graph`)

This is the **Ape module execution model** — not a convenience pipeline builder.

A module pipeline is a nested dataflow graph, authored during plugin initialization, then **statically frozen before the first tick**. No structural graph mutation occurs during ticking. A hierarchical state machine selects the active stage subset at tick boundaries.

```
ComponentGraph<TScratch>        authoring / composition
        │
        ├── nested graphs       structural modularity
        ├── Expose / Connect    dataflow (Port IR; scratch is the bus)
        ├── Raise(...)          events
        └── HSM                 control plane
        │
        ▼
      Compile()                 initialization-time freeze
        │
        ├── flatten hierarchy
        ├── compile A: S → 2^V  per-state masks
        ├── resolve ports
        ├── validate            CG101… (expanding)
        └── freeze
        │
        ▼
     FrozenPlan                 Structure + Control + Data
        │
        ▼
 tick(frame N)                  control microsteps, then masked stages
        │
        ▼
 Scene Commit
```

This is the intra-plugin counterpart of the commit pipeline in [DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md): the graph is the **deterministic reducer**; the scene sink is the **single writer**. Plugins should declare units, edges, and states — not threads, callback lifetimes, or subscription cleanup.

### Discrete time, not reactive streams

The graph is a **discrete-time execution model**, not a reactive dataflow. Graphene / RxJS derives time from emit order: each source callback can run fusion against a mixed snapshot (new radar, old camera). Ape makes time an explicit part of the program:

```
Async world  →  SAMPLE  →  deterministic graph  →  COMMIT  →  Async world
```

```
I_t = Sample(Environment, t)
O_t = Tick(I_t)
```

The frame is a **consistency boundary**. Fusion, tracking, and scene commit see one world snapshot per tick — not whatever arrived mid-callback. Replay follows if you keep `P`, `Q_0`, `I_0 … I_n`: same compiled plan, same SAMPLE tuple, same outputs.

> **Async is an I/O property, not the application execution model.** Serial, QUIC, HTTP, UI, and disk stay asynchronous. They only append to pending buffers. The program inside the tick is a reducer.

Do not treat enabled stages inside one `FrozenPlan` as independent Hz. If `parse` and `demux` are both in the mask, both run **once this tick**. Rate mismatch lives first at the SAMPLE boundary (async arrivals vs `I_t`), then at skipped consumers / multi-value slots (Delivery), not as `parse @ 60 Hz` vs `demux @ 20 Hz` on the same sequential plan.

The compiler and the SAMPLE boundary are how the framework stays **misuse-resistant** and **fail-fast**:

```
multiple writers              →  CG207
wrong producer/consumer order →  CG208
unknown port / raise          →  CG209
invalid connection            →  CG213
async timing ambiguity        →  SAMPLE boundary
hidden execution ordering     →  FrozenPlan + StageOrder
```

> **No hidden execution. No hidden state. No hidden time.**

### Initialization-time freeze (not C# compile time)

`new ComponentGraph<TScratch>(...).Compile()` is still **runtime compilation**. It runs during `OnInit`, before any host frame. That is stronger than “the graph grows as messages arrive”, and weaker than Roslyn / source-generation compile time.

> The graph is dynamically authored during module initialization, but statically frozen before execution. No structural graph mutation occurs during ticking.

If a later source generator moves the same IR to build time, that is when the term *compile-time graph compilation* becomes accurate.

### Authoring vs runtime

Nested graphs are an **authoring-time** abstraction. The developer sees `decode/eph/gate`; the tick loop sees a flat `IStage[]` plus a 64-bit enable mask.

| Layer | Types | Lives until |
| ----- | ----- | ----------- |
| **Authoring** | `Component<TScratch>`, `ComponentGraph<TScratch>`, `IStage<TScratch>` | `Compile()` |
| **IR** | `GraphDefinition` (internal) | compile only |
| **Runtime** | `FrozenPlan<TScratch>` + `PlanRuntime` + `TScratch` | plugin lifetime |

`ComponentGraph<TScratch>.Compile()` walks the nested tree, assigns **qualified stage ids** (`decode/parse`, `decode/eph/gate`), validates HSM targets/states, and returns a `FrozenPlan`. After that the authoring objects can be discarded. `Tick` is a `for` loop over an array plus an HSM mask — not a graph walk.

Nested ids are the **declaration order** of `Add(...)` calls. Same graph source → same `StageOrder`. Tests should assert that list (see `GraphCompilerTests`). Dump the frozen plan with `plan.Describe()` when debugging or when an agent needs to inspect the program.

```
Authoring (nested)                         FrozenPlan.StageOrder
──────────────────                         ────────────────────
serial-io                                   watch
├── watch                                  serial
├── serial                                 cfg
├── cfg                                    decode/parse
├── decode                                 decode/demux
│   ├── parse                              decode/week
│   ├── demux                              decode/eph/accumulate
│   ├── week                               decode/eph/eph-decode
│   ├── eph                                decode/eph/gate
│   │   ├── accumulate                     decode/rawx
│   │   ├── eph-decode                     rinex
│   │   └── gate                           publish
│   └── rawx
├── rinex
└── publish
```

### Compiler: now vs next

`Compile()` already **flattens, validates, resolves HSM masks, and freezes**. It is not yet a full optimizer / execution planner. Intended pipeline:

```
ComponentGraph
      │
      ▼
 flatten hierarchy          ← now
      │
      ▼
 compile per-state masks    ← now: A(S) = active stages
      │
      ▼
 resolve ports              ← now: ExposeIn/Out + Connect
      │
      ▼
 validate                   ← now: CG101 duplicate stage,
                              CG102 unknown HSM target,
                              CG103 undeclared HSM state,
                              CG104 consumer on, producer off,
                              CG105 duplicate HSM state,
                              CG106 more than 64 stages,
                              CG201 incompatible ports,
                              CG207 multiple writers, no hidden last-write,
                              CG208 producer after consumer this tick,
                              CG209 unresolved port / raise,
                              CG210 duplicate port,
                              CG212 bad scratch selector,
                              CG213 connection not Out→In
      │
      ▼
 writer arbitration         ← now: *declared* Writes, CG207
      │
      ▼
 freshness contracts        ← Sampled / CurrentTick now;
                              Latched needs init analysis first
      │
      ▼
 side-effect classification ← next (CG311: scene writes only in commit)
      │
      ▼
 Strict access mode         ← later: closed-world Reads/Writes
      │
      ▼
 sampling / delivery        ← later: SAMPLE policy, then Delivery
                              lowering into scratch storage
      │
      ▼
 FrozenPlan
```

Analysis is **permissive**: missing `Describe` is allowed. `CG207` proves `|W_declared(s, q)| ≤ 1`, not `|W(s, q)| ≤ 1`. Undeclared ≠ no access.

Later compiler modes (roadmap, not built):

| Mode | Rule | Guarantee |
| ---- | ---- | --------- |
| **Permissive** (now) | `Describe` optional | best-effort on declared access |
| **Strict** | graph-relevant scratch access must be declared | closed-world: the compiler's `|W| ≤ 1` and tick-order facts cover every access |

`CompilerAccepted(P)` ⇒ the **enforced** invariants hold (CG104, CG207, CG208, …). That is not `CompilerAccepted(P)` ⇒ `Correct(P)`. Undeclared scratch access is invisible in Permissive mode. Do not claim **correct-by-construction** or closed-world safety until Strict exists, plus Effect IR and temporal / freshness validation.

Unconnected `ExposeIn` is a **SAMPLE / environment** input (the host writes scratch before Tick). `Connect` is for graph-owned producers.

### Port contracts (Type + Freshness + Sampling + Delivery)

A port is more than a typed scratch slot. Four orthogonal facts; do not collapse them into one “backpressure” flag:

```
PortContract
│
├── Type          CLR type of the slot
│
├── Freshness     When is the value valid this tick?
│     Sampled / CurrentTick / Latched
│
├── Sampling      How does the async world become I_t?
│     Latest / DrainAll / Cap(N) / Overflow / Reduce
│     (ExposeIn only — SAMPLE boundary)
│
└── Delivery      How does a producer reach a consumer
                  when rates or enable-masks differ?
      Direct / Queue<N> / Batch<N> / Every(n) / …
      (Connect — compiler sugar, not a runtime channel)
```

**Freshness** is three temporal contracts, not three boolean flags:

```
Sampled      SAMPLE_t produced q; no graph producer, no CG208
CurrentTick  a writer w this tick with w ≺ reader (CG208)
Latched      q_t = q_k for some k < t; ordering this tick is optional
```

**Latched is an initialization problem**, not an ordering problem. `Boot` reading `Tracks` as latched is illegal unless something defined `Tracks` before the first tick (`InitialValue`, SAMPLE, or a writer on every path to that state). Do not treat `PortFreshness.Latched` as proven until that analysis exists.

**Sampling** maps async arrivals onto this tick’s input. It is the Ape-native answer to RxJS backpressure — at the deterministic boundary, not between sequential stages:

```
async arrivals:  F1  F2  F3  F4  F5
                 ───────── tick ─────────
Latest        →  F5
DrainAll      →  [F1, F2, F3, F4, F5]
Cap(3)+oldest →  [F3, F4, F5]
Reduce        →  R(F1..F5)
```

Today SAMPLE is an implicit host copy (`pending[]` → scratch). A later `ExposeIn(..., sample: Latest | DrainAll.Cap(64).Overflow(DropOldest))` makes that contract compiler-visible. Implement **Sampling on `ExposeIn` first**; that is where 60 fps cameras vs a 60 Hz tick actually meet.

**Delivery** is authoring syntax on `Connect` for skipped consumers or N→K slots (camera enabled every tick, detector every fourth; a producer writing a collection the consumer cannot drain). It is **not** a hidden edge queue. `TScratch` remains the only runtime data bus.

```
authoring:  Connect(a, b).Delivery(Queue(4), DropOldest)

Compile() lowers to:

            a  →  scratch.__delivery_k  →  b
                  (explicit storage + overflow rule)
```

Inspector and replay must see that slot (depth, drops, age in ticks). A `BufferStage` that hides a ring buffer inside `Execute` is the wrong direction. Wall-clock operators (`Debounce(50ms)`, `Window(100ms)`) belong only if they use **logical time** from SAMPLE / `frameId` — not `DateTime.UtcNow`.

Do not add `BlockProducer`. A tick never waits on a consumer.

**Flow adaptation is HSM state, not runtime magic.** Do not put `.Adaptive<LatencyPolicy>(...)` on a connection. Measure queue depth, drop rate, stage duration for the Inspector; if the system must shed load, raise a control event and change the mask / sampling in an explicit state (`Normal` → `Pressure` → `Critical`). Then `Policy_t ∈ State_t`, so the journal records `CpuPressure` the same way it records `PortOpen`.

```
metrics → FlowController → EnqueueEvent → HSM
                                Normal | Pressure | Critical
```

Effects (`CG311`) are the next IR axis to build: Structure + Control + Data are already in the IR; scene / I/O side effects are not yet classified. Optimizer, Strict mode, then Sampling / Delivery IR come after that. This section is the spec, not the current API. Do not implement Sampling or Delivery before `CG311`.

> **No hidden temporal state.** Queue, Window, Debounce, Batch, Latch, Reduce all need memory. That memory is compiled IR + named scratch storage: identifiable, inspectable, replayable. No closure, no Rx object, no scheduler behind the plan.

Examples the compiler already catches **before the first tick**:

```
ERROR CG104
demux/frames consumes parse/frames,
but 'parse' is disabled in HSM state "Run".

ERROR CG207
Multiple writers for scratch.Frames in HSM state "Run":
  fuse, coast
No deterministic arbitration rule is defined.

ERROR CG208
demux/frames reads parse/frames this tick, but 'demux' (index 0)
runs before 'parse' (index 1) in HSM state "Run".

ERROR CG209
Connection 'serial/bytes -> decode/bytes' has unresolved producer 'serial/bytes'.
```

Still later: latched initialization analysis, then scene writes outside commit (`CG311`). Runtime data still moves through `TScratch`; Port IR is the compiler's view of that bus, not a second PortBag.

`CG208` on a `Connect` first uses **leaf** Reads/Writes when both ends declared them for that slot. Otherwise it falls back to an **owner-prefix span** (last enabled stage in the producer component vs first in the consumer). Nested graphs make that span conservative: extra siblings in `decode/*` count even if they do not touch the slot. The span is not the final dependency semantics.

**Deterministic last-writer in `StageOrder` is not an arbitration rule.** Two declared writers of the same slot in one state is `CG207` until an explicit policy exists.

### Introspection

`FrozenPlan` is a program. Agents and humans should be able to read it:

```csharp
plan.Describe();        // order + HSM activation matrix
plan.ToDot();           // Graphviz
plan.ExplainStage("decode/eph/gate");
```

`Describe()` prints the **compiled** per-state activation masks — the same table `Tick` uses. Assert `StageOrder` in tests; use `Describe()` when the question is “what actually compiled?”.

### Scratch, stages, HSM

**`TScratch`** is a single shared struct (or class) that stages read and write. Port IR *describes* those members (full path, e.g. `Decode.Frames`); it does not replace them. There is no PortBag at tick time: if `parse` produced frames, `demux` reads `scratch.Frames`. Keep scratch fields explicit; that is the data bus.

**`IStage<TScratch>`** is one `Execute(ref TScratch)` call when the plan enables it. Optional `Describe` declares scratch Reads/Writes for the compiler (`CG207`, leaf `CG208`). Empty `Describe` means those accesses are **invisible** to writer analysis — not that they do not happen (permissive mode). Stages may keep **private buffers** (a UBX extractor accumulating bytes). That instance state is part of the machine: replay from a cold `Compile()` + `InitRuntime`, not from a mid-stream object.

**`PlanRuntime`** holds HSM state, a FIFO event queue, and a 64-bit enable mask (so a plan currently has at most **64** stages). It is **not** compiled IR.

**HSM** (`ComponentGraph.Hsm`) is the **control plane of the compiled dataflow**, not a side state tracker.

Two principles:

> **Hierarchy is erased from execution, but preserved as an addressing namespace.** Nested `Add` is gone after `Compile()`; `"decode"` still names `decode/*` in a state's activation set.

> **The execution mask is solely a function of the current HSM state:** `M_t = A(S_t)`. A transition replaces the mask; it does not incrementally mutate the previous one.

The compiler materializes `A: S → 2^V` from `.State(name, active…)`. `CurrentState` is enough to know which stages run — there is no hidden history in the mask.

Declare the active subset on the **state**, not on the edge:

```csharp
.State("Idle")
.State("Opening", "watch", "serial", "cfg")
.State("Streaming", "decode", "rinex", "publish")
.State("Fault")
.State("Safe")
.On("Idle", DeviceMatch, "Opening")
.On("Opening", PortOpen, "Streaming")
```

Prefixes still expand: `"decode"` turns on `decode/parse`, `decode/eph/gate`, … in one shot. Unknown targets, undeclared states, and duplicate state names fail `Compile()` (`CG102`, `CG103`, `CG105`).

```
              HSM
               │
     compiled mask table
               ↓
data → A → B → C → D → E     (Streaming)
       ✗   ✗   ✓   ✓   ✓
```

Idle / Opening / Streaming / Fault / Safe (u-blox) each compile to a `ulong` mask. Reaching `Streaming` via Opening or via a later retry yields the same enabled stages.

```
Tick(scratch, runtime):
  1. Drain runtime.Events (FIFO) as control microsteps
     → first matching transition wins per event
     → CurrentState = toState
     → mask = A(toState)                 // replace, do not OR
  2. Execute only the stages in the *final* mask
```

If SAMPLE enqueues `DeviceMatch` then `PortOpen` on the same frame, control walks `Idle → Opening → Streaming` **before any stage runs**, and that tick executes only the Streaming subset. Formally:

```
(S_{t-1}, E_t)  --control / RTC-->  S_t  --A-->  M_t  --execute-->  scratch'
```

**Control microsteps first, dataflow execution second.** Intermediate states on that path do not get a dataflow slice in this frame.

Events that appear **during** `Execute` (if a stage somehow enqueues them) wait for the **next** frame's control phase. Dataflow must not change this tick's mask.

Port IR is the compiler's view of the scratch bus; **`TScratch` remains the runtime store**. There is no PortBag at tick time.

A `Connect` is a **current-tick** dependency: the producer must run *before* the consumer in `StageOrder` in every state where both are active (`CG208`). An unconnected `ExposeIn` is **sampled** — the host SAMPLE phase wrote it, so there is no graph producer to order against.

Leaf `Describe` is the precise producer/consumer pair. Owner-prefix span is the fallback approximation.

### How a plugin should tick a plan

The host loop (see [DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md) §4.3 / Step 8) is:

```
PollEvents → IHostFrameRunner.RunNextFrame()
               frameId++
               → participants (Phase → Order → Id) / apply or fail
               → ReplicaManager.Tick() only if Applied and network on
```

`RaiseHostFrame` is the test/Core-internal collection API. Production must not call it and then `ReplicaManager.Tick()` itself — a `false` result would be easy to ignore.

`RunNextFrame` calls every `IDeterministicFrameParticipant.OnHostFrame` in **`Phase → Order → ParticipantId`** order, each with a host-thread **`IFrameCommitBatch`** that is **sealed** when the call returns, then applies concatenated batches. Plugins register through **`IFrameParticipantRegistry`**. A graph plugin registers in `OnInit` and does **three phases** inside `OnHostFrame`:

| Phase | What belongs here | What must not happen here |
| ----- | ----------------- | ------------------------- |
| **SAMPLE** | Drain ingress buffers into scratch; enqueue HSM events | Parse, decode, scene writes |
| **TICK** | `_plan.Tick(ref scratch, ref rt)` | OS I/O, spawning work, mutating `ISceneManager` |
| **COMMIT** | Enqueue `scratch.Commits` on the provided `IFrameCommitBatch` | Extra stage execution after commit; capturing the batch for callbacks |

Async I/O (`SerialDataHandler.DataReceived`, TCP completions) **only appends to a pending buffer**. The next SAMPLE copies that buffer into `scratch.IngressChunk` (or equivalent). That copy **is** today’s Sampling policy (usually drain-all of what arrived). That is what makes the tick **replayable**: the ordered sample is the input log; `Tick` is a pure-enough reducer. Do not parse, fuse, or apply overflow inside the I/O callback.

```
I/O thread / callback          Host frame (OnHostFrame)
─────────────────────          ────────────────────────
bytes → pending[]              SAMPLE: pending → scratch.IngressChunk
device matched → flag            + EnqueueEvent("device-match" | "port-open" | …)
                               TICK:   FrozenPlan.Tick  (HSM then stages)
                               COMMIT: scratch.Commits → IFrameCommitBatch
                                          ↓
                               SceneCommitService concatenates batches → applicator
                                          ↓
                               ReplicaManager.Tick() (post-commit)
```

### Minimal example

```csharp
public struct DemoScratch
{
    public byte[] Ingress;
    public int Parsed;
    public List<ISceneCommitRequest> Commits;
}

public sealed class ParseStage : IStage<DemoScratch>
{
    public string Id => "parse";
    public void Execute(ref DemoScratch s) =>
        s.Parsed = s.Ingress.Length;
}

public sealed class PublishStage : IStage<DemoScratch>
{
    public string Id => "publish";
    public void Execute(ref DemoScratch s)
    {
        if (s.Parsed > 0)
            s.Commits.Add(/* sealed scene op */);
    }
}

const string EvGo = "go";

var plan = new ComponentGraph<DemoScratch>("demo")
    .Add(new ParseStage())
    .Add(new ComponentGraph<DemoScratch>("out")
        .Add(new PublishStage()))
    .Hsm(h => h
        .State("Idle")
        .State("Run", "parse", "out")
        .On("Idle", EvGo, "Run"))
    .Compile();

var scratch = new DemoScratch { Ingress = [], Commits = new() };
var rt = new PlanRuntime();
plan.InitRuntime(ref rt);          // Idle → no stages enabled

plan.Tick(ref scratch, ref rt);    // still Idle: Parsed stays 0

rt.EnqueueEvent(EvGo);
scratch.Ingress = recordedBytes;   // SAMPLE from a journal, not from the wall clock
plan.Tick(ref scratch, ref rt);    // Run: parse then out/publish
```

`StageOrder` here is `["parse", "out/publish"]`. Feed the same `Ingress` + the same event sequence into a fresh plan → same `Parsed` / `Commits` (modulo documented float or time fields).

### Module example: serial graph

A typical I/O module authors a nested graph (decode / publish), an HSM (`Idle → Opening → Streaming | Fault`), and scratch for ingress bytes plus `Commits`. The plugin implements `IPlugin` + `IDeterministicFrameParticipant`. `OnHostFrame` is SAMPLE (pending bytes + HSM events) → `Tick` → COMMIT (`IFrameCommitBatch`). Stages stay free of scene writes.

```csharp
public void OnHostFrame(in FrameContext context, IFrameCommitBatch commits)
{
    SampleIngress(ref _scratch, ref _rt);   // pending serial bytes + HSM events
    _plan.Tick(ref _scratch, ref _rt);      // HSM + enabled stages
    Commit(ref _scratch, commits);          // batch.Enqueue only
}
```

### Determinism and replay

A frozen plan is a **fixed program**: declaration-order stages, FIFO HSM, **per-state compiled masks**. Logical replay (same ordered inputs → same domain outputs) follows if you keep I/O and clocks **outside** `Execute`. The enabled subset after a tick depends only on the resulting HSM state, not on the path taken to reach it.

| Do | Why it replays |
| -- | -------------- |
| Freeze once in `OnInit`; tick the same `FrozenPlan` | Stage order and HSM table never change while ticking |
| Declare activation on `State(...)`, not on `On(...)` | `M = f(S)`; retries and alternate paths cannot leak stages |
| SAMPLE then TICK then COMMIT, once per `frameId` | Matches the host commit window; observers see post-commit state |
| Record `(frameId, ingress bytes, HSM events)` | That tuple is the journal for this plugin |
| Scene mutations only on `IFrameCommitBatch` | Single writer; golden tests hash the applied scene |
| Assert `plan.StageOrder` in tests | Refactors that shuffle `Add` order are visible |

| Don't (breaks replay) | Why |
| --------------------- | --- |
| Parse or publish from `DataReceived` | OS callback order is not a frame |
| `DateTime.UtcNow` / RNG inside `Execute` | Observation time belongs in SAMPLE or an injected `IFrameClock` |
| Direct `ISceneManager` writes from a stage | Bypasses the commit window and participant order |
| Rely on dictionary iteration or extra threads inside `Tick` | Hidden ordering |
| Hidden queue / Rx channel behind `Connect` | Second bus; temporal state is not in scratch or the SAMPLE journal |
| Change sampling without an HSM event | `Policy_t` leaves `State_t`; two replays can diverge |

**Replay:** a module may persist an append-only SAMPLE journal (ordered ingress, frame id, logical time, config hash) and replay it on `IsolatedSceneReplayHost` with no live I/O. Keep journals off shared/untrusted storage. See [DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md).

`Ape.Core.Determinism` (`FrameContext`, `IngressBuffer`, `FrameProcessingHost`) and `Ape.Core.Graph` compose: the host owns **frame id and commit**; the graph owns **ordered stages inside that frame**. `IDeterministicPipelineStage` is optional metadata for composing *processors*; `IStage<TScratch>` is the flattened unit *inside* a `FrozenPlan`. Do not mix the two in one loop.

### Checklist

- [ ] One `ComponentGraph<TScratch>` frozen in `OnInit`; tick only the `FrozenPlan`
- [ ] Plugin implements `IDeterministicFrameParticipant` and registers on `IDeterministicHostTick`
- [ ] SAMPLE copies ingress; TICK is `plan.Tick`; COMMIT uses `IFrameCommitBatch`
- [ ] Stages write scratch (and maybe private buffers), not the scene
- [ ] HSM events come from SAMPLE (`EnqueueEvent`), not from ad-hoc threads during `Execute`
- [ ] Test: `StageOrder` is the nested declaration order
- [ ] Test: `plan.Describe()` (or `ExplainStage`) matches the intended HSM activation
- [ ] Test: recorded SAMPLE + Tick → same commits (golden or structural diff)

---



## Concurrency (summary)

Plugins run on **separate threads**. Scene objects use `ApeReplica.SyncRoot`. New code should submit scene mutations via the commit pipeline ([DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md)). Graph plugins still **SAMPLE / TICK / COMMIT** on the host frame — do not treat the plugin thread (`OnRun`) as the reducer. See [CONTRIBUTING.md](CONTRIBUTING.md) § Concurrency and thread safety.

---



## See also

- [README.md](README.md) — documentation index
- [CONTRIBUTING.md](CONTRIBUTING.md) — style and commits
- [DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md) — logical replay and commit pipeline
- [NETWORKING.md](../src/Ape.Core/docs/NETWORKING.md) — replication, subscriptions, file chunks

**Code:** `src/Ape.Core/Graph/` (`ComponentGraph`, `FrozenPlan.Describe` / `ToDot`, `GraphCompileException`, `PlanRuntime`, `IStage`); module graphs live under each `Ape.Module.*/Graph/`.

**This 3-tier architecture ensures clean separation: Core provides framework, Services provide infrastructure, Plugins provide application logic.**
