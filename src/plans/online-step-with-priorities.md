# Online step: table priorities decide what is taken

Status: plan only, not implemented. Supersedes the earlier "reorder only" variant.

## Context
`CatchupStepAsync` takes work table by table in `PriorityTables()` order (`SubscribeSession.cs:219`): open tables and FK parents of open tables first. `OnlineStepAsync` takes the oldest `OnlineBatchRows` versions across all tables, so with a cap a closed table (e.g. `Log`) can fill the step while the open table (`Item`) waits. Goal: the cap is spent in priority order.

## Why this is possible without a protocol change
- Client already keeps a cursor per table and applies `Batch.Covers` per `b.Tbl` (`SyncAgent.cs:473`) and `ChangeMessage.Extra` for any table (`SyncAgent.cs:464`).
- `Head(V)` is the only part that assumes one watermark: `LiftTo(V)` lifts every table that has no open ranges (`CursorState.LiftTo`). So `Head` must carry `V = min over tables of sent_t`, never more.
- If each table's `Covers` starts exactly at that table's own `sent_t`, it touches the client cursor and merges, so no ranges build up in the online phase.

## Protocol
Checked `Replication.Core/Protos/sync.proto`: no new field, message or version bump is needed. `ChangeMessage.extra`, `Batch.covers` and `Batch.online` already carry per-table progress; only the meaning of `Head` is tightened.
- `Head.version` now means "every table is complete up to this version", i.e. the minimum over per-table progress. Before, the owner sent the single online watermark, which is the same value when all tables move together, so behaviour is unchanged for that case.
- Edit the comment on `message Head` in `sync.proto` to say this. Doc change only, no regeneration impact.
- Compatibility: old client with new owner works (client only uses `covers`, `extra`, `Head` as it does now). New client with old owner works (old owner never defers a table). No `SchemaVersion` change: that field versions the data schema, not the wire.
- Rejected: a new `TableProgress` message or per-table `Head`. It would duplicate `RangeExtra` and force both sides to upgrade together.
- Test: `Online_head_is_min_of_table_progress` (below) pins the `Head` contract.

## Design
Replace the single `_sentUpTo` by `Dictionary<string,long> _sent` (per table). At the end of catch-up set every `_sent[t] = _catchupHead` (all cursors were `Reset(head)` by `TableSynced`).

One online step, in a deferred read transaction, head `H`:
1. If `min(_sent.Values) >= H`: nothing to do, return false.
2. `budget = OnlineBatchRows`. For `t` in `PriorityTables()`:
   - `budget == 0` -> table untouched, `upTo_t = _sent[t]`.
   - else `rows = OwnerReader.Range(_sent[t], H, budget + 1, oldest first)`.
     - `rows.Count > budget`: take `budget` rows, `upTo_t = last taken version`, `budget = 0`.
     - else take all, `upTo_t = H`, `budget -= rows.Count`.
   - Own-origin rows count against the budget (as today) but are not sent.
3. Per table with `upTo_t > _sent[t]`:
   - has non-own rows: `Batch{Online, Covers=(_sent[t], upTo_t]}` as today (origin filter, `ToWire(t, it, _mirror[t].Cursor)` unchanged).
   - no non-own rows: add `RangeExtra{t, _sent[t], upTo_t}` to `_extra` (existing mechanism, attached by `SendAsync` to the next message), so the client cursor still moves.
4. Send batches in priority order, then `Head(min(upTo_t))` last.
5. After send, per table: `_mirror[t].AddRange(_sent[t], upTo_t)`, `_sent[t] = upTo_t`. Drop the global `LiftTo` loop; `AddRange` touching the cursor already lifts it.

Kept: throttle (`OnlineInterval` wait in `MainLoopAsync`), cap value, origin filter, oldest-first rows inside a batch, `_needFull` preemption.

Fixes a stall that exists today and gets worse here: `MainLoopAsync` only runs a step when woken or when `PRAGMA data_version` changed. After a capped step with backlog and no new writes nothing triggers the next step. Make `OnlineStepAsync` report backlog (`min(_sent) < H` after the step) and skip the wake/data_version gate while it is true (still honour the throttle).

FK safety: parents sit before children in `PriorityTables()` (stable `OrderBy`, model order), so a child row is taken only after its parent table is fully taken up to `H`; a deferred parent implies budget 0 and no child rows.

Known trade-off: a busy open table can use the whole budget every step and starve closed tables until it quiets (same as catch-up). No reserved share; add one only if asked.

## Files
- `Replication.Core/Owner/SubscribeSession.cs` (`_sentUpTo` -> `_sent`, `MainLoopAsync` online hand-off, `OnlineStepAsync`, doc comments)
- `Sample.Lab/Scenarios.cs`, `Sample.Lab/OwnerNode.cs`, `Sample.Lab/Lab.cs`
- `Replication.Tests/SubscribeSessionTests.cs`
- `Replication.Core/Protos/sync.proto` (comment on `Head` only)
- Client: no change.

## Tests
`Replication.Tests/SubscribeSessionTests.cs`, raw gRPC `Subscribe` like `Reader_fault_ends_the_stream_with_an_error`. Owner changes in one transaction. Record message sequence until `Head`.

1. `Online_cap_is_spent_on_open_table_first`: `OnlineBatchRows = 3`, open `[Item]`, 3 Item edits + 4 older Log rows -> first step has only Item rows, Log batch comes in a later step.
2. `Online_fk_parent_taken_before_open_child`: open `[Item]`, cap 2, Category rename + Item edits -> Category first, then Item.
3. `Online_covers_are_per_table`: after a capped step, Item `Covers (s, upTo_item]`, Log not covered; `Head` = Log's `sent` (the minimum), never above it.
4. `Online_empty_table_gets_extra_range`: table with no changes while others progress -> its `RangeExtra` arrives, its client cursor reaches the step's `upTo`.
5. `Online_backlog_drains_without_new_writes`: one capped step, no more writes -> remaining batches still arrive (stall fix); lab client `Inspect.Diff` empty after `SettleAsync`.
6. `Online_open_tables_change_applies_to_next_step`: `OpenTables [Log]` mid-stream flips priority.
7. `Online_priority_keeps_origin_filter_and_throttle`: own-origin row not sent but range closed; two steps are at least `OnlineInterval` apart.
9. `Online_head_is_min_of_table_progress`: in every step the `Head` value equals the minimum `upTo` over all tables and is never above any table's covered version.
8. `Online_client_cursors_stay_without_ranges`: after drain, lab client `CursorOf(t).Ranges` empty for every table.

Regression: `ScenarioTests` (`echo`, `columns`, `catchup`, `flow`, `delwins`), `OwnerToClientTests`, `RecoveryTests`.

## Scenario `openfirst` ("Пріоритет онлайн-змін")
`Sample.Lab/Scenarios.cs`, picked up by `ScenarioTests.Ids`.
- Setup: `OnlineBatchRows = 4`, `BothSynced`. Client 1 has `Item` open (default, `ClientNode.cs:40`).
- Step 1: `l.C2.Agent.SetOpenTables("Log")`.
- Step 2: new `OwnerNode.MixedChangeAsync()`, one transaction: 4 Log entries, 3 Item price edits, 1 Category rename (Log versions are the oldest).
- Expected: Client 1 first step = Category + 3 Item (cap 4), Log arrives in the next step. Client 2 first step = the 4 Log rows, Item and Category follow. Without priority both clients would get the Log rows first.
- Verify: `Lab` collects owner `онлайн:` log lines per client (from `SyncLog.Written`); `Check(...)` that Client 1's first line has no Log and Client 2's first line only Log. Runner then checks replicas equal the owner.
- Intro: "Ліміт пачки витрачається спершу на відкриті таблиці."

## Verification
`dotnet test` (all, incl. `ScenarioTests`), `dotnet format --verify-no-changes`.
