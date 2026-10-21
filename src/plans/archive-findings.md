# Archive findings (snapshot install) — deferred

Found while planning `simple-snapshot-import.md`. Not fixed; archive handling is out of scope for that change.

- **Archive key.** Archived rows have `InstanceId = "<old>:archive"` (`SyncColumns.ArchiveOf`, `SyncModel.cs:21`). `InstallSnapshot` deletes `WHERE InstanceId IN (old, new)`, so it never touches them.
- **No rename on instance change.** When the owner's `instance_id` changes (restored DB), archived rows stay under `<old>:archive`. `HasArchiveBelow` (`ArchiveGuard.cs`) and the FK rule `child == ArchiveOf(parent)` (`SyncModel.cs:32`) look for `ArchiveOf(new)` and no longer see them. Possible fix: `UPDATE <table> SET InstanceId = ArchiveOf(new) WHERE InstanceId = ArchiveOf(old)` for every table inside the install transaction.
- **Id clash.** `Id` is the sole primary key. A snapshot row with the same `Id` as an archived row is silently skipped by `INSERT OR IGNORE`: the live row never arrives and the cursor jumps to V. A plain `INSERT` would fail the whole transaction instead. Happens for example when an owner backup restored later contains rows the client archived after the backup. Needs a policy: archive wins (current) or snapshot wins.
- **Dangling children.** The install uses a bare `DELETE` with foreign keys off, not `ArchiveGuard.DeleteOrArchive` (which the stream-tombstone path uses). A parent that archived rows point to and that is missing from the new snapshot is deleted, and the archived children dangle.
- **Pending Archive actions.** Once the outbox is dropped at snapshot time, an unsent `Archive` action is dropped too. The owner may still hold those rows, so they arrive live in the snapshot while the archived copy also exists (Id clash above). The in-memory `_archiveRetry` / `_archiveTries` in `SyncAgent` also refer to rows the install replaced.
- **Not verified by running code.** All of the above comes from reading `ReplicaWriter.cs`, `ArchiveGuard.cs`, `SyncModel.cs` and `SyncAgent.cs`.
