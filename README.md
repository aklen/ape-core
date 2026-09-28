# Ape.Core

**Tier 1 framework library for the Ape platform** — scene graph, replication, networking, events, configuration, runtime loading, and component graphs.

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

Ape.Core is the foundational layer of [Ape](https://github.com/MTASZTAKI/ApertusVR): a cross-platform, event-driven framework for building distributed applications with a shared scene model and network-synchronized replicas. It continues the ideas of ApertusVR with modern .NET.

This repository is **extracted from the former monolithic `apertus-csharp` tree** and is intended to live inside the [ape-skeleton](https://github.com/your-org/ape-skeleton) workspace at:

```
ape-skeleton/
└── src/
    └── Ape.Core/          ← this repository
```

The skeleton repo provides the launcher, CLI (`ape`), modules, solution file, and build orchestration. **Ape.Core** is the library every module and host references.

---

## Role in the platform

Ape uses a **3-tier architecture**:

| Tier | Package | Role |
|------|---------|------|
| **1 — Core** | **Ape.Core** (this repo) | Built-in services: logging, events, scene, replication, network, config, plugin/service loaders, component graphs |
| **2 — Services** | `Ape.Module.*` / `Ape.Service.*` | Optional infrastructure loaded from DLLs (`IService`) |
| **3 — Plugins** | `Ape.Module.*.Plugin.*` | Application logic loaded from DLLs (`IPlugin`) |

Core services are always registered in DI at startup and cannot be disabled via configuration. Modules and plugins reference **Ape.Core** and optionally other modules.

For the full platform overview, build commands, and module layout, see the **ape-skeleton** repository and its `docs/` folder.

---

## What's in this repository

```
Ape.Core/
├── Ape.Core.csproj
├── Config/           # JSON config parsing, module configuration
├── Determinism/      # Frame journals, logical replay primitives
├── Event/            # Thread-safe pub/sub (IEventManager)
├── Graph/            # ComponentGraph → FrozenPlan execution model
├── Logging/          # ILogger abstraction + console transport
├── Network/          # INetworkManager, LiteNetLib & QUIC transports
├── Replication/      # ApeReplica, delta sync, subscription demos
│   ├── Plugins/      # Core demo plugins (subscription samples)
│   └── Samples/      # Host JSON configs for replica demos
├── Runtime/          # ApeSystem, PluginManager, ServiceLoader
├── Scene/            # Scene graph, commit pipeline, ISceneRead
├── Tests/            # Unit tests (Ape.Core.Tests)
└── Utils/
```

**Runtime entry point:** `ApeSystem.Start(configPath)` — used by `Ape.Launcher` in the skeleton repo, or by any custom host that references this library.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- **(macOS, QUIC only)** `brew install libmsquic`

---

## Building standalone

You can build and test this repo on its own without the full skeleton checkout:

```bash
# Build the core library
dotnet build Ape.Core.csproj -c Release

# Run tests
dotnet test Tests/Ape.Core.Tests.csproj -c Release
```

To run demos (subscription server/client, etc.), use an **ape-skeleton** checkout with Core synced under `src/Ape.Core/`:

```bash
# In ape-skeleton (after cloning this repo into src/Ape.Core/)
./ape sync    # or your submodule / clone workflow
./ape build
./ape run -c src/Ape.Core/Replication/Samples/subscription-server.json
```

See [Replication/Samples/README.md](Replication/Samples/README.md) for replica subscription demos.

---

## Referencing from a module

Add a project reference from any `Ape.Module.*` project:

```xml
<ProjectReference Include="..\..\Ape.Core\Ape.Core.csproj" />
```

Or from the skeleton solution:

```bash
dotnet add src/Ape.Modules/Ape.Module.Example/Ape.Module.Example.csproj reference src/Ape.Core/Ape.Core.csproj
```

Plugins implement `IPlugin` and receive core services via DI in `OnInit()`. Scene writes go through the deterministic commit pipeline (`IFrameCommitBatch`), not direct property assignment from plugin threads. See skeleton `docs/ARCHITECTURE.md` and `docs/DETERMINISM.md`.

---

## Key dependencies

| Package | Purpose |
|---------|---------|
| [LiteNetLib](https://github.com/RevenantX/LiteNetLib) | Default UDP transport |
| [MessagePack](https://github.com/neuecc/MessagePack-CSharp) | Binary serialization for replicas |
| Microsoft.Extensions.DependencyInjection | Service container |

---

## Documentation

Platform-wide documentation lives in **ape-skeleton** (`docs/`):

- **ARCHITECTURE.md** — tiers, module layout, component graphs, commit pipeline
- **DETERMINISM.md** — frame model, replay, scene commits
- **NETWORKING.md** — replication, subscriptions, transports
- **DETERMINISM.md** / **NETWORKING.md** — linked from skeleton root

Module-specific config reference: [Config/Docs/Module-Configuration.md](Config/Docs/Module-Configuration.md).

---

## License

Copyright (c) 2026 Akos Hamori.

Licensed under the [Mozilla Public License 2.0 (MPL-2.0)](https://www.mozilla.org/MPL/2.0/).
