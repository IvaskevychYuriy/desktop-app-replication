# Fall back from online steps to catch-up when the client lags

## Context

Today a Subscribe stream goes catch-up -> online once and never back (`SubscribeSession._online` is set to true at `SubscribeSession.cs:200` and nothing resets it). A slow stream stays online and the only recovery is a dropped link and reconnect. The goal is to let a lagging stream return to catch-up (newest first, open tables first, small batches, `remaining` reporting) without dropping the link.

## Findings that shape the design

1. `_lagWarning` cannot fire in online mode today. `TrackRemaining` is fed only by catch-up batches (`SyncAgent.cs:558`, `!b.Online`) and by `Progress`. The owner computes `Batch.Remaining` only in `CatchupBatch` (`SubscribeSession.cs:318`); `OnlineStepAsync` sets none, and the only `Progress` it ever sends is `Done = true` with `remaining = 0`. So the client has no lag signal while online.
2. The owner is the side that sees lag. `OnlineStepAsync` ends in `await _out.WriteAsync` (`SendAsync`, line 437). gRPC flow control blocks that write when the client reads slowly, whether the cause is network or apply (`ReadLoopAsync` handles messages one by one). Two cheap signals follow: `head - _sentUpTo` after a step, and the duration of a step.
3. The owner's mirror is already consistent for a switch back. After an online step, `_mirror` cursors are lifted to `upTo` (lines 383-387). Catch-up computes gaps from `_mirror` against the current head, so flipping `_online = false` makes `NextCatchup` pick up exactly the versions above `_sentUpTo`. No client cursor data is needed.
4. Re-entry already works. When catch-up runs dry the loop sets `_online = true`, `_sentUpTo = _catchupHead` and sends `Progress { Done = true }` (lines 196-205). `_synced` must be cleared on fallback, or `NextCatchup` skips the `TableSynced` path for tables it thinks are done (line 255).
5. The client needs a state signal. It enters `AgentState.Catchup` only once, after `Start` is accepted (`SyncAgent.cs:399`) and returns to `Online` on `Progress.Done` (line 604). A fallback would leave the UI showing `Online` unless the owner tells it.

## Options

**A. Client drops the link on `_lagWarning` (no protocol change).**
Throw from the session so the agent reconnects; `Start` carries the acked cursors and the owner runs catch-up. Smallest change, but it needs finding 1 fixed first (an online lag signal), loses in-flight data, pays a reconnect and `Identity` round trip, and can flap with the 3 minute window.

**B. Owner-side fallback (recommended).**
In `MainLoopAsync`, after an online step, if `head - _sentUpTo` exceeds a threshold or the step took longer than a limit, set `_online = false`, clear `_synced`, and send `Progress { remaining = Remaining(...), done = false }`. The client reacts to `Progress` with `!Done` by calling `SetState(AgentState.Catchup)` and `TrackRemaining` (already called for every `Progress`). New knobs in `OwnerOptions` next to `OnlineBatchRows`: lag version threshold and step-duration limit. No proto change: `Progress.done = false` is already a valid message that the owner never sends today.

**C. Client asks for it with a new `Subscribe` message.**
Add a `Resync`/`Lag` body to `SubscribeMessage` (breaking proto changes are acceptable per project memory), handled in `ReadLoopAsync` like `OpenTables`. Needs finding 1 fixed on the client (online `remaining` on `Head` or `Batch`, which costs `Remaining()` count queries per online step). More moving parts than B and the owner sees the lag first anyway.

## Recommended plan (B)

Files:
- `Replication.Core/Owner/OwnerOptions.cs`: add `OnlineLagVersions` and `OnlineStepLimit` (names open), with defaults.
- `Replication.Core/Owner/SubscribeSession.cs` (`MainLoopAsync`, around line 216): after `OnlineStepAsync`, evaluate the lag condition; on trigger set `_online = false`, `_synced.Clear()`, log it, and send the non-done `Progress` using the existing `Remaining(conn, tx, head)` (open a short deferred transaction as `CatchupStepAsync` does).
- `Replication.Core/Client/SyncAgent.cs` (`HandleAsync`, `Progress` case, line 571): when `!Done` and the state is `Online`, call `SetState(AgentState.Catchup)`. Check `SetState` (line 214-220) so `_lagWarning` handling and `_offlineSince` stay right.
- Hysteresis: after a fallback, do not re-trigger until catch-up has completed once (the existing `_online = true` re-entry already enforces this).

Reuse: `CatchupStepAsync`, `NextCatchup`, `Remaining`, `TrackRemaining`; no new protocol messages.

## Open questions

- Intended benefit of catch-up over online: priority for open tables and newest-first, bounded 500-row batches, and `remaining` visibility. Total bytes sent are the same, so a slow link is not made faster. Is that the goal?
- Trigger preference: version gap, step duration, or both.
- Should `_lagWarning` also be fed online (option C), or is the owner-side trigger enough?

## Verification

- Unit test in `Replication.Tests` for `SubscribeSession`: stream with `Opt.Faults.StreamDelay` (used in `SendAsync`) large enough to trip the trigger; assert the owner sends a non-done `Progress`, then `Batch` messages with `Online = false`, then `Progress { Done = true }`, and that the replica ends equal to the owner.
- Client test: after the non-done `Progress`, `GetStatus().State == AgentState.Catchup`, then `Online` again at `Done`.
- Check no rows are lost or duplicated across the switch by comparing owner and replica after concurrent writes during the fallback.
- Run the full `Replication.Tests` suite, including `MultiOwner` and `CursorCommitTests`.
