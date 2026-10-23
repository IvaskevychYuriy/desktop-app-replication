# Client decides the snapshot; drop `_ownerInstance` and `SnapshotMode`

## Context

Today `ResolveInstanceAsync` runs once before `Start()` and sets two fields:

- `_ownerInstance`: only a "resolved" marker for the guard in `Start()`.
- `_instance`: null when there is no replica of the owner yet. In that case `BuildStart` sends an empty `instance_id`, and the owner decides about the snapshot in `SubscribeSession.HandshakeAsync`.

The owner can answer in two ways:
- `SnapshotRequired`: the client's `SnapshotMode` then picks a file snapshot or an empty replica.
- `Accepted`, when `skip_snapshot` is set.

Goal:
- `_instance` is always the owner's id, resolved on every session.
- The cursors are the stored ones or zeros, and zeros are written to `_sync_cursors`.
- The client decides itself whether to take a snapshot. The default is to stream.
- `SnapshotMode` disappears: zero cursors already are the "empty replica".
- `RequestSnapshot()` cancels the stream, takes a snapshot, then continues.

This also fixes two problems:
- **A different owner on the same address.** Today `StartEmptyReplicaAsync` deletes the old instance's rows (`SyncAgent.cs:849`).
- **No agent while the owner is down.** Today `ConnectAsync` throws when the owner cannot be reached.

## Step 0

Copy this plan to `src/plans/rework-instance-snapshots.md`. Plan mode allows writing only the plan file, so this is the first implementation step.

## Protocol (`Replication.Core/Protos/sync.proto`)

This is a breaking change. Existing instances are upgraded and restarted manually.

- `IdentityReply`: add `int32 schema_version`, `int64 head`, `int64 purged_version`, `int64 snapshot_size_bytes`. Update the comment.
- `Start.instance_id`: always set. Update its comment.
- Remove `Start.skip_snapshot` (field 6), the `Accepted` message, and `ChangeMessage.accepted` (field 8). Mark the freed numbers as `reserved 6;` and `reserved 8;`.

## Owner

- `SyncGrpcService.Identity`: fill the new fields from `OwnerStore.Head`, `Purged`, `FileSizeBytes`, and `Options.SchemaVersion`.
- `SubscribeSession.HandshakeAsync`: remove `_adopt`, the `"репліки нема"` branch, and the `Accepted` send in `RunAsync`.
- Keep the remaining checks (schema, other instance_id, cursor < purged on a non-empty table, `minC > head`) as a safety net. The reason: a purge batch can run between `Identity` and `Start`, and only the owner's transaction (check plus `MarkSubscribed`, 10.2) sees that race. In the normal case the client has already decided the same way, so the owner never answers `SnapshotRequired`. When it does, the client handles it the same way as its own decision.

## Client (`Replication.Core/Client/SyncAgent.cs`)

### Remove

- `_ownerInstance` and the guard in `Start()`.
- The `SnapshotMode` enum.
- From `AgentOptions`: `SnapshotMode`, `SnapshotFileThreshold`, `AssumedDownBytesPerSecond`.
- The Auto/eta block in `SnapshotFlowAsync`.
- The `Accepted` branch in `SessionAsync`.
- `SkipSnapshot` from `BuildStart`.

### Keep and add

- Keep `AgentOptions.SkipSnapshot`: with it set, the client never takes a snapshot on its own.
- Add `AgentOptions.CatchupSnapshotThreshold` (`long?`, default `null` = off): take a snapshot when `head - MinCursor` is above it.
- Add `private volatile bool _snapshotRequested`.

### `AdoptInstanceAsync(string id, ct)`

Replaces the body of `ResolveInstanceAsync`. Under `_gate`, if `id != _instance`:
- `_instance = id`.
- If `HasInstance` is false, then in one transaction call `ClientStore.SaveInstance(conn, tx, id, null)` and `ClientStore.SaveCursor(conn, tx, id, t, new CursorState(0))` for every table.
- `_cursors = LoadCursors(id)`.

### `SessionAsync`

1. Call `IdentityAsync` on the session channel, then `AdoptInstanceAsync`.
2. If `identity.SchemaVersion != Options.SchemaVersion`, set the `SchemaMismatch` state, log, and return. The wait loop in `RunAsync` stays. A schema mismatch never triggers a snapshot: the file has the owner's schema, which the client app does not have.
3. Decide whether to take a snapshot, in this order:
   - `_snapshotRequested`, which comes from the explicit `RequestSnapshot()` and wins over `SkipSnapshot`;
   - `!SkipSnapshot` and all cursors are empty (cursor 0, no ranges);
   - `!SkipSnapshot`, `CatchupSnapshotThreshold` is set, and `head - MinCursor` is above it;
   - a refill is required: a non-empty cursor is below `purged_version`, or `MinCursor > head`. Without `SkipSnapshot` this means a snapshot. With `SkipSnapshot` it means `ResetReplicaAsync()` (below) and then stream.
4. Snapshot: `await SnapshotAsync(client, ct)`, clear `_snapshotRequested`, `continue`. The next round goes to `Subscribe` with the fresh cursors.
5. `Subscribe` with `Start{instance_id=_instance, cursors}`. A `SnapshotRequired` reply (the race path) takes the snapshot the same way, or the reset when `SkipSnapshot` is set.

### Snapshot and reset

- `SnapshotAsync(client, ct)`, which is the old `SnapshotFlowAsync` without the mode: `DownloadAsync`, then `InstallSnapshot`, then `LoadCursors`. `InstallSnapshot` drops the queue of the instance and logs it (`ReplicaWriter.cs:214`). That is the intended behavior and stays. The docs are updated to match (below).
- `ResetReplicaAsync()` is the old `StartEmptyReplicaAsync(instance)` without the parameter. It deletes the rows of `_instance` and sets the cursors to 0. It is used only for a required refill with `SkipSnapshot`. It also drops the outbox of the instance, the same way `InstallSnapshot` does. This was added during implementation: the queued actions point at rows the reset deletes, and with them left in place the multi-owner test never settled.

### Public API

- **`public void RequestSnapshot()`**: `_snapshotRequested = true; _sessionCts?.Cancel(); _linkSignal.Set();`, the same pattern as `SetSchemaVersion`. The stream stops, `RunAsync` starts a new session, the snapshot is taken, then the stream continues. While the link is off, the flag waits for the next session.
- **`ResolveInstanceAsync`**: stays public as an optional warm-up (Identity, then `AdoptInstanceAsync`). Its doc says it is not needed before `Start`.

## Callers, tests, docs

- `Sample.Lab/ClientReplication.cs` `ConnectAsync`: wrap `ResolveInstanceAsync` in try/catch (best-effort). `Start()` always runs. Update the summary.
- `Sample.Lab/TwoOwnerLab.cs`, `Sample.Lab/Scenarios.cs`, `Replication.Tests/MultiOwner/TwoOwnerRig.cs`: remove `SnapshotMode` and use `SkipSnapshot` where `EmptyReplica` was used.
- Rewrite tests:
  - `Replication.Tests/EmptyReplicaTests.cs`, `Replication.Tests/Integration/SkipSnapshotTests.cs`: a new client with `SkipSnapshot` streams from zero, and `InstanceId` is known right after Identity.
  - `Replication.Tests/Integration/AgentNameTests.cs:40`: `Start` without Resolve reaches Online.
- New tests:
  - `RequestSnapshot` on an Online agent: the snapshot is installed, the unsent queue is dropped, the agent is Online again.
  - A different owner on the same address: A's rows are kept, B is filled.
  - `CatchupSnapshotThreshold`.
  - The owner guard: a purge after Identity still gives `SnapshotRequired`.
- `README.md`, `design/sync-design.md` 6.2/6.3/10.3:
  - Update the handshake table (lines 345-347): the client decides from Identity, and the owner guards.
  - Remove `SnapshotMode`/EmptyReplica, `skip_snapshot`, and `Accepted`.
  - Line 346 "спершу відправляє чергу" becomes: the snapshot replaces the replica, and the unsent queue of the instance is dropped.

## Verification

- `dotnet build`.
- `dotnet test Replication.Tests`, all of them.
- Sample.Lab scenarios.

## Revision: the owner decides again

The owner still has to guard the handshake, so the snapshot decision moved back to the owner. Only the instance resolution stays on the client.

- `Start.skip_snapshot` (field 6) is restored. `IdentityReply` carries only `instance_id` again. `Accepted` stays removed: the client knows the instance from Identity.
- `HandshakeAsync` adds `"репліки нема"` → `SnapshotRequired` when all cursors are empty, `skip_snapshot` is unset, and `head > 0`. With `head = 0` there is nothing to snapshot, and a zero-version snapshot would loop.
- The client answers `SnapshotRequired` with a snapshot. With `SkipSnapshot` it resets the replica instead (rows, outbox, cursors 0). If `SnapshotRequired.instance_id` is not `_instance`, the client takes no action and the next round adopts the new owner.
- `CatchupSnapshotThreshold` and the client-side rules (`RefillReason`, the Identity schema check) are removed.
- An on-demand snapshot (`RequestSnapshotAsync`) was added and then removed: the client takes a snapshot only on `SnapshotRequired`. Its design was public with no flag. It throws when the link is off. It cancels the session, takes the `_session` semaphore that `RunAsync` holds for each session, downloads and installs the snapshot, releases the semaphore, and wakes the loop.
