# Host module configuration

This document defines how **feature modules**, **plugins**, and **pluggable services** are declared in host JSON. It describes the intended contract for the host bootstrap and for tests that need to enable or disable specific components without maintaining parallel top-level lists.

For the broader config subsystem, see [Overview.md](./Overview.md).

---

## 1. Single source: the `modules` object

Host configuration uses a **`modules`** object whose keys are **module identifiers** (for example `Ape.Core.Logging`, `Ape.Module.Example`).

**Target layout:** discovery and activation are driven **only** from this tree. Root-level **`plugins`** and **`services`** arrays are **not** used for bootstrap in this model; listing what to load is expressed under each module entry instead.

Each key under `modules` means: *this module ID participates in configuration*. The value is an object that may be empty or may contain module options, nested **`plugins`**, and nested **`services`**.

---

## 2. Module presence and options

### 2.1 Declaring a module

If a key exists under `modules`, the module is **defined** in configuration. The object may be:

- **Empty** — `"{}"` — the module is configured with defaults where applicable.
- **Non-empty** — arbitrary options for that module (for example `"level": "debug"` for logging).

Example:

```json
"modules": {
  "Ape.Core.Logging": {
    "level": "debug"
  },
  "Ape.Core.Event": {}
}
```

Both entries declare their respective modules; the second uses an empty object to mark participation without overriding defaults.

### 2.2 Disabling a module: `enabled`

If the module object contains **`"enabled": false`**, the host **does not load** that module, even though it remains listed under `modules`. Other keys in the same object may still be present for documentation or for tooling; they are ignored for activation while `enabled` is `false`.

Example — network module present in JSON but not loaded:

```json
"Ape.Core.Network": {
  "enabled": false
}
```

This is intended for **tests** and staged rollouts: keep the structure and options in place, but flip a single flag to skip loading.

**Default:** If `enabled` is omitted, it is treated as **`true`** (subject to host rules for unknown module IDs).

---

## 3. Plugins under a module

Plugins are declared under **`modules[moduleId].plugins`** as an **object**. Keys are **plugin keys** (typically short names such as `Demo`); values are objects with plugin-specific options.

### 3.1 Loading a plugin

If a plugin key exists and is not disabled (see below), the host resolves the plugin assembly from the key (for example short key → `{moduleId}.Plugin.{key}`) and loads it according to host rules.

Example — load a plugin with options:

```json
"Ape.Module.Example": {
  "plugins": {
    "Demo": {
      "endpoint": "127.0.0.1:9000",
      "verbose": false
    }
  }
}
```

### 3.2 Disabling a plugin: `enabled` on the plugin object

If the **plugin** object contains **`"enabled": false`**, that **plugin is not loaded**. The **parent module** may still load; only the plugin entry is skipped.

Example — module loads, plugin does not:

```json
"Ape.Module.Example": {
  "plugins": {
    "Demo": {
      "enabled": false,
      "endpoint": "127.0.0.1:9000",
      "verbose": false
    }
  }
}
```

Use this to exercise module code paths without loading a specific plugin, or to disable hardware-facing plugins in CI.

---

## 4. Services under a module

**`modules[moduleId].services`** follows the **same pattern** as plugins: an object whose keys identify service entries (as declared by each module), and whose values are option objects.

- If a service object includes **`"enabled": false`**, that **service entry is not loaded** from disk or not registered, per host rules, while the **module** may still load.
- Omitting `enabled` implies **enabled** for that entry.

Exact service key semantics (DLL name vs. short name) remain defined by each module and the host loader; the **`enabled`** flag applies uniformly at the **per-entry** level.

---

## 5. Summary table

| Location | `enabled: false` effect |
|----------|-------------------------|
| `modules[moduleId]` | Module is **not** loaded. |
| `modules[moduleId].plugins[key]` | That **plugin** is not loaded; module may still load. |
| `modules[moduleId].services[key]` | That **service** entry is not loaded; module may still load. |

---

## 6. Rationale

- **One tree** under `modules` avoids duplicating the same identifiers in root **`plugins`** / **`services`** arrays and per-module blocks.
- **Per-level `enabled`** supports precise tests: disable a whole module, or only a plugin or service, without removing configuration or commenting out large JSON blocks.

---

## 7. Migration note

Root-level **`plugins`** and **`services`** arrays are **not** read by the host. All plugin and pluggable-service DLL discovery uses **`modules[*].plugins`** and **`modules[*].services`** only.

Plugins whose assemblies are not tied to an `Ape.Module.*` id (for example **`Ape.Plugin.*`**) may be listed under a dedicated module entry such as **`Host.Plugins`** using **full assembly base names** as object keys (keys containing **`.`** are used as-is).

Legacy configs must be updated accordingly; see module **`Samples/`** folders and Core **`Replication/Samples/`** for subscription demos.

---

## 8. Replication (`Ape.Core.Replica`)

**`ReplicaAccessControlService`** reads **`modules["Ape.Core.Replica"]`**. If that section is missing, replica updates are broadcast (no path filter).

Supported shape (matches subscription demos under **`Replication/Samples/`**):

```json
"Ape.Core.Replica": {
  "subscription": {
    "paths": [ "/PingNode" ],
    "mode": "include"
  },
  "authentication": {
    "rules": [
      {
        "path": "/PingNode",
        "whitelist": {
          "participant-or-peer-id": { "privileges": [ "read" ] }
        },
        "blacklist": []
      }
    ]
  }
}
```

- **`subscription.paths`**: path patterns for who receives replica updates (`include` vs `exclude` via **`mode`**). Omit **`subscription`** entirely for broadcast behaviour (no filtering).
- **`authentication.rules`**: optional; **`whitelist`** maps participant ids to privilege sets; **`blacklist`** lists peer ids denied for that rule path.

Clients that only subscribe in-process may omit **`Ape.Core.Replica`** or use an empty object; servers that enforce paths/auth should define **`subscription`** (and optionally **`authentication`**) as above.
