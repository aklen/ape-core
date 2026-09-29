# Ape.Core.Config — Overview

## Purpose

**`Ape.Core.Config`** turns JSON into a tree and helps the engine and modules read it.

- **`IConfigNode` / `ConfigNode`** — hierarchical in-memory model (objects, arrays, primitives).
- **`IConfigManager`** — parse any JSON file into a `ConfigNode` (startup file or a module’s own file).
- **`IStartupConfig`** — the process’s once-parsed startup JSON snapshot (`null` if started without a path).
- **`IModuleTable` / `ModuleTable`** — the **`modules`** map on a tree: `enabled`, plugin and pluggable-service DLL names. No domain rules for feature modules. See **[Module-Configuration.md](./Module-Configuration.md)**.

Runtime implementations (`ConfigManager`, `StartupConfig`) live under **`Services/`**.

## Folder layout (within `Config/`)

| Area | Role |
|------|------|
| **`Common/Models`** | Tree (`ConfigNode`), `modules` map (`ModuleTable`). |
| **(Config root)** | `ConfigModuleIds.cs` — stable id for this subsystem under `modules["…"]`. |
| **`Common/Interfaces`** | Startup snapshot (`IStartupConfig`), configuration manager (`IConfigManager`). |
| **`Common/Utils`** | Placeholder for small, stateless helpers specific to config parsing or keys (optional). |
| **`Services/`** | `ConfigManager` (JSON → `ConfigNode`), `StartupConfig` (wraps parsed root for `IStartupConfig`). |

Public **namespaces** remain **`Ape.Core.Config`**, **`Ape.Core.Config.Models`**, and **`Ape.Core.Config.Services`**.

## Data flow

1. At startup, **`ConfigManager.LoadJson`** parses the process JSON (if a path is given) into a **`ConfigNode`**.
2. **`IStartupConfig`** is registered as a singleton holding that root (or `null` when no file is used).
3. **`ConfigManager`** is registered as **`ICoreService`**: on **`Initialize`**, it copies **`IStartupConfig.Root`** into **`StartupRoot`** so callers can use either **`IConfigManager`** or **`IStartupConfig`** for the process tree.
4. Feature modules read **`modules["…"]`** via **`IModuleTable.GetModuleSection`**, or load a separate JSON with **`IConfigManager.LoadJson`**.

## Design notes

- **Single parse** of the startup file avoids inconsistent snapshots; extra files are loaded only through explicit **`LoadJson`** into caller-supplied nodes.
- **`IModuleTable`** sorts plugin and pluggable-service assembly names lexicographically so bootstrap order is declarative.

Logging, replica subscription, and network options live under **`modules["Ape.Core.*"]`**. Root-level `logging` / `subscription` / `authentication` objects are not read.
