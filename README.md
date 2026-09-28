# Ape.Core

**Tier 1 framework library for the Ape platform** — scene graph, replication, networking, events, configuration, runtime loading, and component graphs.

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4)](https://dotnet.microsoft.com/)

Ape.Core is the foundational layer of **Ape**: a cross-platform, event-driven framework for building distributed applications with a shared scene model and network-synchronized replicas. It continues the ideas of [ApertusVR](https://github.com/MTASZTAKI/ApertusVR) with modern .NET.

In the [ape-skeleton](https://github.com/aklen/ape-skeleton) workspace this library is the `src/Ape.Core/` checkout:

```
ape-skeleton/
└── src/
    └── Ape.Core/          ← this repository
```

The skeleton provides the launcher, CLI (`ape`), modules, solution file, and build orchestration. **Ape.Core** is the library every module and host references.

---

## Role in the platform

Ape uses a **3-tier architecture**:

| Tier | Package | Role |
|------|---------|------|
| **1 — Core** | **Ape.Core** (this repo) | Built-in services: logging, events, scene, replication, network, config, plugin/service loaders, component graphs |
| **2 — Services** | `Ape.Module.*` / `Ape.Service.*` | Optional infrastructure loaded from DLLs (`IService`) |
| **3 — Plugins** | `IPlugin` types in `Ape.Module.*` | Application logic in module assemblies beside the process |

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
├── Replication/      # replica engine, delta sync, access control
├── Runtime/          # ApeSystem, PluginManager, ServiceLoader
├── Scene/            # Scene graph, commit pipeline, ISceneRead
├── Tests/            # Unit tests (Ape.Core.Tests)
└── Utils/
```

**Runtime entry point:** `ApeSystem.Start(configPath)` — used by `Ape.Launcher` in the skeleton repo, or by any custom host that references this library.

---

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) — verify with `dotnet --version` (must report `9.x`)
- **(macOS, QUIC only)** `brew install libmsquic`

This repo has **no `.sln` file** — build projects directly via their `.csproj` paths from the repository root.

---

## Building

### Core library

```bash
cd ape-core   # repository root

dotnet restore Ape.Core.csproj
dotnet build Ape.Core.csproj -c Release
```

**Output:** `build/bin/Ape.Core/Release/net9.0/Ape.Core.dll`

Build artifacts are centralized under `build/` via root `Directory.Build.props` (not next to each `.csproj`).

Debug build:

```bash
dotnet build Ape.Core.csproj -c Debug
# → build/bin/Ape.Core/Debug/net9.0/Ape.Core.dll
```

### Unit tests

```bash
dotnet test Tests/Ape.Core.Tests.csproj -c Release
```

Builds the test project (which references `Ape.Core.csproj`) and runs all tests in `Tests/`.

Replica **subscription demos** (IPlugin examples that exercise Core replication) live in **Ape.Module.ReplicaClientServer** under `src/Ape.Modules/`, not in this repository. The replica engine (`ReplicaManager`, access control, packets) stays here.

### Clean build artifacts

```bash
rm -rf build/
dotnet build Ape.Core.csproj -c Release
```

Legacy `bin/` / `obj/` folders under source trees (from older builds) can be removed the same way if present.

### Building from ape-skeleton

When this repo lives at `src/Ape.Core/` inside **ape-skeleton**, use the skeleton CLI instead:

```bash
./ape build core
```

To run replica subscription demos (module DLL next to the launcher — no `plugins/` folder):

```bash
./ape sync
./ape build
./ape run -c src/Ape.Modules/Ape.Module.ReplicaClientServer/Samples/subscription-server.json
```

See `src/Ape.Modules/Ape.Module.ReplicaClientServer/Samples/README.md`.

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

Module-specific config reference: [Config/Docs/Module-Configuration.md](Config/Docs/Module-Configuration.md).

---

## Acknowledgments

Ape continues ideas from [ApertusVR](https://github.com/MTASZTAKI/ApertusVR).
ApertusVR was originally co-developed by [Peter Kovacs](https://github.com/pkovacs86) and [Akos Hamori](https://github.com/aklen), with further contributions from:

- [Peter Kopacsi](https://github.com/kopacsipeter)
- [Erik Toth](https://github.com/ErikToth97)
- [Akos Rabely](https://github.com/akibaki97)
- [Mark Fekula](https://github.com/markf006)

---

## License

Copyright (c) 2026 [Akos Hamori](https://github.com/aklen).

Licensed under the [Mozilla Public License 2.0 (MPL-2.0)](https://www.mozilla.org/MPL/2.0/).
