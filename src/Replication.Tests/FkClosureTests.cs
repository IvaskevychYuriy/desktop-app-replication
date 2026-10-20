using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NUnit.Framework;
using Replication.Client;
using Replication.Model;
using Replication.Owner;
using Sample.Lab;

namespace Replication.Tests;

/// <summary>Design 8.4: a create goes in the same or an earlier batch than the rows that point to it, FK cycles included.</summary>
public sealed class FkClosureTests
{
    [Test]
    public async Task Row_created_pointing_to_itself_is_sent()
    {
        await using GraphPair pair = await GraphPair.StartAsync();
        await pair.GoOfflineAsync();
        var node = new Node { Name = "Корінь" };
        await using (GraphDbContext db = pair.ClientDb())
        {
            db.Nodes.Add(node).SetInstance(pair.Instance);
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
            node.ParentId = node.Id;
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
        }

        await pair.SyncAsync();

        await using GraphDbContext owner = pair.OwnerDb();
        Node sent = await owner.Nodes.SingleAsync(x => x.Id == node.Id, TestContext.CurrentContext.CancellationToken);
        Assert.That(sent.ParentId, Is.EqualTo(node.Id));
        Assert.That(pair.Client.Store.Entries(), Is.Empty);
    }

    [Test]
    public async Task Rows_created_pointing_to_each_other_go_in_one_batch()
    {
        await using GraphPair pair = await GraphPair.StartAsync();
        await pair.GoOfflineAsync();
        var dept = new Dept { Name = "Відділ" };
        var emp = new Emp { Name = "Керівник" };
        await using (GraphDbContext db = pair.ClientDb())
        {
            db.Depts.Add(dept).SetInstance(pair.Instance);
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
            emp.DeptId = dept.Id;
            db.Emps.Add(emp).SetInstance(pair.Instance);
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
            dept.ManagerId = emp.Id;
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
        }

        await pair.SyncAsync();

        await using GraphDbContext owner = pair.OwnerDb();
        Assert.That((await owner.Depts.SingleAsync(x => x.Id == dept.Id, TestContext.CurrentContext.CancellationToken)).ManagerId, Is.EqualTo(emp.Id));
        Assert.That((await owner.Emps.SingleAsync(x => x.Id == emp.Id, TestContext.CurrentContext.CancellationToken)).DeptId, Is.EqualTo(dept.Id));
        Assert.That(pair.Client.Store.Entries(), Is.Empty);
        IReadOnlyList<string> applies = pair.Applies;
        Assert.That(applies, Has.Count.EqualTo(1));
        string apply = applies[0];
        Assert.That(apply, Does.Contain(Wire.Short(Wire.PkText(dept.Id))));
        Assert.That(apply, Does.Contain(Wire.Short(Wire.PkText(emp.Id))));
    }

    [Test]
    public async Task Acyclic_chain_goes_parents_first_and_bulk_actions_last_in_one_batch()
    {
        await using GraphPair pair = await GraphPair.StartAsync(owner => owner.Nodes.Add(new Node { Name = "Старий" }));
        await pair.GoOfflineAsync();
        // The bulk action is queued first, yet interactive actions go before it.
        Assert.That(await pair.Agent.DeleteWhereAsync("Node", new Predicate("Name", "Старий")), Is.EqualTo(1));
        var a = new Node { Name = "A" };
        var b = new Node { Name = "B" };
        var c = new Node { Name = "C" };
        await using (GraphDbContext db = pair.ClientDb())
        {
            foreach (Node n in new[] { a, b, c })
                db.Nodes.Add(n).SetInstance(pair.Instance);
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
            // A → B → C: the outbox holds A, B, C in this order, the parents must reach the owner first.
            a.ParentId = b.Id;
            b.ParentId = c.Id;
            await db.SaveChangesAsync(TestContext.CurrentContext.CancellationToken);
        }

        await pair.SyncAsync();

        IReadOnlyList<string> applies = pair.Applies;
        Assert.That(applies, Has.Count.EqualTo(1));
        string apply = applies[0];
        int[] at = [.. new[] { c, b, a }.Select(n => apply.IndexOf(Wire.Short(Wire.PkText(n.Id)), StringComparison.Ordinal))];
        Assert.That(at, Is.All.GreaterThanOrEqualTo(0), apply);
        Assert.That(at[0] < at[1] && at[1] < at[2], Is.True, apply);
        Assert.That(at[2] < apply.IndexOf("[масова]", StringComparison.Ordinal), Is.True, apply);
        await using GraphDbContext owner = pair.OwnerDb();
        Assert.That(await owner.Nodes.OrderBy(x => x.Name).Select(x => x.Name).ToListAsync(TestContext.CurrentContext.CancellationToken), Is.EqualTo(new[] { "A", "B", "C" }));
        Assert.That((await owner.Nodes.SingleAsync(x => x.Id == a.Id, TestContext.CurrentContext.CancellationToken)).ParentId, Is.EqualTo(b.Id));
        Assert.That((await owner.Nodes.SingleAsync(x => x.Id == b.Id, TestContext.CurrentContext.CancellationToken)).ParentId, Is.EqualTo(c.Id));
    }
}

public sealed class Node
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? ParentId { get; set; }
    public Node? Parent { get; set; }
}

public sealed class Dept
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? ManagerId { get; set; }
    public Emp? Manager { get; set; }
}

public sealed class Emp
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? DeptId { get; set; }
    public Dept? Dept { get; set; }
}

/// <summary>A model with FK cycles: a tree and two tables that point to each other.</summary>
public sealed class GraphDbContext(DbContextOptions<GraphDbContext> options) : DbContext(options)
{
    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Dept> Depts => Set<Dept>();
    public DbSet<Emp> Emps => Set<Emp>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Node>(e =>
        {
            e.ToTable("Node");
            e.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId);
        });
        b.Entity<Dept>(e =>
        {
            e.ToTable("Dept");
            e.HasOne(x => x.Manager).WithMany().HasForeignKey(x => x.ManagerId);
        });
        b.Entity<Emp>(e =>
        {
            e.ToTable("Emp");
            e.HasOne(x => x.Dept).WithMany().HasForeignKey(x => x.DeptId);
        });
        b.UseReplication();
    }

    public static GraphDbContext Open(string path, params IInterceptor[] interceptors) =>
        new GraphDbContext(new DbContextOptionsBuilder<GraphDbContext>()
            .UseSqlite($"Data Source={path};Pooling=True;Default Timeout=30")
            .AddInterceptors(interceptors)
            .Options);
}

/// <summary>An owner and one client of <see cref="GraphDbContext"/> over gRPC on localhost.</summary>
internal sealed class GraphPair : IAsyncDisposable
{
    private const string Label = "клієнт";

    private readonly List<string> _applies = [];

    private GraphPair(OwnerHost owner, ClientReplication client, SyncLog log)
    {
        Owner = owner;
        Client = client;
        log.Written += e =>
        {
            if (e.Source == Label && e.Text.StartsWith("→ Apply:", StringComparison.Ordinal))
            {
                lock (_applies)
                    _applies.Add(e.Text);
            }
        };
    }

    public OwnerHost Owner { get; }
    public ClientReplication Client { get; }
    public SyncAgent Agent => Client.Agents[0];
    public string Instance => Agent.InstanceId ?? throw new InvalidOperationException("репліки ще нема");

    /// <summary>The "→ Apply:" log lines of the client: one per sent batch.</summary>
    public IReadOnlyList<string> Applies
    {
        get
        {
            lock (_applies)
                return [.. _applies];
        }
    }

    public static async Task<GraphPair> StartAsync(Action<GraphDbContext>? seedOwner = null)
    {
        string dir = TestDb.NewDir();
        var log = new SyncLog();
        string ownerPath = Path.Combine(dir, "owner.db");
        SyncModel model;
        using (GraphDbContext db = GraphDbContext.Open(ownerPath))
        {
            db.Database.EnsureCreated();
            model = SyncModel.From(db);
        }
        OwnerHost owner = await OwnerHost.StartAsync(ownerPath, model, new OwnerOptions { Log = log, OnlineInterval = TimeSpan.FromMilliseconds(300) });
        if (seedOwner is not null)
        {
            using GraphDbContext db = GraphDbContext.Open(ownerPath);
            seedOwner(db);
            db.SaveChanges();
        }

        string clientPath = Path.Combine(dir, "client.db");
        using (GraphDbContext db = GraphDbContext.Open(clientPath))
            db.Database.EnsureCreated();
        var client = new ClientReplication(ClientReplication.BuildServices(clientPath, model).InstallClientStore(), log);
        var pair = new GraphPair(owner, client, log);
        client.Connect(owner.Address, "Власник", new AgentOptions { AckInterval = TimeSpan.FromMilliseconds(300) }, Label);
        await pair.SyncAsync();
        return pair;
    }

    /// <summary>Owner rows: a root whose ParentId is its own Id, and the nodes below it.</summary>
    public static void SeedTree(GraphDbContext owner, Node root, params Node[] below)
    {
        owner.Nodes.Add(root);
        owner.Nodes.AddRange(below);
        owner.SaveChanges();
        root.ParentId = root.Id;
        owner.SaveChanges();
    }

    public GraphDbContext ClientDb() => GraphDbContext.Open(Client.Store.DbPath, Client.Router);

    public GraphDbContext OwnerDb() => GraphDbContext.Open(Owner.Store.DbPath);

    public async Task GoOfflineAsync()
    {
        Agent.LinkEnabled = false;
        await Lab.WaitAsync(() => Agent.GetStatus().State == AgentState.Offline, TimeSpan.FromSeconds(30), "клієнт не вийшов з мережі");
    }

    /// <summary>Turns the link on and waits until the client is online with an empty outbox.</summary>
    public async Task SyncAsync()
    {
        Agent.LinkEnabled = true;
        await Lab.WaitAsync(() => Agent.GetStatus() is { State: AgentState.Online, Pending: 0 }, TimeSpan.FromSeconds(30), "клієнт не відправив чергу");
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await Owner.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }
}
