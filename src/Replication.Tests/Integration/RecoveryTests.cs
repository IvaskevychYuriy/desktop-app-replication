using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Replication.Client;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// Restarts and recovery (5.3, 10.3, 14): either side restarts and sync resumes from what is in the databases;
/// a restored owner database and a client behind the retention period both end in a new snapshot that keeps the outbox.
/// </summary>
[TestFixture]
public sealed class RecoveryTests
{
    private readonly List<string> _log = [];
    private SyncLog _syncLog = null!;
    private string _dir = "";
    private OwnerNode? _owner;
    private ClientNode? _client;
    private int _port;

    private string OwnerPath => Path.Combine(_dir, "owner.db");
    private string ClientPath => Path.Combine(_dir, "client.db");

    [SetUp]
    public void SetUp()
    {
        _log.Clear();
        _syncLog = new SyncLog();
        _syncLog.Written += e =>
        {
            lock (_log)
                _log.Add($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Text}");
        };
        _dir = TestDb.NewDir();
    }

    [TearDown]
    public async Task TearDown()
    {
        await StopClientAsync();
        await StopOwnerAsync();
        lock (_log)
        {
            foreach (string l in _log)
                TestContext.Out.WriteLine(l);
        }
    }

    [Test]
    public async Task Client_restart_resumes_from_its_cursors_and_keeps_its_outbox()
    {
        await StartOwnerAsync();
        StartClient();
        await SettleAsync();
        string instance = _client!.Instance;
        long snapshot = SnapshotVersion();
        await GoOfflineAsync();
        await _client.SetPriceAsync("Степлер", 150);
        string cursors = _client.Agent.CursorOf("Item").ToString();

        await StopClientAsync();
        await _owner!.SetPriceAsync("Палета", 470);
        StartClient();

        // Before it connects, the restarted client already knows its instance, cursors and queued edit.
        Assert.That(_client.Agent.InstanceId, Is.EqualTo(instance));
        Assert.That(_client.Agent.CursorOf("Item").ToString(), Is.EqualTo(cursors));
        Assert.That(_client.Outbox().Single().Columns, Does.Contain("Price"));

        _client.Link = true;
        await SettleAsync();
        Assert.That(Inspect.OwnerItem(_owner, "Степлер")?.Price, Is.EqualTo(150));
        Assert.That(Inspect.Diff(_owner, _client), Is.Empty);
        Assert.That(SnapshotVersion(), Is.EqualTo(snapshot), "a catch-up, not a new snapshot");
    }

    [Test]
    public async Task Owner_restart_keeps_its_instance_and_clients_catch_up_on_what_the_app_wrote_meanwhile()
    {
        await StartOwnerAsync();
        StartClient();
        await SettleAsync();
        string instance = _owner!.Store.InstanceId;
        long snapshot = SnapshotVersion();

        await StopOwnerAsync();
        // The application writes while its sync server is down: the triggers still version every write.
        await using (Sample.Domain.SampleDbContext db = Sample.Domain.SampleDbContext.Open(OwnerPath))
        {
            db.Log.Add(new Sample.Domain.LogEntry { Text = "без сервера синку" });
            await db.SaveChangesAsync();
        }
        await StartOwnerAsync();

        Assert.That(_owner.Store.InstanceId, Is.EqualTo(instance));
        await SettleAsync();
        Assert.That(Inspect.Diff(_owner, _client!), Is.Empty);
        Assert.That(SnapshotVersion(), Is.EqualTo(snapshot));
    }

    [Test]
    public async Task Owner_restored_from_a_backup_gets_a_new_instance_and_the_client_takes_a_snapshot_with_its_outbox()
    {
        await StartOwnerAsync();
        StartClient();
        await SettleAsync();
        string oldInstance = _owner!.Store.InstanceId;
        await StopOwnerAsync();
        string backup = Path.Combine(_dir, "owner.backup.db");
        File.Copy(OwnerPath, backup);

        // The owner moves on, the client follows, then the database file is replaced by the older backup.
        await StartOwnerAsync();
        await _owner!.AddLogAsync(5);
        await SettleAsync();
        await GoOfflineAsync();
        await _client!.SetPriceAsync("Степлер", 150);
        await StopOwnerAsync();
        File.Copy(backup, OwnerPath, overwrite: true);
        await StartOwnerAsync();

        // Its version is below the one recorded next to the database: a new instance id (5.3).
        Assert.That(_owner!.Store.InstanceId, Is.Not.EqualTo(oldInstance));
        _client.Link = true;
        await SettleAsync();

        Assert.That(_client.Instance, Is.EqualTo(_owner.Store.InstanceId));
        Assert.That(Rows.All(ClientPath, "Item", oldInstance), Is.Empty, "the old replica is gone");
        Assert.That(Inspect.OwnerItem(_owner, "Степлер")?.Price, Is.EqualTo(150), "the outbox moved to the new replica");
        Assert.That(Inspect.Diff(_owner, _client), Is.Empty);
    }

    [Test]
    public async Task Client_behind_the_retention_period_takes_a_snapshot_and_keeps_its_outbox()
    {
        await StartOwnerAsync();
        StartClient();
        await SettleAsync();
        long snapshot = SnapshotVersion();
        await GoOfflineAsync();
        await _client!.SetPriceAsync("Палета", 470);
        await _owner!.DeleteItemAsync("Скотч");
        await _owner.DeleteItemAsync("Факс");

        // A month later the owner forgets the silent client and purges the tombstones it never got (10.2).
        _owner.AdvanceClock(TimeSpan.FromDays(31));
        Replication.Owner.OwnerStore.PurgeResult purged = await _owner.PurgeAsync();
        Assert.That(purged.Tombstones, Is.EqualTo(2));
        Assert.That(purged.PurgedVersion, Is.GreaterThan(_client.Agent.CursorOf("Item").Cursor));

        _client.Link = true;
        await SettleAsync();

        Assert.That(SnapshotVersion(), Is.GreaterThan(snapshot), "SnapshotRequired, a new snapshot");
        Assert.That(Inspect.OwnerItem(_owner, "Палета")?.Price, Is.EqualTo(470));
        Assert.That(Inspect.ClientHas(_client, "Скотч"), Is.False);
        Assert.That(Inspect.Diff(_owner, _client), Is.Empty);
    }

    private async Task StartOwnerAsync()
    {
        _owner = await OwnerNode.StartAsync(OwnerPath, _syncLog, _port, configure: o => o.OnlineInterval = TimeSpan.FromMilliseconds(300));
        _port = _owner.Host.Port;
    }

    private async Task StopOwnerAsync()
    {
        if (_owner is not null)
            await _owner.DisposeAsync();
        _owner = null;
        SqliteConnection.ClearAllPools();
    }

    private void StartClient()
    {
        var options = new AgentOptions
        {
            AckInterval = TimeSpan.FromMilliseconds(300),
            ReconnectMin = TimeSpan.FromMilliseconds(200),
            ReconnectMax = TimeSpan.FromSeconds(1),
        };
        _client = ClientNode.Create("клієнт", ClientPath, $"http://127.0.0.1:{_port}", _syncLog, options);
        _client.Link = true;
    }

    private async Task StopClientAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        _client = null;
    }

    private async Task GoOfflineAsync()
    {
        _client!.Link = false;
        await Lab.WaitAsync(() => _client.Agent.GetStatus().State == AgentState.Offline, TimeSpan.FromSeconds(30), "the client did not go offline");
    }

    private Task SettleAsync() =>
        Lab.WaitAsync(() => _client!.Agent.GetStatus() is { State: AgentState.Online, Pending: 0 } && _client.Agent.MinCursor >= _owner!.Head(),
            TimeSpan.FromSeconds(30), "the client did not catch up with the owner");

    private long SnapshotVersion() => (long)Rows.Scalar(ClientPath, "SELECT snapshot_version FROM _sync_instances")!;
}
