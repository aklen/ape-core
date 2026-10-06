# Contributing

Guidelines for the **Ape** platform (ape-skeleton workspace and its checkouts). Style is aligned with [.NET Runtime coding conventions](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md). Apply gradually; no big-bang reformat.

For Ape, **who may write authoritative state, from which thread, under which lock, how cancellation propagates, and which operations are deterministic** matter more than naming trivia.

---

## 1. Code style

### Principles

- **Consistency** — follow established patterns in the area you touch.
- **Readability** — code is written for humans first.
- **IDE-agnostic** — rules should not depend on a specific formatter.

### Naming

| Element | Convention | Example |
|---------|------------|---------|
| Classes, types | PascalCase | `NetworkManager` |
| Interfaces | `I` + PascalCase | `IEventManager` |
| Methods, properties | PascalCase | `StartServer`, `LocalPeerId` |
| Private instance fields | `_camelCase` | `_logger`, `_connectionCount` |
| Locals, parameters | camelCase | `configPath` |
| Constants | PascalCase | `DefaultPort` |

`s_` / `t_` prefixes for static / thread-static fields are optional; `_camelCase` is enough for Ape.

### Files and namespaces

- **File-scoped namespaces** preferred (.NET 6+).
- **One primary public top-level type per file**; filename matches that type. Nested types and small closely related internal helpers in the same file are fine.
- Namespace should generally follow the logical module/package structure and normally match the folder hierarchy (`Ape.Core.Network`, `Ape.Module.Example.Services`). Do not rename a public namespace solely because a file moved.
- Module layout: see [ARCHITECTURE.md](ARCHITECTURE.md) § Module folder layout.

### Formatting

- **4 spaces**, no tabs.
- **Allman braces** (opening brace on its own line).
- ~100–120 characters per line (flexible).
- `var` when the type is obvious from the right-hand side:

```csharp
var stream = new FileStream(path, FileMode.Open);
var replica = (ApeReplica)value;

EvaluationResult result = evaluator.Evaluate(context);
```

### Language preferences

- Prefer `readonly` fields where possible.
- Prefer immutable data for messages, commands, and results.
- Prefer `record` for value- or message-like immutable types where appropriate.
- Prefer init-only properties for configuration/model objects when mutation is not required.
- Avoid the null-forgiving operator (`!`) unless the invariant is externally guaranteed and documented.
- Use `nameof(...)` instead of string literals for member and parameter names.
- Use `is null` / `is not null` for null checks.
- Prefer pattern matching when it improves readability.
- Do not require `ConfigureAwait(false)` everywhere. Ape is not a UI framework; decide per context if a `SynchronizationContext` actually matters.

### Nullable reference types

Project uses nullable reference types — annotate with `?` consistently.

### XML documentation

Required for **externally consumable** public APIs and **non-obvious** public contracts (ownership, threading, determinism, exceptions). Avoid comments that only repeat the member name.

```csharp
/// <summary>
/// Transfers authoritative ownership of the replica.
/// </summary>
/// <remarks>
/// The caller must hold the replica write lock.
/// </remarks>
/// <exception cref="InvalidOperationException">
/// Thrown if ownership has already been transferred.
/// </exception>
```

Use `<summary>`, `<param>`, `<returns>`, `<remarks>`, `<exception>` where they add semantics.

### Dependency injection

- Constructor injection preferred.
- `GetRequiredService<T>()` when the service is mandatory; `GetService<T>()` when optional.

---

## 2. API design

- Public surface is a contract: prefer additive changes; breaking changes need a `BREAKING CHANGE` footer (see § Git commits).
- Validate arguments at public API boundaries (`ArgumentNullException.ThrowIfNull(config)`).
- Keep types small and focused; put domain types in the owning module, not Core, unless they are framework primitives.

---

## 3. Async and cancellation

- Suffix async methods with `Async`, except:
  - well-known names such as `StartAsync` / `StopAsync`;
  - interface implementations and overrides that must match the existing member name.
- Do not use `async void` except for event handlers.
- `CancellationToken` is normally the last parameter.
  - Use `= default` on convenience / public APIs where cancellation is genuinely optional.
  - Omit the default in infrastructure and pipeline code where cancellation **must** propagate.

```csharp
Task RunAsync(SimulationContext context, CancellationToken cancellationToken = default);

Task ExecuteAsync(EvaluationContext context, CancellationToken cancellationToken);
```

---

## 4. Concurrency and thread safety

Ape plugins run on **separate threads**. Scene graph objects inherit locking via `ApeReplica.SyncRoot`.

1. **Document** when a type is thread-safe.
2. **Keep critical sections short** — do not hold locks during I/O or logging.
3. **Avoid nested locks** on different nodes (deadlock risk). When multiple locks are unavoidable, define and document lock ordering (typical: Scene → Node → Component).
4. **Never invoke unknown / user / plugin code while holding a lock** (callbacks, events, virtuals that leave the type).
5. Concurrent collections (`ConcurrentDictionary`, `ConcurrentQueue`) make **individual** operations thread-safe; they do **not** make multi-step transitions atomic. Prefer `GetOrAdd` over check-then-add; factories passed to `GetOrAdd` must be side-effect-safe (they may run more than once).
6. Prefer **RAII-style** scoped locks (`WithLock`, `LockNode`) for multi-step mutations.

```csharp
// Good: short critical section
node?.WithLock(n => n.Position = newPos);

// Bad: lock held during slow work
node?.WithLock(n =>
{
    n.Position = newPos;
    _logger?.LogInfo($"Updated to {newPos}");
});

// Bad: unknown code under lock
node?.WithLock(n => callback(n));
```

---

## 5. Determinism and state mutation

Authoritative scene writes go through the **commit pipeline** ([DETERMINISM.md](../src/Ape.Core/docs/DETERMINISM.md)). Direct `ISceneManager` mutation from plugin threads is legacy.

Graph plugins still **SAMPLE / TICK / COMMIT** on the host frame — do not treat the plugin thread (`OnRun`) as the reducer. See [ARCHITECTURE.md](ARCHITECTURE.md).

---

## 6. Error handling

- Use exceptions for exceptional failures, not normal control flow.
- Do not swallow exceptions.
- Preserve the original exception as `InnerException` when wrapping.
- Prefer domain-specific exceptions only when callers can meaningfully act on them.
- Validate public API arguments at boundaries.

```csharp
ArgumentNullException.ThrowIfNull(config);
```

---

## 7. Tests

- Cover contracts and regressions next to the code (`src/Ape.Core/Tests/`, `src/Ape.Modules/Ape.Module.*/Tests/`).
- Prefer tests that lock ordering, commit windows, and cancellation propagation — not only happy-path wiring.
- Run the relevant test `.csproj`, not the whole solution.

---

## 8. Git commit messages

### Format

```
<type>: <short description>

[optional body — wrap at ~72 chars]

[optional footer]
```

- **Short description:** imperative mood, ≤50–72 characters, no trailing period.
- **Language:** English.

### Types

| Type | Use |
|------|-----|
| `feat` | New feature |
| `fix` | Bug fix |
| `docs` | Documentation only |
| `refactor` | Behaviour-preserving restructure |
| `perf` | Performance improvement |
| `test` | Tests |
| `build` | Build, CI, dependencies |
| `style` | Formatting, no logic change |
| `chore` | Miscellaneous |

### Examples

```
feat: add QUIC transport support
fix: load audio UI defaults from config
docs: consolidate platform documentation
refactor: extract transport factory from NetworkManager
```

### With body

```
fix: handle role vs mode config key in network setup

Read both "role" and "mode" from network config, preferring
"role" for backward compatibility.
```

### Optional scope

```
feat(network): add GetPreferredLocalIPAddress for discovery
fix(replica): ownership validation on delete
```

### Breaking changes

Use a `BREAKING CHANGE:` footer and/or `!` after the type (or type+scope):

```
feat(network)!: change StartServer return type

BREAKING CHANGE: callers must check the return value.
```

### Rules

1. One logical change per commit.
2. Imperative mood: "Add feature", not "Added feature".
3. No `WIP` / meaningless messages.
