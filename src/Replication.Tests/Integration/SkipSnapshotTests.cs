using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Replication.Client;
using Replication.Owner;
using Sample.Domain;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// A client without a replica may ask for catch-up instead of a snapshot (<c>Start.skip_snapshot</c>). The owner accepts it
/// with zero cursors and sends its instance_id in <c>Accepted</c>; in every other case the snapshot rules stay as they were.
/// </summary>
[TestFixture]
public sealed class SkipSnapshotTests : LabTests
{
    [Test]
    public async Task Client_without_a_replica_is_accepted_and_takes_the_instance_from_Accepted()
    {
        Lab lab = await StartAsync(o => o.CatchupBatchRows = 3);
        lab.C2.Agent.Options.SkipSnapshot = true;

        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lab.C2.Instance, Is.EqualTo(lab.Owner.Store.InstanceId));
            Assert.That(lab.Diff(lab.C2), Is.Empty);
            Assert.That(Rows.All(lab.C2.DbPath, "Item", lab.C2.Instance), Has.Count.EqualTo(9));
            Assert.That(Rows.Scalar(lab.C2.DbPath, "SELECT COUNT(*) FROM _sync_instances WHERE snapshot_version IS NULL"), Is.EqualTo(1L), "no snapshot was installed");
            Assert.That(OwnerLogAbout(lab.C2), Has.Some.Contains("Accepted"));
            Assert.That(OwnerLogAbout(lab.C2), Has.None.Contains("SnapshotRequired"));
        });
    }

    [Test]
    public async Task Without_the_flag_a_client_without_a_replica_still_gets_a_snapshot()
    {
        Lab lab = await StartAsync();

        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(OwnerLogAbout(lab.C2), Has.Some.Contains("SnapshotRequired"));
            Assert.That(OwnerLogAbout(lab.C2), Has.None.Contains("Accepted"));
            Assert.That(lab.Diff(lab.C2), Is.Empty);
        });
    }

    [Test]
    public async Task A_change_made_during_catch_up_arrives_over_the_same_stream()
    {
        Lab lab = await StartAsync(o => o.CatchupBatchRows = 2);
        lab.C2.Agent.Options.SkipSnapshot = true;

        await lab.SyncNowAsync(lab.C2);
        await lab.Owner.SetPriceAsync("Палета", 470);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lab.C2.ReplicaRow("Item", lab.ItemId("Палета"))["Price"], Is.EqualTo(470L));
            Assert.That(lab.Diff(lab.C2), Is.Empty);
        });
    }

    [Test]
    public async Task Flag_does_not_bypass_a_snapshot_when_the_replica_is_behind_purged_version()
    {
        Lab lab = await StartSyncedAsync();
        await lab.GoOfflineAsync(lab.C2);
        await using (SampleDbContext db = lab.Owner.Db())
            await db.Log.ExecuteDeleteAsync(TestContext.CurrentContext.CancellationToken);
        await lab.SettleAsync();
        lab.Owner.AdvanceClock(TimeSpan.FromDays(31));
        OwnerStore.PurgeResult purged = await lab.Owner.PurgeAsync();
        Assert.That(purged.PurgedVersion, Is.GreaterThan(lab.C2.Agent.CursorOf("Log").Cursor));

        lab.C2.Agent.Options.SkipSnapshot = true;
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(OwnerLogAbout(lab.C2), Has.Some.Contains("SnapshotRequired"));
            Assert.That(lab.Diff(lab.C2), Is.Empty);
        });
    }
}
