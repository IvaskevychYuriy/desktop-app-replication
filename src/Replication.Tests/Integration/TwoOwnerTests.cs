using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Replication.Client;
using Replication.Model;
using Sample.Domain;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// One client, two owners with different data (4): one database, rows of each owner under its own <c>InstanceId</c>,
/// one agent and one change stream per owner. Clients join by catch-up from zero cursors (<c>skip_snapshot</c>), no snapshots.
/// </summary>
[TestFixture]
public sealed partial class TwoOwnerTests
{
    private readonly List<SyncLogEntry> _log = [];
    private TwoOwnerLab? _lab;

    private async Task<TwoOwnerLab> StartAsync(int catchupBatchRows = 3)
    {
        var log = new SyncLog();
        log.Written += e =>
        {
            lock (_log)
                _log.Add(e);
        };
        _lab = await TwoOwnerLab.StartAsync(log: log, configureOwner: o => o.CatchupBatchRows = catchupBatchRows);
        return _lab;
    }

    private async Task<TwoOwnerLab> StartSyncedAsync()
    {
        TwoOwnerLab lab = await StartAsync();
        await TwoOwnerLab.SyncNowAsync(lab.ToA);
        await TwoOwnerLab.SyncNowAsync(lab.ToB);
        await lab.SettleAsync();
        return lab;
    }

    [TearDown]
    public async Task StopLabAsync()
    {
        if (_lab is not null)
            await _lab.DisposeAsync();
        _lab = null;
        lock (_log)
        {
            foreach (SyncLogEntry e in _log)
                TestContext.Out.WriteLine($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Text}");
            _log.Clear();
        }
    }

    /// <summary>Log entries written by <paramref name="source"/>: an owner's name or an agent's label.</summary>
    private List<SyncLogEntry> LogOf(string source)
    {
        lock (_log)
            return [.. _log.Where(e => e.Source == source)];
    }

    /// <summary>The owner's lines about its Subscribe session with the client, without the client id prefix.</summary>
    private List<string> SessionLog(string owner) =>
        [.. LogOf(owner).Select(e => e.Text).Where(t => t.StartsWith('[')).Select(t => t[(t.IndexOf("] ", StringComparison.Ordinal) + 2)..])];

    /// <summary>Tables of the catch-up steps (Batch or TableSynced) in the order the owner sent them.</summary>
    private List<string> CatchupTables(string owner) =>
        [.. SessionLog(owner).Select(t => CatchupStep().Match(t)).Where(m => m.Success).Select(m => m.Groups[1].Value)];

    [GeneratedRegex(@"^(?:Batch|TableSynced) (\w+)")]
    private static partial Regex CatchupStep();

    private static Dictionary<Guid, RowData> Replica(ClientNode c, string table) => Rows.All(c.DbPath, table, c.Instance);

    private static Guid ItemId(OwnerNode o, string name) =>
        Rows.All(o.DbPath, "Item").Single(x => (string?)x.Value["Name"] == name).Key;

    private static string Snapshot(string dbPath, SyncModel model, string? instance = null) =>
        string.Join("\n", model.Tables.SelectMany(t => Rows.All(dbPath, t.Name, instance).OrderBy(x => x.Key)
            .Select(x => $"{t.Name} {x.Key} v{x.Value.Version} " + string.Join("|", t.Columns.Select(c => x.Value[c])))));

    private static string Outbox(ClientNode c) =>
        string.Join("\n", c.Outbox().Select(e => $"{e.Id} {e.Instance} {e.Table} {e.Pk} {e.Kind} {e.Sent} {string.Join(",", e.Columns)}"));

    // 1. An empty client joins both owners.

    [Test]
    public async Task Empty_client_catches_up_from_both_owners_then_gets_their_online_changes()
    {
        TwoOwnerLab lab = await StartAsync();

        lab.ToA.Link = true;
        lab.ToB.Link = true;
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lab.ToA.Instance, Is.EqualTo(lab.A.Store.InstanceId));
            Assert.That(lab.ToB.Instance, Is.EqualTo(lab.B.Store.InstanceId));
            Assert.That(lab.ToA.Instance, Is.Not.EqualTo(lab.ToB.Instance));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
            Assert.That(Replica(lab.ToA, "Item"), Has.Count.EqualTo(9));
            Assert.That(Replica(lab.ToB, "Item"), Has.Count.EqualTo(5));
            Assert.That(Replica(lab.ToB, "Log"), Has.Count.EqualTo(6));
            Assert.That(Rows.All(lab.ToA.DbPath, "Item"), Has.Count.EqualTo(14), "nothing but the two replicas in the client database");
            Assert.That(Rows.Scalar(lab.ToA.DbPath, "SELECT COUNT(*) FROM _sync_instances WHERE snapshot_version IS NULL"), Is.EqualTo(2L), "no snapshot was installed");
            foreach (string owner in new[] { TwoOwnerLab.OwnerA, TwoOwnerLab.OwnerB })
            {
                Assert.That(SessionLog(owner), Has.Some.Contains("Accepted"), owner);
                Assert.That(SessionLog(owner), Has.None.Contains("SnapshotRequired"), owner);
                // 3 rows a batch: more than one step per owner.
                Assert.That(SessionLog(owner).Count(t => t.StartsWith("Batch ", StringComparison.Ordinal)), Is.GreaterThan(3), owner);
            }
        });

        await lab.A.SetPriceAsync("Степлер", 175);
        await lab.B.SetPriceAsync("Чайник", 700);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(lab.ToA.ReplicaRow("Item", ItemId(lab.A, "Степлер"))["Price"], Is.EqualTo(175L));
            Assert.That(lab.ToB.ReplicaRow("Item", ItemId(lab.B, "Чайник"))["Price"], Is.EqualTo(700L));
            foreach (ClientNode c in new[] { lab.ToA, lab.ToB })
            {
                List<string> lines = [.. LogOf(c.Label).Select(e => e.Text)];
                int done = lines.FindIndex(t => t.Contains("досинхронізовано", StringComparison.Ordinal));
                int online = lines.FindIndex(t => t.StartsWith("← Batch Item онлайн", StringComparison.Ordinal));
                Assert.That(done, Is.GreaterThanOrEqualTo(0), c.Label);
                Assert.That(online, Is.GreaterThan(done), $"{c.Label}: the change comes as an online step after catch-up");
            }
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    // 2. A local edit goes to the owner of the row only.

    [Test]
    public async Task Local_edits_go_only_to_the_owner_the_row_came_from()
    {
        TwoOwnerLab lab = await StartSyncedAsync();
        string bBefore = Snapshot(lab.B.DbPath, lab.B.Store.Model);
        long bHead = lab.B.Head();

        await lab.ToA.SetPriceAsync("Степлер", 999);
        await lab.ToA.DeleteItemAsync("Скотч");
        Guid created = await lab.ToA.CreateItemAsync("Дрон", 4200, "новий", "Офіс");
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Rows.One(lab.A.DbPath, "Item", ItemId(lab.A, "Степлер"))["Price"], Is.EqualTo(999L));
            Assert.That(Rows.Has(lab.A.DbPath, "Item", created), Is.True);
            Assert.That(Rows.All(lab.A.DbPath, "Item").Values.Select(r => r["Name"]), Has.None.EqualTo("Скотч"));
            Assert.That(lab.B.Head(), Is.EqualTo(bHead), "owner 2 got nothing");
            Assert.That(Snapshot(lab.B.DbPath, lab.B.Store.Model), Is.EqualTo(bBefore));
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.None.Contains("Apply"));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });

        // One SaveChanges with rows of both replicas: each change is queued for its own owner.
        Guid stapler = ItemId(lab.A, "Степлер");
        Guid kettle = ItemId(lab.B, "Чайник");
        await using (SampleDbContext db = lab.ToA.Db())
        {
            (await db.Items.SingleAsync(x => x.Id == stapler)).Status = "архів";
            (await db.Items.SingleAsync(x => x.Id == kettle)).Price = 777;
            await db.SaveChangesAsync();
        }
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Rows.One(lab.A.DbPath, "Item", stapler)["Status"], Is.EqualTo("архів"));
            Assert.That(Rows.One(lab.A.DbPath, "Item", stapler)["Price"], Is.EqualTo(999L));
            Assert.That(Rows.One(lab.B.DbPath, "Item", kettle)["Price"], Is.EqualTo(777L));
            Assert.That(Rows.One(lab.B.DbPath, "Item", kettle)["Status"], Is.EqualTo("активний"));
            Assert.That(Rows.Has(lab.A.DbPath, "Item", kettle), Is.False);
            Assert.That(Rows.Has(lab.B.DbPath, "Item", stapler), Is.False);
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    // 3. One owner stays online while the other one is caught up again.

    [Test]
    public async Task Reconnect_to_one_owner_catches_up_while_the_other_keeps_streaming_online()
    {
        TwoOwnerLab lab = await StartSyncedAsync();
        await TwoOwnerLab.GoOfflineAsync(lab.ToB);
        await lab.B.BurstAsync();
        await lab.B.AddLogAsync(6);
        // Every message to the client waits, so the catch-up of owner 2 spans several online steps of owner 1.
        lab.B.Store.Options.Faults.DelayStream(lab.ToB.Replication.ClientId, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(60));

        var notOnline = new List<AgentState>();
        Guid pallet = ItemId(lab.A, "Палета");
        using var stop = new CancellationTokenSource();
        // Owner 1 changes a price and waits until the client has it, over and over, until owner 2 is caught up.
        Task automation = Task.Run(async () =>
        {
            long price = 500;
            while (!stop.IsCancellationRequested)
            {
                long p = price += 10;
                await lab.A.SetPriceAsync("Палета", p);
                await Lab.WaitAsync(() => (long)lab.ToA.ReplicaRow("Item", pallet)["Price"]! == p, TimeSpan.FromSeconds(10), "owner 1's change did not arrive");
                AgentState s = lab.ToA.Agent.GetStatus().State;
                if (s != AgentState.Online)
                    notOnline.Add(s);
            }
        });
        DateTimeOffset reconnect = DateTimeOffset.Now;
        await TwoOwnerLab.SyncNowAsync(lab.ToB, TimeSpan.FromSeconds(60));
        await stop.CancelAsync();
        await automation;
        await lab.B.SetPriceAsync("Чайник", 710);
        await lab.SettleAsync(TimeSpan.FromSeconds(60));

        List<SyncLogEntry> b = [.. LogOf(lab.ToB.Label).Where(e => e.At >= reconnect)];
        DateTimeOffset firstCatchup = b.First(e => e.Text.StartsWith("← Batch", StringComparison.Ordinal) && !e.Text.Contains("онлайн", StringComparison.Ordinal)).At;
        DateTimeOffset done = b.Last(e => e.Text.Contains("досинхронізовано", StringComparison.Ordinal)).At;
        List<SyncLogEntry> aOnline = [.. LogOf(lab.ToA.Label).Where(e => e.Text.StartsWith("← Batch Item онлайн", StringComparison.Ordinal) && e.At > firstCatchup && e.At < done)];
        Assert.Multiple(() =>
        {
            Assert.That(notOnline, Is.Empty, "owner 1 stayed online the whole time");
            Assert.That(aOnline, Has.Count.GreaterThanOrEqualTo(2), "owner 1's online steps arrived during owner 2's catch-up");
            Assert.That(SessionLog(TwoOwnerLab.OwnerB).Where(t => t.StartsWith("Batch ", StringComparison.Ordinal)).Count(), Is.GreaterThan(3));
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.None.Contains("SnapshotRequired"));
            Assert.That(b.FindIndex(e => e.Text.StartsWith("← Batch Item онлайн", StringComparison.Ordinal)),
                Is.GreaterThan(b.FindLastIndex(e => e.Text.Contains("досинхронізовано", StringComparison.Ordinal))), "the change after catch-up is an online step");
            Assert.That(lab.ToB.ReplicaRow("Item", ItemId(lab.B, "Чайник"))["Price"], Is.EqualTo(710L));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    // 4. A client with one replica joins the second owner.

    [Test]
    public async Task Client_with_a_replica_of_one_owner_joins_the_second_and_keeps_the_first()
    {
        TwoOwnerLab lab = await StartAsync();
        await TwoOwnerLab.SyncNowAsync(lab.ToA);
        await lab.SettleAsync();
        // A pending edit for owner 1 waits in the outbox while the client joins owner 2.
        await TwoOwnerLab.GoOfflineAsync(lab.ToA);
        await lab.ToA.SetPriceAsync("Степлер", 135);
        string aReplica = Snapshot(lab.ToA.DbPath, lab.A.Store.Model, lab.ToA.Instance);
        string aOutbox = Outbox(lab.ToA);

        await TwoOwnerLab.SyncNowAsync(lab.ToB);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Snapshot(lab.ToA.DbPath, lab.A.Store.Model, lab.ToA.Instance), Is.EqualTo(aReplica), "the replica of owner 1 is untouched");
            Assert.That(Outbox(lab.ToA), Is.EqualTo(aOutbox));
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.Some.Contains("Accepted"));
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.None.Contains("SnapshotRequired"));
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.None.Contains("Apply"));
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });

        await lab.B.NewItemAsync("Сито", category: "Кухня");
        await TwoOwnerLab.SyncNowAsync(lab.ToA);
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Replica(lab.ToB, "Item").Values.Select(r => r["Name"]), Has.Some.EqualTo("Сито"));
            Assert.That(LogOf(lab.ToB.Label).Select(e => e.Text), Has.Some.StartsWith("← Batch Item онлайн"));
            Assert.That(Rows.One(lab.A.DbPath, "Item", ItemId(lab.A, "Степлер"))["Price"], Is.EqualTo(135L));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    // 5. Each owner orders its catch-up by the open tables of its own agent.

    [Test]
    public async Task Each_owner_orders_catch_up_by_the_open_tables_of_its_agent()
    {
        TwoOwnerLab lab = await StartAsync();
        await lab.A.AddLogAsync(6);
        lab.ToA.Agent.SetOpenTables("Log");
        lab.ToB.Agent.SetOpenTables("Item");

        lab.ToA.Link = true;
        lab.ToB.Link = true;
        await lab.SettleAsync();

        List<string> a = CatchupTables(TwoOwnerLab.OwnerA);
        List<string> b = CatchupTables(TwoOwnerLab.OwnerB);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.SupersetOf(new[] { "Category", "Item", "Log" }), "owner 1 sent every table");
            Assert.That(b, Is.SupersetOf(new[] { "Category", "Item", "Log" }), "owner 2 sent every table");
            // Owner 1: Log is open, so it goes first; Category and Item follow.
            Assert.That(a.FindLastIndex(t => t == "Log"), Is.LessThan(a.FindIndex(t => t is "Category" or "Item")), string.Join(", ", a));
            // Owner 2: Item is open and Category is its FK parent, so both go before Log.
            Assert.That(b.FindLastIndex(t => t is "Category" or "Item"), Is.LessThan(b.FindIndex(t => t == "Log")), string.Join(", ", b));
            Assert.That(b.FindLastIndex(t => t == "Category"), Is.LessThan(b.FindIndex(t => t == "Item")), "the parent goes before the open table");
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    [Test]
    public async Task Changing_open_tables_during_catch_up_reorders_only_that_owner()
    {
        TwoOwnerLab lab = await StartAsync(catchupBatchRows: 2);
        await lab.A.AddLogAsync(6);
        await lab.B.AddLogAsync(6);
        foreach ((OwnerNode o, ClientNode c) in lab.Pairs)
            o.Store.Options.Faults.DelayStream(c.Replication.ClientId, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(60));

        lab.ToA.Link = true;
        lab.ToB.Link = true;
        await Lab.WaitAsync(() => CatchupTables(TwoOwnerLab.OwnerB).Contains("Item"), TimeSpan.FromSeconds(30), "owner 2 did not start on Item");
        lab.ToB.Agent.SetOpenTables("Log");
        await lab.SettleAsync(TimeSpan.FromSeconds(60));

        List<string> a = CatchupTables(TwoOwnerLab.OwnerA);
        List<string> b = CatchupTables(TwoOwnerLab.OwnerB);
        Assert.Multiple(() =>
        {
            Assert.That(a, Is.SupersetOf(new[] { "Category", "Item", "Log" }), "owner 1 sent every table");
            Assert.That(b, Is.SupersetOf(new[] { "Category", "Item", "Log" }), "owner 2 sent every table");
            Assert.That(SessionLog(TwoOwnerLab.OwnerB), Has.Some.StartsWith("OpenTables: Log"));
            Assert.That(SessionLog(TwoOwnerLab.OwnerA), Has.None.StartsWith("OpenTables"));
            // Owner 2 switched to Log before Item was done.
            Assert.That(b.IndexOf("Log"), Is.LessThan(b.LastIndexOf("Item")), string.Join(", ", b));
            // Owner 1 kept Item first.
            Assert.That(a.FindLastIndex(t => t is "Category" or "Item"), Is.LessThan(a.IndexOf("Log")), string.Join(", ", a));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }

    // Edge cases.

    [Test]
    public async Task One_owner_offline_does_not_hold_back_the_other()
    {
        TwoOwnerLab lab = await StartSyncedAsync();
        await lab.B.DisposeAsync();

        await lab.ToA.SetPriceAsync("Степлер", 321);
        await lab.A.SetPriceAsync("Палета", 480);
        await Lab.WaitAsync(() => lab.ToA.Agent.GetStatus().Pending == 0
                                  && (long)lab.ToA.ReplicaRow("Item", ItemId(lab.A, "Палета"))["Price"]! == 480, TimeSpan.FromSeconds(30), "owner 1 stalled");
        await lab.ToB.SetPriceAsync("Чайник", 1);

        Assert.Multiple(() =>
        {
            Assert.That(Rows.One(lab.A.DbPath, "Item", ItemId(lab.A, "Степлер"))["Price"], Is.EqualTo(321L));
            Assert.That(lab.ToA.Agent.GetStatus().State, Is.EqualTo(AgentState.Online));
            Assert.That(lab.ToB.Agent.GetStatus().State, Is.Not.EqualTo(AgentState.Online));
            Assert.That(lab.ToB.Agent.GetStatus().Pending, Is.EqualTo(1), "the edit for owner 2 waits in its outbox");
        });
    }

    [Test]
    public async Task Item_put_in_the_replica_of_owner_2_under_a_category_of_owner_1_is_rejected_by_owner_2()
    {
        TwoOwnerLab lab = await StartSyncedAsync();
        Guid office = Rows.All(lab.A.DbPath, "Category").Single(x => (string?)x.Value["Name"] == "Офіс").Key;
        var item = new Item { Name = "Чужий", Price = 1, Status = "новий", CategoryId = office };
        await using (SampleDbContext db = lab.ToB.Db())
        {
            db.Items.Add(item).SetInstance(lab.ToB.Instance);
            await db.SaveChangesAsync();
        }
        await lab.SettleAsync();

        Assert.Multiple(() =>
        {
            Assert.That(Rows.Has(lab.B.DbPath, "Item", item.Id), Is.False);
            Assert.That(Rows.Has(lab.A.DbPath, "Item", item.Id), Is.False);
            Assert.That(Rows.Has(lab.ToB.DbPath, "Item", item.Id), Is.False, "the client drops the rejected row");
            Assert.That(lab.ToB.Replication.Store.Notes().Select(n => n.Text), Has.Some.Contains("Чужий"));
            Assert.That(Inspect.Diff(lab.A, lab.ToA), Is.Empty);
            Assert.That(Inspect.Diff(lab.B, lab.ToB), Is.Empty);
        });
    }
}
