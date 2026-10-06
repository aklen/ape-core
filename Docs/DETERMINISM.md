# Determinism, Commit Windows, and Replay

**Status:** Living document  
**Audience:** Ape.Core and Ape.Modules implementers

Part I defines the normative model; Part II is the implementation checklist.

**Related:** [ARCHITECTURE.md](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md) · [ARCHITECTURE.md — component graphs](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#component-graphs-apecoregraph) · **Primitives:** `Ape.Core.Determinism`, `Ape.Core.Graph`

---

# Part I — Concepts and target behaviour

This document defines **what “deterministic behaviour” means** for the **Ape** modular, event-driven platform: **Ape.Core** (runtime, scene, replication, plugins) and **Ape.Modules.*** (plugins, pluggable services, module-local registration). It clarifies the relationship to **distributed systems theory**, describes the **current** architecture at a high level, and proposes **how determinism and replay should be implemented** as the system matures.

**Audience:** system designers and implementers working on **Ape.Core**, **`Ape.Module.*`** plugins and services, scene/replica pipelines, and operational logging.

**Related:** [`ARCHITECTURE.md`](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md) (tiered Core / services / plugins / [component graphs](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#component-graphs-apecoregraph)), [ARCHITECTURE.md — scene entities](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#scene-entity-migration) (modular scene entities and registry). **Code rollout:** [`DETERMINISM.md#part-ii--implementation-checklist`](./DETERMINISM.md#part-ii--implementation-checklist) (step-by-step implementation checklist). **Primitives:** `Ape.Core.Determinism` (`IFrameClock`, `FrameContext`, `IngressBuffer`, `FrameProcessingHost`, …) and `Ape.Core.Graph` (`ComponentGraph`, `FrozenPlan`).

---

## Where the product lives: Launcher vs Core vs Modules

| Layer | Role |
|-------|------|
| **`Ape.Launcher`** | **Launcher only**: reads config, builds the DI container, registers **Ape.Core** `ICoreService` implementations, loads **`IPluggableService`** / **`IPlugin`** types from module DLLs beside the process, runs the main loop until shutdown. It should stay **thin**—no substitute for Core abstractions. |
| **`Ape.Core`** | **The platform**: `ISceneManager`, `IReplicaManager`, `INetworkManager`, `IEventManager`, `PluginManager`, config merge (`ModuleTable`), registry-backed entity creation, MessagePack replication paths, etc. Deterministic processing contracts **belong here** (or in shared abstractions Core defines). |
| **`Ape.Modules.*`** | **Feature verticals**: optional **`IPlugin`** assemblies, optional **`IPluggableService`** types in module assemblies, and module **`ICoreService`** hooks (e.g. scene entity factory registration). Domain-specific DTOs and behaviour live in modules, not in the launcher. |

Replay, ordering guarantees, and journaling are **product concerns of Core + modules**; the **launcher** only calls `ApeSystem.Start` with a config. Network **host** means the scene participant/server, not this executable.

---

## 1. Executive summary

- A **fully deterministic distributed system** (all nodes bit-identical given the same inputs) is **not a realistic goal** for the stack today: wall clocks, floating-point arithmetic, thread scheduling, and asynchronous I/O all break bit-level replay without strict discipline.
- **Official guarantee (migrated pipelines):** asynchronous input enters a frame only through an explicit ingress contract; participant execution is stably ordered (`FramePhase` → `Order` → `ParticipantId`); Scene mutation happens on a frame-scoped batch through a single COMMIT applicator. A participant failure discards the batch without Scene writes; an **applicator failure can leave partial Scene writes**, is marked `MayHavePartialSceneWrites`, skips replication, and halts the host. **Plugin DI** uses an explicit Core capability allowlist and permits interfaces whose assembly name starts with `Ape.Module.*` (prefix, not loader attestation; host must only load configured module DLLs beside the process). Container, Scene-write, and host-tick capabilities are denied. Replica setters, create/remove, binder, and deserialize require **`SceneMutationScope`**. `ISceneRead` still returns live objects; scalar/property setters throw outside apply/network restore. **Remaining bypass: in-place mutation of mutable reference-valued properties, including collections and nested model objects** (mutable reference-valued properties are the remaining bypass). Given the same module versions, compiled plans, initial state, configuration, and ordered **`SampleFrame`** sequence (including **`LogicalTime`**), Ape produces the same ordered commit sequence and Scene state sequence when apply succeeds. Independent live callback schedules are not that guarantee. Full logical replay still needs Scene-output comparison. Pluggable services that still call `ISceneManager` outside apply scope now throw.
- A **practical target** is **logical replay**: the same **ordered stream of domain inputs** (commands, sensor datagrams, tick indices) processed by the **same version** of deterministic **pure** logic yields the **same observable scene state and decisions** modulo explicitly documented tolerances (e.g. float rounding).
- **“Commit window”** here means a **bounded processing phase** in which inputs are **ordered**, applied **atomically** to authoritative state (or to a single writer), and optionally **snapshotted** or **journaled**—not a Git commit.
- **Frame participants** run in a total order **`FramePhase` → `Order` → `ParticipantId`**. Duplicate ids are a configuration error. Plugin assembly name is not the sort key.
- **Domain-level “intent”** (what a user or policy assigns to an entity or command in a given module) is **not** the same as a **processing frame** (what the engine does in one scheduler turn). This document uses **processing frame** / **commit** for engine semantics to avoid overloading “intent.”
- **Authoritative scene mutations** are possible only through a **frame-scoped `IFrameCommitBatch`** handed to `OnHostFrame`. Async callbacks use **`IIngress<T>`**, not the scene. Only the Core applicator writes `ISceneManager`; replication is post-commit.

---

## 2. Is this a “deterministic distributed system”?

**Partially related, but not the same label.**

| Concept | Typical meaning | Ape relevance |
|--------|------------------|-------------------|
| **Deterministic distributed algorithm** (Paxos, Raft, etc.) | Agreement and ordering under failures | Relevant if multiple writers or failover; **not** the main focus of single-process replay today |
| **Causal / total order of events** | Every observer sees a consistent order | Important for **replication** (`ReplicaManager`, network fan-out) |
| **Deterministic simulation / reducers** | Same input sequence → same state | **Primary** alignment for **logical replay** and audit |
| **Eventually consistent views** | Different replicas converge | May apply to **observers**; authoritative scene should remain **single-writer** per partition |

**Conclusion:** The platform should be described as aiming for **deterministic processing of an ordered input log** on each authoritative node, with **well-defined replication semantics**, rather than claiming a classical “deterministic distributed system” in the formal sense unless those algorithms are explicitly introduced.

---

## 3. Definitions

### 3.1 Levels of determinism

1. **Bit-exact replay** — Byte-identical memory and outputs. Rarely achievable without fixed RNG seeds, integer-only math, and a single thread.
2. **Logical / domain replay** — Same **canonical inputs** (with timestamps and ordering keys) → same **domain state** and decisions within tolerance. Suitable for **scene entities**, game/simulation state, automation rules, and audit trails.
3. **Weak reproducibility** — “Same config, similar load” — useful for smoke tests, **insufficient** for compliance or forensic replay.

**Recommendation:** Treat **logical replay** as the engineering contract; document where **float** or **time** breaks bit equality.

### 3.2 Processing frame and commit window

- **Processing frame** — One scheduler turn that may: dequeue pending work, run **ordered** plugin stages, update authoritative state, emit replication packets, append to a journal. **Frame ids always advance**, including discarded/failed frames (`41` ok → `42` failed → `43` ok). The failed id stays in the audit log (`HostFrameRecord`); it is not reused.
- **Commit window (collection atomicity)** — If any `OnHostFrame` throws, **no** collected batch is applied and **`ReplicaManager.Tick` is skipped**. Core batch/scope state is clean for the next frame. **Participant-owned scratch, HSM, and counters are not rolled back** (modules must treat a failed frame as “logic ran, scene did not”).
- **Failed-frame input** — Default after participant failure: **consumed**. SAMPLE/drain already happened; the outcome row is `Failed` (or `Discarded` for strict multi-writer); the next live frame continues with new ingress. Safety pipelines may set `modules["Ape.Core.Scene"].failedFrame` to **`stop`** (`FailedFramePolicy.StopPipeline`). Applicator failures always stop because the Scene may have changed partially. Input is not retried as the same `SampleFrame` unless a replay harness does that explicitly.
- **Scene transaction atomicity** — **Not** guaranteed today if `SceneCommitApplicator.Apply` throws mid-batch (`op1`/`op2` may already have mutated the scene). The outcome is persisted as `Failed` with `MayHavePartialSceneWrites=true`, and the host halts. Missing-target sets currently warn and skip that op. Full atomic apply needs prevalidation, copy-on-write, or inverse ops (later). Do not equate collection discard with a Scene transaction.

### 3.3 “Intent” disambiguation

| Term | Meaning |
|------|--------|
| **Domain / user intent** | What a module’s model expresses (e.g. operator command, classification, goal state). Defined per feature, not by Core. |
| **Processing frame (this doc)** | One engine turn: ingest → apply ordered transitions → publish side effects. Prefer naming **phases** or **stages** in code (e.g. `Ingest`, `Transform`, `Publish`) instead of overloading “intent.” |

### 3.4 Six normative invariants

1. External and async code may write only **`IIngress<T>`** (per-source stream). Not `ISceneManager`, not a process-wide commit sink.
2. Each ingress stream has a **source identity** (`SourceId` + session **epoch**) and an **`IngressOrdering`**. Identity is `stream + epoch + sequence`. Named epochs are **never reused** on the same stream. `ResetSession` **drops pending** input. Packets from a retired/other epoch are **dropped**. **SourceSequence and ObservationTime require `sourceEpoch` on every enqueue**; CaptureOrder may omit it. Protocol streams default to **`IngressSequencePolicy.StrictlyIncreasing`** across drains. Alternatives: `UniqueButOutOfOrder` (bounded recent window), `WrappingCounter` (16-bit serial numbers: modulus 65536, forward iff modular delta ∈ (0, 32768); identity uses an **extended** sequence so wrap is not a duplicate of the previous cycle). **Reconnect is a new epoch, not a wrap.** `CaptureOrder` stays unique-within-drain only.
3. **SAMPLE** copies ingress into scratch (and optionally a **`SampleFrame`** journal: `FrameId` + per-input source id, sequence, observation time, payload or hash/CAS). That tuple is the replay input, not the OS callbacks.
4. **TICK** reads only scratch, on one thread, with a frozen stage order (`FrozenPlan`).
5. **COMMIT** writes only the **`IFrameCommitBatch`** the host opens for that participant call (host thread, sealed when `OnHostFrame` returns). If any participant throws, **the whole frame is discarded** (no scene apply, no replica tick).
6. The authoritative Scene is mutated **only** by the Core applicator after concatenating batches in participant order; **`ReplicaManager.Tick()`** is post-commit and skipped when the frame was discarded.

Logical time is **`FrameContext.LogicalTime`** via **`LogicalFrameTime.FromFrame(frameId, epoch, duration)`**. Journal header minimum: **`logicalEpoch`**, **`frameDuration`**, **`timeScale`**, **`configHash`**. Changing host Hz without the header is a different clock. Pause/step belong in the frame input/state, not wall-clock side effects. Reducers must not read `DateTime.UtcNow` / `SystemFrameClock`.

Production multi-writer configs should set `modules["Ape.Core.Scene"].multiWriter` to **`strict`**. Dev default remains last-writer-wins with a warning. `failedFrame` defaults to consume-and-continue; **`stop`** halts `IHostFrameRunner.RunNextFrame` after a non-applied frame.

Opt-in host outcome persistence: `modules["Ape.Core.Scene"].outcomeJournalPath` creates a new append-only JSONL sidecar (the path must not exist). It records `Applied`, `Discarded`, or `Failed` for each host frame and flushes every row. `HostFrameOutcomeJournal.VerifyAlignment` checks frame ids and logical times against a SAMPLE journal captured over the same frame interval; it does not compare Scene contents or prove that the two files belong to the same binary/configuration. An applicator exception records `MayHavePartialSceneWrites=true` and stops the host even under consume-and-continue. If writing an outcome fails after apply, the host stops and reports that the Scene may already have changed.

**Scene replay comparison pilot:** `SceneStateDigest.Capture` sorts all nodes and entities by identity and hashes their MessagePack-replicated payloads plus Scene lookup paths. `SceneReplayComparisonTests` reads a three-line fixture, persists/reads `SampleFrame`s, runs two cold hosts, aligns their outcome journals, and compares the Scene digest after **every** frame. The failed middle frame retains the preceding digest; a changed final property diverges. This is a schema/version-dependent digest of replicated Scene state, not a byte-level guarantee across runtime or module upgrades, nor a check of external side effects.

**Full-host replay:** Core `IsolatedSceneReplayHost` owns a fresh, network-free host and Scene. A module participant can feed a SAMPLE journal into the same processor used live. See Core tests for the contract.

**Next closings (priority):** (1) Eliminate in-place mutation of mutable reference-valued Scene properties (a frozen snapshot type is the pilot; remaining domain models still mutable). (2) Commit-batch prevalidation / transactional apply. (3) Remaining plugin and service writers (they now throw on direct Scene writes). (4) Extend journal/replay beyond the current IsolatedSceneReplayHost pilot: verify module/config versions and CAS payloads and cover live sensor capture.

```
callback → IIngress<T>
SAMPLE  → SampleFrame / scratch
TICK    → FrozenPlan
COMMIT  → IFrameCommitBatch (per participant)
apply   → SceneCommitApplicator
replica → post-commit
```

---

## 4. Current architecture (Core + modules; launcher omitted except wiring)

The following is grounded in the **current** codebase shape; it is the baseline for gap analysis. **`Ape.Launcher`** is only the process that **instantiates** this; the behaviour described lives in **Ape.Core** and loaded **Ape.Module.*** assemblies.

### 4.1 Tiers (product structure)

- **Tier 1 — Core (`Ape.Core`):** `ILogger`, `IEventManager`, `ISceneManager`, `IReplicaManager`, `INetworkManager`, `IConfigManager`, etc. Registered via **`ICoreService.Register`** when the launcher builds DI.
- **Tier 2 — Pluggable services:** Optional types from module DLLs beside the process (`modules[*].services`). Implement **`IPluggableService`** where applicable; long-running infrastructure (device stacks, bridges) often lives here.
- **Tier 3 — Plugins:** Loaded from module DLLs beside the process using **`modules[*].plugins`** (`ModuleTable.MergePluginSpecs`). Each plugin implements **`IPlugin`**; **`PluginManager` (`Ape.Core`)** runs each plugin on its **own thread** (`SupervisedPluginEntry`).

### 4.2 Modular configuration

- **`modules`** in JSON groups **network**, **services**, and **plugins** per module id ([ARCHITECTURE.md](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md)). Effective plugin list = resolved **`modules[*].plugins`** keys (lexicographic order over the merged set); see **`src/Ape.Core/Config/Docs/Module-Configuration.md`**.
- **Scene entity factories** are registered by **`Ape.Module.*`** `ICoreService` implementations; the launcher only **selects** which registration services to wire into DI—**without** embedding feature logic. **`SceneManager`** uses **`ISceneEntityRegistry`**; it does not hard-code module types.

### 4.3 Main loop (typically driven by the launcher process)

- A simple **~100 ms** tick in the host process: `PollEvents` → **`IHostFrameRunner.RunNextFrame()`**. That is the only production path: `frameId++` → participants / apply-or-fail → **`ReplicaManager.Tick()`** only when the frame was **Applied**, networking is enabled, **and** a replica manager is present (`Replicated` is true only then). Plugins register via **`IFrameParticipantRegistry`**; they do not receive `IHostFrameRunner` / `RaiseHostFrame`. `IDeterministicHostTick.RaiseHostFrame` remains a Core-internal collection API. In-memory **`HostFrameRecord`** is a bounded ring (default 1000, `frameRecordLimit`); the full history belongs in the journal (Steps 10–11).

### 4.4 Events

- Property changes on replicas propagate through **setters** → **`PropertyChangedEvent`** → per-plugin queues (`DrainEventsFor`). Ordering is **per plugin queue**, not a global total order of all mutations.

### 4.5 Implications

- **Plugin `OnInit` DI** uses an explicit Core capability allowlist and permits module-service interfaces whose **assembly name** starts with `Ape.Module.*` (a prefix, not a loader attestation). Trust assumes the host only loads configured module DLLs beside the process. Container, Scene-write, and host-tick capabilities are denied, including via `IEnumerable<T>`. A module service may still wrap a writer. `ISceneRead` / `IFrameParticipantRegistry` are façades. Writes go through the frame-scoped **`IFrameCommitBatch`**. Replica setters, create/remove, binder, and deserialize require **`SceneMutationScope`** (commit applicator or network restore). Remaining bypass: in-place mutation of mutable reference-valued properties, including collections and nested model objects. **Pilot:** replace a whole nested model through commit rather than mutating it in place.
- **Pluggable services** still receive the **root** container, so they can still resolve `ISceneManager`.
- Production **network** and **replica** ticks follow **`IHostFrameRunner.RunNextFrame`**: replica tick is post-commit and only on **Applied** frames.

### 4.6 Component graphs (intra-plugin reducer)

`Ape.Core.Graph` is how a plugin turns nested authoring (`ComponentGraph<TScratch>`) into a **frozen linear plan** ticked once per host frame. Authoring happens at **initialization**; the plan does not grow during ticks. It does **not** replace the commit window; it is the deterministic work **inside** `IDeterministicFrameParticipant.OnHostFrame`.

Contract (see [ARCHITECTURE.md — Component graphs](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#component-graphs-apecoregraph)):

1. **SAMPLE** — copy ingress (bytes, flags) into scratch; enqueue HSM events. I/O callbacks only fill a pending buffer.
2. **TICK** — `FrozenPlan.Tick`: drain queued HSM events as control microsteps (mask = A(final state)), then enabled stages in **declaration order**. Intermediate states on that event path do not execute in this frame.
3. **COMMIT** — enqueue `ISceneCommitRequest`s on the host-provided **`IFrameCommitBatch`**. Stages do not write the scene.

Same recorded SAMPLE tuple + same compiled plan → same scratch / commits (**logical replay**). `StageOrder` is part of the contract; tests should assert it. Graph modules in `Ape.Module.*` are the reference shape.

Async I/O stays **outside** the reducer (`Async world → SAMPLE → Tick → COMMIT → Async world`). Rate mismatch is a SAMPLE policy first, not an intra-plan Hz split. Temporal buffers (queues, batches) must be named scratch, not a hidden channel — [port contracts](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#port-contracts-type--freshness--sampling--delivery).

---

## 5. Sources of non-determinism (to control explicitly)

| Source | Risk | Mitigation direction |
|--------|------|----------------------|
| **Multiple plugin threads** | Races on scene / shared state | Single-writer scene thread, or strict locks + ordered submission queue |
| **Async I/O** (TCP, timers) | Datagrams arrive in OS-dependent order | Timestamp + **sequence** at ingress; **single** ingress queue per stream |
| **`DateTime.UtcNow` / `Stopwatch`** in logic | Different replay times | Split **observation time** (source/event) vs **processing time** (frame); inject a **clock** in tests |
| **Floating-point** | Cross-platform drift | Document tolerance; deterministic tests with fixed seeds where needed |
| **Dictionary enumeration** | Unstable iteration order | Never rely on it for plugin execution order |
| **Logging side effects** | Confused with state | Structured logs with **frame id** / **correlation id**; do not treat log order as causal proof |
| **Plugin load order** | Implicit if not specified | **Explicit** ordered list or **topological** sort from declared edges |

---

## 6. Target behaviour: how it *should* be implemented

Normative for **Ape.Core** and **modules**; the launcher should only **configure and start** the result.

### 6.1 Authoritative state and single writer

- **One** logical **writer** for the authoritative scene graph per process (or per shard): **Core code** running inside the **commit window** (the frame dispatcher / reducer). Plugins and services **never** call `ISceneManager` (or underlying mutable scene APIs) for writes; they **only enqueue** work.
- **Read-only** access (queries, snapshots for rendering or debug) may be exposed separately with an explicit **read** contract; it must not double as a write path.

### 6.1.1 Enforcement (not merely convention)

The intended end state is **architectural**, not “please remember to”:

- **`IPlugin` and `IPluggableService` constructors / DI** must **not** receive injectable **scene write** capabilities. Writes happen on **`IFrameCommitBatch`** during `OnHostFrame` only.
- **Only** the **commit runner** holds the **mutable** `ISceneManager` reference and applies concatenated batches **once per frame**.
- **Async callbacks** (TCP completion, timers) **cannot** bypass this: they enqueue on **`IIngress<T>`**. They do not receive `IFrameCommitBatch`.

Until the codebase is migrated, legacy direct `ISceneManager` usage is **technical debt**; new code should target the submission API only.

### 6.2 Ingress: ordering and identity

- Every external message should carry: **source id**, **sequence** or **monotonic id**, and **when the phenomenon was observed** (vs when Core processed it—both may be logged).
- The **I/O layer** should enqueue **(connection id, payload)** into a **per-authoritative-pipeline** queue; **merge** rules (e.g. by priority) must be **documented**.

### 6.3 Commit window semantics

Within one **commit**:

1. **Assign** a monotonic **frame id** (logical time).
2. **Order** all pending inputs for that frame (total order, or deterministic merge of partial orders).
3. **Apply** state transitions in **plugin phase order** (see below).
4. **Emit** replication and side effects (network) **after** authoritative state is updated (or in a defined two-phase pattern).
5. **Append** an optional **journal record**: frame id, input hashes, resulting snapshot id or diff hash.

This matches **event-sourcing** and **simulation lockstep** practice without requiring the whole codebase to become pure functional.

### 6.4 Plugin ordering: phases and dependencies

**Do not** rely on merge order of `modules` JSON alone unless that order is **documented** as the **total order** and implemented deterministically in **`ModuleTable`** (or equivalent).

Preferred models (in increasing sophistication):

1. **Ordered list** — Plugin names are collected from **`modules`** and output in **lexicographic** order (see `MergePluginNames`).
2. **Phases** — Plugins declare `Phase = Ingest | Transform | Publish` (enum or string). Within a phase, order is fixed by config or name.
3. **DAG** — Plugins declare **dependencies** (e.g. `After: ["other-plugin"]`); **`PluginManager` or Core** topologically sorts; cycles are configuration errors.

**Within a commit window**, execution is **phase-serial, participant-serial**: `Ingest` / `Transform` (fusion) / `Publish`, then `Order`, then ordinal `ParticipantId`. Registration order is not the contract. If two participants write the same property in one frame, Core **warns**; the later participant still wins. That is reproducible, but usually a missing arbitration policy.

### 6.5 Services vs plugins (same scene-write rule)

- **`IPluggableService`** and **`IPlugin`** are symmetric for **authoritative scene changes**: both use **only** the **submission / commit** surface. There is **no** separate “service path” that writes the scene outside the commit window.
- Non-scene side effects (hardware I/O, outbound network not tied to replica state, local caches) remain module-owned but must not **mutate authoritative scene** except via the same queue.

### 6.6 Domain DTOs and policy versioning (modules)

- Replicated **module DTOs** should remain **serializable** and **versioned** (e.g. MessagePack) where they cross the wire.
- Any **scoring**, **classification**, or **derived** state that must replay identically should take **explicit inputs** (inputs + **policy / model version** + frame id). Modules own those types; Core provides timing and ordering hooks.

### 6.7 Distributed replication

- **`ReplicaManager`** batches **Update** packets on a tick. The **authoritative** node should produce **ordered** updates consistent with the **same** commit order observers would see in replay.
- **Causal ordering** across peers: if two peers can both write, you need either **partitioning** (single writer per entity) or a **consensus** layer—not assumed today.

---

## 7. Logging and audit

- **Structured fields:** `frameId`, `pluginId`, `entityId`, `sourceTime`, `ingestTime`, `correlationId`.
- **Separation:** **Decision logs** (what the engine concluded) vs **trace logs** (debug noise). Only the former need to align with replay guarantees.
- **Replay harness:** Feed **journal + inputs** into a **headless** process that loads **Core + the same modules**; **diff** scene snapshots or exported DTOs.

---

## 8. Phased implementation roadmap (suggested)

Incremental evolution alongside [ARCHITECTURE.md](https://github.com/aklen/ape-skeleton/blob/main/docs/ARCHITECTURE.md#scene-entity-migration); implementation targets **Ape.Core** first, then module contracts.

| Phase | Deliverable | Determinism impact |
|-------|-------------|-------------------|
| **A — Document** | Explicit plugin order in config; document thread model in Core | Removes hidden assumptions |
| **B — Serialize mutations** | Single ingress queue; plugins/services get **submission API only**—Core applies to `ISceneManager` inside the commit window | Eliminates races and bypass paths |
| **C — Frame id** | Monotonic `frameId` in the runtime loop; pass into plugins/services | Enables ordered audit |
| **D — Journal** | Optional append-only log of inputs per frame | Enables offline replay |
| **E — Replay CLI** | Load journal, disable network, compare outputs | Validates logical determinism |

---

## 9. Summary

- **Terminology:** Prefer **logical determinism** and **ordered commit / processing frames** over claiming a fully **deterministic distributed system** unless you implement the corresponding algorithms and invariants.
- **`Ape.Launcher`** is a **launcher**; **Ape.Core** owns platform semantics; **`Ape.Modules.*`** own features. Replay requires **explicit ordering**, a **single writer** inside each commit, and **no direct scene writes** from plugins or services—only **submissions** consumed by the **frame dispatcher** (see §6.1.1).
- **Modules** should version **policy** and **DTOs** where replay matters; **Core** should supply **frame identity**, **ordering**, and the **sealed submission API** for scene mutations.
- **Component graphs** (`FrozenPlan.Tick`) are the intra-plugin reducer: SAMPLE → TICK → COMMIT. Replay them by replaying the SAMPLE tuple, not by replaying OS callbacks.

---

## 10. Glossary

| Term | Definition |
|------|------------|
| **Authoritative state** | The single scene / entity store that replication and decisions treat as truth for a partition |
| **Commit window** | Atomic batch application of ordered inputs to authoritative state |
| **Frame id** | Monotonic logical counter for a processing turn |
| **Logical replay** | Re-running ordered inputs through deterministic logic to reproduce domain outcomes |
| **Processing frame** | One scheduler turn that may ingest, reduce, and publish |
| **FrozenPlan** | Flattened stage list + HSM, frozen at plugin init; the replayable program of a `ComponentGraph` |
| **SAMPLE / TICK / COMMIT** | Per-frame plugin phases: copy ingress, run the plan, enqueue scene ops |

---

*Document version: 1.7 — `SceneMutationScope` per-Begin lease; plugin DI = Core allowlist + `Ape.Module.*` name prefix (host-loaded modules); **`IFrameCommitBatch`**; participant order `Phase → Order → Id`.*

---

# Part II — Implementation checklist

This document turns [`DETERMINISM.md#part-i--concepts-and-target-behaviour`](./DETERMINISM.md#part-i--concepts-and-target-behaviour) into **concrete code steps** for **Ape.Core**, **Ape.Launcher**, and **Ape.Modules.***. It assumes the normative model in §6 (submission-only scene writes, commit runner, optional journal/replay).

**Scope:** single-process authoritative host first; multi-writer / consensus is out of scope unless explicitly added later.

---

## Current baseline (inventory)

| Area | Today | Gap |
|------|--------|-----|
| **Plugins** | `PluginServiceProvider`: Core allowlist + `Ape.Module.*` **name prefix** (host-loaded DLLs). Reads via **`ISceneRead`**. Writes via **`IFrameCommitBatch`**. Setters require **`SceneMutationScope`**. frozen snapshot models | Prefix is not attestation; module services may wrap a writer; other nested models/collections still mutable in place; unmigrated publishers **fail-fast** |
| **Pluggable services** | e.g. a pluggable service takes **`ISceneManager`** in constructor (root DI) | Same bypass as pre-facade plugins |
| **Host loop** | ~100 ms tick → `PollEvents` + **`IHostFrameRunner.RunNextFrame()`**; opt-in `HostFrameOutcomeJournal` | SAMPLE journal is opt-in; synthetic full-host Scene replay/diff is tested, live-capture replay is not yet |
| **Config** | `ModuleTable.MergePluginSpecs` — returns **lexicographically sorted** plugin keys | OK for deterministic ordering |

**Files that still write Scene outside COMMIT (non-exhaustive):** some pluggable services (root DI still has `ISceneManager`). Unmigrated plugins that still require Scene publish **throw in `OnInit`**.

---

## Guiding principles

1. **`ISceneManager` mutation** stays **inside Ape.Core** and is invoked **only** from the **commit runner** after draining the submission queue.
2. **Plugins and pluggable services** receive **`IIngress<T>`** for async input; scene writes use **`IFrameCommitBatch`** inside `OnHostFrame`. They receive **`ISceneRead`** / **`ISceneQuery`** (or a slim read-only façade) for **reads** if needed—**not** the full mutable manager.
3. **Async I/O** ends in **ingress enqueue**; never scene commits and never direct scene calls.
4. **Backward compatibility:** migrate **incrementally**; feature-flag or compile-time path only if necessary—prefer one-direction migration.

---

## Step 0 — Lock the contract (done / ongoing)

- Keep [`DETERMINISM.md#part-i--concepts-and-target-behaviour`](./DETERMINISM.md#part-i--concepts-and-target-behaviour) as the source of truth.
- This plan is the **execution checklist**; update both when decisions change.

### Step 0b — Shared primitives (`Ape.Core.Determinism`)

**Implemented in Core** (namespace `Ape.Core.Determinism`) as reusable building blocks for every level (plugin core, pipeline stage, future commit runner):

| Type | Role |
|------|------|
| `IFrameClock` / `SystemFrameClock` | Injectable time; avoid raw `DateTime.UtcNow` in logic. |
| `FrameContext` | `FrameId` + clock + optional `PolicyVersion`. |
| `IngressEnvelope<T>` | Payload + `Sequence` + optional source observation time. |
| `IIngress<T>` / `IngressBuffer<T>` | Thread-safe enqueue; `DrainOrdered()` for deterministic batches. |
| `IFrameProcessor<TIn,TOut>` | One batch in → `ProcessingResult<TOut>` out. |
| `ProcessingResult<TOut>` | Outputs + optional `ISceneCommitRequest` list (marker for future sink). |
| `FrameProcessingHost<TIn,TOut>` | Wires ingress + processor + clock; `RunFrame(frameId)` for host-driven ticks. |
| `DeterministicFrameLayerBase<TIn,TOut>` | Optional inheritance hook for `OnProcessFrame`. |
| `IDeterministicPipelineStage` | Metadata (`StageId`, `Order`) for composed **processors** (not `FrozenPlan` stages). |
| `TopologicalSort` | DAG ordering for dependency edges. |
| **`Ape.Core.Graph`** (`ComponentGraph`, `FrozenPlan`, `PlanRuntime`, `IStage<TScratch>`) | Nested authoring → flattened per-frame reducer. Compose **with** the types above: host frame + commit outside, graph tick inside. |

**Scene commit (implemented):** `Ape.Core.Scene.Commit` — `IHostFrameRunner.RunNextFrame` (Core-internal production tick), `IFrameParticipantRegistry` (plugin DI), `IFrameCommitBatch` (host-thread, sealed after `OnHostFrame`), `SceneCommitService`, `FramePhase` + `Order` + `ParticipantId` sort, concrete `ISceneCommitRequest` records, `SceneCommitApplicator`. Callbacks use `IIngress<T>` / `IngressOrdering`. Replay input type: `SampleFrame`. Pilots live in Core tests and graph modules.

Plugins and modules **compose or inherit** the Determinism types; extend **`ISceneCommitRequest`** for more scene operations as needed.

---

## Step 1 — Explicit plugin ordering in configuration

**Goal:** A **total order** of plugins is defined before any commit logic depends on it.

**Work:**

1. Merge rule is documented in **`Module-Configuration.md`** / **`ARCHITECTURE.md`**: plugins come only from **`modules[*].plugins`**; output list is **sorted** (ordinal).
2. Optionally add **`pluginOrder`** / **`phase`** in JSON later; for v1, **sorted keys + array order** may suffice.
3. Unit test: same config file → same ordered list of plugin assembly names.

**Deliverable:** deterministic ordered list consumed by `PluginManager` (or by the future scheduler).

**Depends on:** nothing.

---

## Step 2 — Core: submission API + sealed operations

**Goal:** Types that plugins can use **instead** of `ISceneManager` for writes.

**Work:**

1. Define **`IFrameCommitBatch`** (frame-scoped `ISceneCommitSink`) opened only for `OnHostFrame` on the host thread; **`IIngress<T>`** for callbacks.
2. Keep **`ISceneCommitRequest`** records (create/remove, generic property set).
3. Do **not** inject a process-wide enqueue sink into plugins for scene writes.

**Deliverable:** compilable Core API; no plugin wired to it yet.

**Depends on:** Step 1 (ordering conceptually independent, but product-wise you want order before batching plugin work per frame).

---

## Step 3 — Core: commit runner + frame id

**Goal:** One component owns **`frameId`** and **applies** queued operations to **`SceneManager`**.

**Work:**

1. Add **`ICommitRunner`** / internal **`SceneCommitService`** that on each tick:
   - `frameId++`
   - drains **`ISceneCommitSink`** (or processes one batch per frame—policy documented)
   - applies operations to **`ISceneManager`** in **queue order**
   - optionally notifies **`ILogger`** / metrics with `frameId`
2. **`ApeSystem`** tick loop (or Core `ICoreService`): **start** the runner on the same cadence as today’s loop (or merge with existing tick).
3. Pass **`frameId`** into structured logs where the runner applies work.

**Deliverable:** commits run in one place; **still** allow legacy direct `ISceneManager` use in parallel during migration.

**Depends on:** Step 2.

---

## Step 4 — Threading model: producer vs commit thread

**Goal:** Plugins may stay on **background threads**, but **all scene writes** go through the **sink**; the **runner** applies on a **defined** thread (often the same thread as the runner loop—document which).

**Work:**

1. Document: **producers** only call **`IIngress<T>.Enqueue`** (async) or **`IFrameCommitBatch.Enqueue`** (COMMIT); **never** `ISceneManager` from plugin threads after migration.
2. If UI or single-thread constraints appear later, optionally require **all** producers to marshal to one queue (already satisfied if they only enqueue).

**Deliverable:** documented threading contract; aligns with §6.1.1.

**Depends on:** Step 2–3.

---

## Step 5 — Migrate plugins off direct `ISceneManager` writes

**Goal:** Each plugin uses **`IFrameCommitBatch`** + **`ISceneRead`**; **`GetService<ISceneManager>()`** is null in plugin DI.

**Done (reference paths):** Core tests, replica subscription demos, graph-module SAMPLE/TICK/COMMIT.

**Still calling `GetService<ISceneManager>()` (now null):** unmigrated plugins must enqueue `ISceneCommitRequest`s like the reference paths. Generic property-set is `SetSceneNodePropertyCommitRequest` / `SetSceneEntityPropertyCommitRequest` — do not add one commit type per field.

---

## Step 6 — Migrate pluggable services

**Goal:** pluggable services do not take **`ISceneManager`** for writes.

**Work:**

1. Refactor those services to depend on **`ISceneCommitSink`** (and the read API if they query the scene).
2. Update their DI registration accordingly.
3. Verify device lifecycle still creates/removes replicas **only** via commit queue.

**Deliverable:** services symmetric with plugins for scene mutation (§6.5).

**Depends on:** Steps 2–5 (sink + operation set mature enough).

---

## Step 7 — DI enforcement (narrow the blast radius)

**Goal:** New code **cannot** accidentally take **`ISceneManager`** for writes.

**Plugin DI (done, not foolproof):** `PluginManager` wraps `OnInit` in **`PluginServiceProvider`**. Plugin DI uses an explicit Core capability allowlist and permits interfaces whose **assembly name** starts with `Ape.Module.*` (prefix only; trust = host loads configured module DLLs beside the process). Container, Scene-write, and host-tick capabilities are denied. `ISceneRead` is a façade. Replica setters / create / remove / binder / deserialize require **`SceneMutationScope`**. Plugins do **not** get a process-wide `ISceneCommitSink`. Core applicator still resolves write-capable `ISceneManager`.

**Still open:**

1. A module service may wrap `ISceneManager` (assembly-prefix trust). Untrusted modules would need an explicit plugin-visible marker.
2. Pluggable-service DI still has `ISceneManager` (Step 6); those writers now **throw** unless they go through apply scope.
3. Remaining bypass: in-place mutation of mutable reference-valued properties, including collections and nested model objects. **Pilot closed:** frozen snapshot models; other Scene models still mutable.
4. Optional: Roslyn **analyzer** or plugin-visible service marker instead of `Ape.Module.*`.

**Deliverable:** plugin-scope architectural guarantee matches §6.1.1; service DI still to migrate.

---

## Step 8 — Unify host tick: network poll, commit, replica tick

**Goal:** **Order** is: ingest network → **`IHostFrameRunner.RunNextFrame()`** (apply commits or discard/fail) → **`ReplicaManager.Tick()`** only after **Applied**, so outbound deltas reflect **post-commit** state.

**Work:**

1. `ApeSystem` host loop calls **`IHostFrameRunner.RunNextFrame()`** after `PollEvents`. Do not pair `RaiseHostFrame` with `ReplicaManager.Tick` in production.
2. Align **`ReplicaManager.Tick()`** with **post-commit** snapshot semantics (see §6.7 in main doc).

**Deliverable:** single documented tick pipeline (done for Core host loop).

**Depends on:** Step 3 minimum.

---

## Step 9 — Ingress ordering (network / TCP)

**Goal:** Each external message has **sequence** or monotonic id at the boundary; merged into the commit queue in **deterministic** order.

**Work:**

1. For each transport path, assign **`connectionId` + seq** (or wall + seq).
2. Document merge when multiple connections feed one host (priority / FIFO).

**Deliverable:** ordered ingress into **`SceneCommitSink`** or a dedicated **ingress queue** drained inside the commit runner.

**Depends on:** Steps 2–3; deep integration with **`INetworkManager`** as needed.

---

## Step 10 — Journal (optional persistence)

**Goal:** Append-only **journal** of `(frameId, operation list hash | payload)` for replay.

**Work:**

1. Behind a flag / interface **`ICommitJournal`**.
2. Implement file or in-memory ring buffer for development.

**Deliverable:** offline replay input source (Step 11).

**Depends on:** Step 3 (stable operation encoding).

---

## Step 11 — Replay harness (headless)

**Goal:** Load journal (or scripted inputs), run Core + modules **without** network, **diff** scene or exported DTOs.

**Work:**

1. Minimal CLI or test host: **feed** `SceneCommitOperation` stream in order.
2. Golden-file or snapshot diff for one scenario.

**Deliverable:** proof of **logical determinism** for a fixed scenario.

**Depends on:** Step 10 (or synthetic in-memory op list without journal file).

---

## Step 12 — Documentation and cleanup

**Goal:** **`ARCHITECTURE.md`** and plugin authoring notes describe submission API, ordering, and migration status.

**Work:**

1. Update **`ARCHITECTURE.md`** Tier 3 section: plugins use **`ISceneCommitSink`**, not raw scene manager.
2. Remove obsolete comments; mark any remaining legacy path **deprecated** with issue link.

**Depends on:** Steps 7–8.

---

## Summary: step count

| # | Step | Primary owner |
|---|------|----------------|
| 0 | Contract locked (`DETERMINISM.md#part-i--concepts-and-target-behaviour`) | Docs |
| 1 | Deterministic plugin list from config | Core config |
| 2 | `IFrameCommitBatch` + `IIngress<T>` | Core |
| 3 | Commit runner + `frameId` + host tick hook | Core + Host |
| 4 | Threading contract (enqueue only) | Docs + Core |
| 5 | Migrate plugins | Modules + samples |
| 6 | Migrate pluggable services | Modules |
| 7 | DI split / enforcement | Core |
| 8 | Tick order: network → commit → replica | Core + Host |
| 9 | Ingress sequencing | Core network |
| 10 | Journal | Core |
| 11 | Replay harness | Host or test project |
| 12 | Doc sync | Docs |

**Rough sequencing:** **1 → 2 → 3 → 4** in parallel with early spikes; **5–6** after sink covers required operations; **7** once migrations are mostly done; **8–9** tighten integration; **10–11** validation; **12** continuous.

---

## Risk notes

- **Operation explosion:** If every `ISceneManager` method becomes an operation, define **batch** / **scripted transaction** types early.
- **Performance:** Per-frame drain + apply; profile with realistic plugin load.
- **Third-party plugins:** Version **`ISceneCommitSink`** carefully; use **semantic versioning** for operation payloads.

---

*Version: 1.0 — aligns with `DETERMINISM.md#part-i--concepts-and-target-behaviour` v1.2.*

---

# Part III — Testing priorities

Suggested test layers (incremental):

1. **Core** — `ProcessingResult`, ingress ordering, `FrameContext` / `PolicyVersion` if used.
2. **Scene commit pipeline** — same `SetSceneNodePropertyCommitRequest` sequence → same final state (`SceneCommitApplicator`, path resolution edge cases).
3. **Host integration** — `IHostFrameRunner.RunNextFrame` + mock participants; `RaiseHostFrame` remains the unit-test collection surface; participant order documented and tested.
4. **Network / replica** — serialized diff equality (integration); golden snapshot tests for client-side logic.
5. **Property-based / fuzz** — random frame ids in range → stable invariants; long-run overflow checks.
6. **Golden vectors** — fixed commit stream file; refactors must match or intentionally bump golden.
7. **Component graphs** — `StageOrder` equals nested `Add` order; recorded SAMPLE + `Tick` → same scratch/commits (`GraphCompilerTests`).

**Priority:** applicator + scene state → host participant order → golden vectors → graph `StageOrder` / SAMPLE replay → network determinism (only if explicit goal).
