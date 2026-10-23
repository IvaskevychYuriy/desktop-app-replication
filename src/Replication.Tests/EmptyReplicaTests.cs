using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Replication.Client;
using Replication.Owner;
using Replication.Tests.Integration;
using Sample.Domain;
using Sample.Lab;

namespace Replication.Tests;

/// <summary>Design 6.2, 6.3: with SkipSnapshotOnEmptyReplica a new replica, or one the owner can no longer catch up, starts empty and fills from the stream, and subscribers hear about it.</summary>
public sealed class EmptyReplicaTests
{
    [Test]
    public async Task Empty_replica_fills_from_the_stream_after_tombstones_were_purged()
    {
        await using Lab lab = await Lab.StartAsync(configureOwner: o => o.CatchupBatchRows = 4);
        await lab.Owner.DeleteItemAsync("Скотч");
        await lab.Owner.DeleteItemAsync("Маркери");
        // No clients yet: every tombstone goes, purged_version > 0.
        OwnerStore.PurgeResult purged = await lab.Owner.PurgeAsync();
        Assert.That(purged.PurgedVersion > 0, Is.True);

        lab.C2.Agent.Options.SkipSnapshotOnEmptyReplica = true;
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();
        Assert.That(Inspect.Diff(lab.Owner, lab.C2), Is.Empty);
    }

    [Test]
    public async Task Skip_snapshot_without_a_replica_fills_from_the_stream_without_SnapshotRequired()
    {
        await using Lab lab = await Lab.StartAsync(configureOwner: o => o.CatchupBatchRows = 4);

        lab.C2.Agent.Options.SkipSnapshotOnEmptyReplica = true;
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        Assert.That(lab.C2.Instance, Is.EqualTo(lab.Owner.Store.InstanceId));
        Assert.That(Inspect.Diff(lab.Owner, lab.C2), Is.Empty);
    }

    [Test]
    public async Task Skip_snapshot_empties_a_replica_that_cannot_be_caught_up_and_fills_it_from_the_stream()
    {
        await using Lab lab = await Lab.StartAsync();
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();
        await lab.GoOfflineAsync(lab.C2);

        await using (SampleDbContext db = lab.Owner.Db())
            await db.Log.ExecuteDeleteAsync(TestContext.CurrentContext.CancellationToken);
        await lab.SettleAsync();
        lab.Owner.AdvanceClock(TimeSpan.FromDays(31));
        OwnerStore.PurgeResult purged = await lab.Owner.PurgeAsync();
        Assert.That(purged.PurgedVersion > lab.C2.Agent.CursorOf("Log").Cursor, Is.True);

        // The cursor is behind purged_version, so catch-up cannot be correct; with the flag the replica is emptied and refilled from the stream instead of a snapshot file.
        lab.C2.Agent.Options.SkipSnapshotOnEmptyReplica = true;
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        Assert.That(Rows.Scalar(lab.C2.DbPath, "SELECT COUNT(*) FROM _sync_instances WHERE snapshot_version IS NULL"), Is.EqualTo(1L), "reset, no new snapshot");
        Assert.That(Inspect.Diff(lab.Owner, lab.C2), Is.Empty);
    }

    [Test]
    public async Task Empty_replica_over_an_existing_one_reports_every_table_changed()
    {
        await using Lab lab = await Lab.StartAsync();
        await lab.SyncNowAsync(lab.C1);
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();
        await lab.GoOfflineAsync(lab.C2);

        // The journal empties on the owner, and its tombstones are forgotten while Клієнт 2 is away: no batch will ever touch it.
        await using (SampleDbContext db = lab.Owner.Db())
            await db.Log.ExecuteDeleteAsync(TestContext.CurrentContext.CancellationToken);
        await lab.SettleAsync();
        lab.Owner.AdvanceClock(TimeSpan.FromDays(31));
        OwnerStore.PurgeResult purged = await lab.Owner.PurgeAsync();
        Assert.That(purged.PurgedVersion > lab.C2.Agent.CursorOf("Log").Cursor, Is.True);

        lab.C2.Agent.Options.SkipSnapshotOnEmptyReplica = true;
        var seen = new HashSet<string>();
        lab.C2.Agent.DataChanged += tables =>
        {
            lock (seen)
                seen.UnionWith(tables);
        };
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        lock (seen)
            Assert.That(seen, Is.SubsetOf(lab.C2.Replication.Model.Tables.Select(t => t.Name).ToHashSet()));
        Assert.That(Inspect.Diff(lab.Owner, lab.C2), Is.Empty);
    }
}
