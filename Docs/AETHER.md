# Aether reducer

Normative for the code under `Aether/`. Trees, checkpoints, disk, and transport are not implemented here. Working notes outside this repository are not the contract.

## What this slice does

One reducer folds writes from any peer. A frozen frame calls that same reducer. Tests call it directly and through the frame, and they assert handwritten results.

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

`StampLocal` assigns the next Lamport by incrementing the local clock, and a per-actor sequence. It is not a received operation. `Apply` with clock observation, on a newly admitted operation, sets:

```text
clock = max(clock, receivedLamport) + 1
```

That includes a received Lamport that is already behind the local clock. The addition is rejected when the clock is already `long.MaxValue` or the received Lamport is `long.MaxValue`. Rejection happens before the clock, the window, or any record changes. `StampLocal` uses the same check. An exact duplicate still in the window does not move the clock. An evicted id is no longer a duplicate, so a replay observes the clock again, and the field version still decides the stored value. The clock is not part of the resolved-view convergence claim. Field versions are. Two peers that receive the same new operations in a different order can stamp their next local write at different clocks.

## Snapshot

`Capture` returns the clock, the raw records, the dedup window, tombstones, visibility, and publication membership. It does not return a resolved sum or a rendered label.

`MergeImage` checks the whole image before it changes the clock, the window, or any record. That check includes a digest mismatch, a value of the wrong shape, an unknown field, and every `(key, version)` collision against the store or against another row in the same image. Visibility and membership use that same rule. A higher row does not hide a conflict on a lower version. Row order does not change the result. A rejected image leaves the reducer unchanged. After the check, `MergeImage` raises the clock to the image clock when that is greater. It does not add one to the restored clock. It then writes the highest version of each key from that same checked set, unions tombstones, and unions the window, trimming back to capacity. A tombstone is only added. An image that lacks one does not clear it. A newer value wins whether it arrives in the image or in a later operation. The destination must already define every field in the image. An id still in either window keeps the same-digest rule after the merge.

## Lifecycle

`DeleteEntity` stores a tombstone for that `entityId`. A later field write, `HideShared`, `RestoreShared`, `WithdrawPublication`, `Publish`, or an older snapshot leaves the tombstone in place. The raw field values stay. The shared view does not.

`HideShared` and `RestoreShared` are one visibility record per entity. The greater `(lamport, actorId)` wins. Equal version with the opposite flag is rejected before any change. Hiding leaves the raw contribution and the sum in the store. With no visibility record, the entity is shared-visible.

`Publish` and `WithdrawPublication` are one membership record per `(entityId, publicationId)`. The greater version wins. Republishing is a `Publish` with a higher version. A snapshot taken while the publication was a member does not undo a later withdraw. Withdrawing one publication leaves the others, the fields, and the entity.

This slice does not check owner, delete, or hide lists. `writerId` is whoever built the operation.

## Frame

`AetherFrame.Compile` freezes two stages, `apply` then `resolve`, on one host tick. Both stages call the reducer. The frame does not grow per entity. Disk and sockets are outside the tick.

The resolve stage keeps three answers apart. `ResolvedDeleted` is the tombstone. `ResolvedSharedVisible` is the entity Hide/Restore record. `ResolvedMember` is membership of the selected `PublicationId`. The shared label and sum require a publication id, membership in it, a living entity, and shared visibility. A missing or empty `PublicationId` leaves that shared output blank. It does not show every field. Raw fields stay in the reducer.

## What this slice does not do

- No scene writes, network, relay, or authentication. `writerId` is whoever built the operation.
- No owner, delete, or hide lists, policy cutover, or parent edges.
- `keepConflicts`, `coordinate`, and `treeMove` are rejected at field definition.
- No shared checkpoint yet. After an id leaves the dedup window, a rewritten payload is not detected. A checkpoint later proves coverage and can reject a covered id without the original digest. An unchanged replay is still held by the field version.
- No retained operation payloads, so `CanReplay` is false. Digest memory does not become a delta.
