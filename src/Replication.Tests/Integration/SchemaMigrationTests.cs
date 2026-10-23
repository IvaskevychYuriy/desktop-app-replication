using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NUnit.Framework;
using Replication.Client;
using Replication.Model;
using Replication.Owner;
using Replication.Tests.Integration.Migrations;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// Design 15.3: an application that already replicates ships a new version with an EF migration. The owner and the
/// client each run the migration on start, then call <c>Install</c> again, which brings back the triggers the table
/// rebuild dropped; replication goes on from the same cursors, and the new column and table replicate both ways.
/// </summary>
[TestFixture]
public sealed class SchemaMigrationTests
{
    private readonly List<string> _log = [];
    private SyncLog _syncLog = null!;
    private string _dir = "";
    private string _ownerPath = "";
    private string _clientPath = "";
    private OwnerStore _owner = null!;
    private OwnerHost? _host;
    private ClientReplication? _client;
    private int _port;

    private SyncAgent Agent => _client!.Agents[0];
    private string Instance => Agent.InstanceId!;

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
        _ownerPath = Path.Combine(_dir, "owner.db");
        _clientPath = Path.Combine(_dir, "client.db");
    }

    [TearDown]
    public async Task TearDown()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_host is not null)
            await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        lock (_log)
        {
            foreach (string l in _log)
                TestContext.Out.WriteLine(l);
        }
    }

    [Test]
    public async Task Sync_goes_on_after_a_migration_that_rebuilds_a_table_and_adds_a_column_and_a_table()
    {
        // Version 1 of the application, replicating.
        Migrate(_ownerPath, 1);
        var shelf = new Shelf { Name = "Склад" };
        var lamp = new Product { Name = "Лампа", Price = 100, ShelfId = shelf.Id };
        var cable = new Product { Name = "Кабель", Price = 200, ShelfId = shelf.Id };
        var paint = new Product { Name = "Фарба", Price = 300, ShelfId = shelf.Id };
        await using (ShopDbContext db = ShopDbContext.Open(_ownerPath, 1))
        {
            db.Shelves.Add(shelf);
            db.Products.AddRange(lamp, cable, paint);
            await db.SaveChangesAsync();
        }
        SyncModel v1 = ModelOf(1);
        await StartOwnerAsync(v1, schema: 1);
        Migrate(_clientPath, 1);
        StartClient(v1, schema: 1);
        await SettleAsync();
        Assert.That(Rows.Diff(v1, _ownerPath, _clientPath, Instance), Is.Empty);

        // Before the upgrade the owner holds a partial change the client has not received: the mask names Name in
        // the version-1 column order (Name, Price, ShelfId). The client has an edit in its outbox.
        await OwnerEditAsync(1, lamp.Id, p => p.Price = 110);
        await SettleAsync();
        long received = Rows.One(_ownerPath, "Product", lamp.Id).Version;
        await Lab.WaitAsync(() => (long)Rows.Scalar(_ownerPath, "SELECT floor FROM _sync_floor WHERE tbl = 'Product'")! >= received,
            TimeSpan.FromSeconds(30), "the owner did not see the client's cursor pass the row");
        await GoOfflineAsync();
        await OwnerEditAsync(1, lamp.Id, p => p.Name = "Лампа LED");
        RowData lampOnOwner = Rows.One(_ownerPath, "Product", lamp.Id);
        Assert.That(lampOnOwner.Mask, Is.EqualTo(v1["Product"].MaskOf(["Name"])), "a partial change of Name only");
        await ClientEditAsync(1, cable.Id, p => p.Price = 999);
        Assert.That(_client!.Store.Entries(Instance).Select(e => (e.Kind, string.Join(",", e.Columns))), Is.EqualTo(new[] { (OutboxKind.Patch, "Price") }));

        // The owner upgrades: the migration rebuilds Product, which drops its triggers and keeps every SyncVersion.
        Dictionary<Guid, long> versions = Rows.All(_ownerPath, "Product").ToDictionary(x => x.Key, x => x.Value.Version);
        long head = (long)Rows.Scalar(_ownerPath, "SELECT version FROM _sync_meta")!;
        await _host!.DisposeAsync();
        _host = null;
        Migrate(_ownerPath, 2);
        Assert.That(Rows.Triggers(_ownerPath, "Product"), Is.Empty, "the rebuild drops the triggers");
        Assert.That(Rows.All(_ownerPath, "Product").ToDictionary(x => x.Key, x => x.Value.Version), Is.EqualTo(versions));

        // Install after the migration, as on every start: triggers for every table, the new one too, and no new versions.
        SyncModel v2 = ModelOf(2);
        await StartOwnerAsync(v2, schema: 2);
        foreach (string table in new[] { "Shelf", "Product", "Tag" })
            Assert.That(Rows.Triggers(_ownerPath, table), Is.EqualTo(new[] { $"_sync_{table}_del", $"_sync_{table}_ins", $"_sync_{table}_upd" }), table);
        Assert.That(Rows.Scalar(_ownerPath, "SELECT version FROM _sync_meta"), Is.EqualTo(head));
        // The new column sorts first, so the partial change keeps naming Name in the new column order (5.2).
        Assert.That(Rows.One(_ownerPath, "Product", lamp.Id).Mask, Is.EqualTo(v2["Product"].MaskOf(["Name"])));
        Assert.That(Rows.One(_ownerPath, "Product", paint.Id).Mask, Is.EqualTo(v2["Product"].FullMask), "an untouched insert stays a full row");

        // The client still runs version 1: SchemaMismatch, nothing sent, the outbox kept (6.2).
        Agent.LinkEnabled = true;
        await Lab.WaitAsync(() => Agent.GetStatus().State == AgentState.SchemaMismatch, TimeSpan.FromSeconds(30), "no SchemaMismatch");
        Assert.That(Rows.One(_ownerPath, "Product", cable.Id)["Price"], Is.EqualTo(200L));
        Assert.That(_client.Store.Entries(Instance), Has.Count.EqualTo(1));

        // The client upgrades and restarts: same migration, Install, and it carries on from its cursors.
        await _client.DisposeAsync();
        _client = null;
        Migrate(_clientPath, 2);
        StartClient(v2, schema: 2);
        await SettleAsync();
        Assert.That(Rows.One(_ownerPath, "Product", cable.Id)["Price"], Is.EqualTo(999L), "the queued edit reached the owner");
        Assert.That(Rows.One(_clientPath, "Product", lamp.Id, Instance)["Name"], Is.EqualTo("Лампа LED"), "the partial change arrived as Name");
        Assert.That(Rows.Diff(v2, _ownerPath, _clientPath, Instance), Is.Empty);

        // The new column and the new table replicate both ways.
        await OwnerEditAsync(2, paint.Id, p => p.Discount = 15);
        Assert.That(Rows.One(_ownerPath, "Product", paint.Id).Mask, Is.EqualTo(v2["Product"].MaskOf(["Discount"])));
        var fresh = new Tag { Label = "новинка", ProductId = lamp.Id };
        await using (ShopDbContext db = ShopDbContext.Open(_ownerPath, 2))
        {
            db.Tags().Add(fresh);
            await db.SaveChangesAsync();
        }
        await ClientEditAsync(2, lamp.Id, p => p.Discount = 5);
        var hit = new Tag { Label = "хіт", ProductId = cable.Id };
        await using (ShopDbContext db = ShopDbContext.Open(_clientPath, 2, _client!.Router))
        {
            db.Tags().Add(hit).SetInstance(Instance);
            await db.SaveChangesAsync();
        }
        await SettleAsync();

        Assert.That(Rows.One(_clientPath, "Product", paint.Id, Instance)["Discount"], Is.EqualTo(15L));
        Assert.That(Rows.One(_clientPath, "Tag", fresh.Id, Instance)["Label"], Is.EqualTo("новинка"));
        Assert.That(Rows.One(_ownerPath, "Product", lamp.Id)["Discount"], Is.EqualTo(5L));
        RowData hitOnOwner = Rows.One(_ownerPath, "Tag", hit.Id);
        Assert.That(hitOnOwner["Label"], Is.EqualTo("хіт"));
        Assert.That(hitOnOwner.Origin, Is.EqualTo(_client.ClientId));
        Assert.That(Rows.Diff(v2, _ownerPath, _clientPath, Instance), Is.Empty);
    }

    /// <summary>Runs the application's migrations up to <paramref name="schema"/>, as the app does on start.</summary>
    private static void Migrate(string path, int schema)
    {
        using ShopDbContext db = ShopDbContext.Open(path, schema);
        db.GetService<IMigrator>().Migrate(ShopDbContext.MigrationOf(schema));
    }

    private static SyncModel ModelOf(int schema)
    {
        using ShopDbContext db = ShopDbContext.Open(":memory:", schema);
        return SyncModel.From(db);
    }

    private async Task StartOwnerAsync(SyncModel model, int schema)
    {
        // The same port after a restart: the client keeps the owner's address.
        _host = await OwnerHost.StartAsync(_ownerPath, model,
            new OwnerOptions { SchemaVersion = schema, Log = _syncLog, OnlineInterval = TimeSpan.FromMilliseconds(300) }, _port);
        _owner = _host.Store;
        _port = _host.Port;
    }

    private void StartClient(SyncModel model, int schema)
    {
        _client = new ClientReplication(ClientReplication.BuildServices(_clientPath, model).InstallClientStore(), _syncLog);
        var options = new AgentOptions
        {
            SchemaVersion = schema,
            AckInterval = TimeSpan.FromMilliseconds(300),
            ReconnectMin = TimeSpan.FromMilliseconds(200),
            ReconnectMax = TimeSpan.FromSeconds(1),
        };
        _client.Connect($"http://127.0.0.1:{_port}", "клієнт", options).LinkEnabled = true;
    }

    private async Task GoOfflineAsync()
    {
        Agent.LinkEnabled = false;
        await Lab.WaitAsync(() => Agent.GetStatus().State == AgentState.Offline, TimeSpan.FromSeconds(30), "the client did not go offline");
    }

    /// <summary>Online, outbox empty, every cursor at the owner's head.</summary>
    private async Task SettleAsync()
    {
        await Lab.WaitAsync(() =>
        {
            using SqliteConnection c = _owner.Open();
            return Agent.GetStatus() is { State: AgentState.Online, Pending: 0 } && Agent.MinCursor >= _owner.Head(c);
        }, TimeSpan.FromSeconds(30), "the client did not catch up with the owner");
    }

    private async Task OwnerEditAsync(int schema, Guid id, Action<Product> change)
    {
        await using ShopDbContext db = ShopDbContext.Open(_ownerPath, schema);
        change(await db.Products.SingleAsync(x => x.Id == id));
        await db.SaveChangesAsync();
    }

    private async Task ClientEditAsync(int schema, Guid id, Action<Product> change)
    {
        await using ShopDbContext db = ShopDbContext.Open(_clientPath, schema, _client!.Router);
        change(await db.Products.SingleAsync(x => x.Id == id && EF.Property<string>(x, SyncColumns.InstanceId) == Instance));
        await db.SaveChangesAsync();
    }
}
