# Client flag: skip snapshot, start with catchup

## Context
Today every handshake with a missing replica ends in `SnapshotRequired` (`Replication.Core\Owner\SubscribeSession.cs:113-120`). The client wants to say "ignore snapshot, run catchup". Catchup already streams all rows newest-first and works from zero cursors, so for a fresh client it is a valid alternative to a snapshot.

## Analysis: each owner reject reason (SubscribeSession.cs:113-120)

| Case | Flag can skip snapshot? | Why |
|---|---|---|
| `IsNullOrEmpty(start.InstanceId)` (line 113) | **Yes** | Owner accepts with all cursors = 0 and tells client its `instance_id` in the first catchup message. Zero cursors are already exempt from `behind` (line 112), so this is the same state `EmptyReplica` mode builds (`SyncAgent.cs:761-790`). |
| `InstanceId != store.InstanceId` (115) | Out of scope | User fixed "same instanceId". Flag ignored, snapshot as before. Cursors from another history are not comparable. |
| `behind`: cursor < `purged_version` (117) | **No** (fallback to snapshot) | Purged tombstones are gone, so deletes in that range never reach the client. Deleted rows would live forever. Catchup cannot detect them. |
| `minC > head` (119) | **No** (fallback to snapshot) | Client claims to be ahead (owner DB rolled back). Accepting hides new versions below the cursor. |
| Schema mismatch (line 92) | Untouched | Separate `SchemaMismatch` path, runs before any of this. |

Note: a client that already has an `instance_id` can already request catchup-from-scratch by sending zero cursors. So the only real protocol gap is the empty-`InstanceId` case: the client has no way to learn the instance id without `SnapshotRequired`.

## Design
Flag is a hint, never an override. Owner skips snapshot only when it is provably safe (empty InstanceId). Everything else behaves as today.

1. `Replication.Core\Protos\sync.proto`
   - `Start`: add `bool skip_snapshot` (new field number).
   - New server message `Accepted { string instance_id }`, added to the server oneof. Sent as the very first message when the owner accepted a skip-snapshot start with empty `instance_id`. Old clients/servers ignore the unknown field/message.
2. `SubscribeSession.cs` `HandshakeAsync`
   - If `start.SkipSnapshot && IsNullOrEmpty(start.InstanceId)`: treat the client cursors as all zero (ignore any sent), skip the reason chain, take the accept path (lines 121-137 with zero cursors: mirror, `SaveCursors`, `MarkSubscribed`, same transaction), and send `Accepted{_store.InstanceId}` before the first catchup step.
   - Otherwise unchanged. Reasons at 115/117/119 still send `SnapshotRequired` even with the flag.
3. `Replication.Core\Client\SyncAgent.cs`
   - New option, e.g. `Options.SkipSnapshot` (bool, default false). `BuildStart` (line 395) sets `SkipSnapshot` from it.
   - `SessionAsync` (line 326): add a branch for first message `Accepted`. In one transaction: set `_sync_instances.instance`, save zero cursors for every table, set `_instance`, reset `_cursors`. Reuse the EmptyReplica block from `SnapshotFlowAsync` (761-790) by extracting a helper; do not delete existing client rows in this path (see open point). Then continue as Catchup.
   - `SnapshotRequired` still handled by `SnapshotFlowAsync`, so the fallback for the other cases is automatic (no loop: the flag is only honoured for empty InstanceId, and after `Accepted` the client has an instance).
4. Outbox: `BuildBatchAsync` returns nothing while `_instance is null` (`SyncAgent.cs:623`), so pending outbox waits until `Accepted` sets the instance. Do not skip the outbox flush that `SnapshotFlowAsync` does (741-750) in the fallback path.

## Open point (decide before implementing)
"Client DB with some data" and empty InstanceId: catchup only upserts. Rows the client holds that no longer exist on the owner are not removed (never-seen deletes have no tombstone). Default in this plan: keep and merge (upsert wins, stale rows stay). Alternative: wipe client rows of the old instance first, like `EmptyReplica` (loses unsynced local data unless outbox is replayed). Recommend merge by default; wipe only if the client states its data is disposable.

## Tests (follow existing test project patterns)
- Empty InstanceId + flag: no `SnapshotRequired`, `Accepted` first, client ends with instance set and all rows via catchup.
- Empty InstanceId, no flag: still `SnapshotRequired`.
- Flag + wrong instance, flag + cursor < purged, flag + cursor > head: still `SnapshotRequired`, snapshot flow completes.
- Flag + schema mismatch: `SchemaMismatch` unchanged.
- Pending outbox on a replica-less client with flag: delivered after `Accepted`.
- Old server (ignores flag) → client falls back to snapshot without loop.

## Verification
`dotnet test` for the solution under `D:\Projects\desktop-app-replication\src`, plus `dotnet format --verify-no-changes` (repo requires it, see commit 17f9251).
