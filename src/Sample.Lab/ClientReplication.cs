using Microsoft.Extensions.DependencyInjection;
using Replication;
using Replication.Client;
using Replication.Model;

namespace Sample.Lab;

/// <summary>
/// The client side of the application: one database for all instances, one <see cref="WriteRouter"/>, and one
/// <see cref="SyncAgent"/> per owner the client connects to (4). The services are built and set up once.
/// </summary>
/// <example>
/// <code>
/// ServiceProvider services = ClientReplication.BuildServices("client.db", model, s => s.AddSampleDb("client.db", sp => [sp.GetRequiredService&lt;WriteRouter&gt;()]));
/// services.MigrateSampleDb().InstallClientStore();
/// var replication = new ClientReplication(services, log);
/// SyncAgent agent = replication.Connect("http://10.0.0.5:5005", "Склад");
/// agent.LinkEnabled = true;
/// </code>
/// </example>
public sealed class ClientReplication : IAsyncDisposable
{
    private readonly List<SyncAgent> _agents = [];
    private readonly SyncLog _log;

    public ClientReplication(IServiceProvider services, SyncLog? log = null)
    {
        Services = services;
        _log = log ?? SyncLog.Null;
        Store = services.GetRequiredService<ClientStore>();
        Router = services.GetRequiredService<WriteRouter>();
        Router.OutboxChanged += instance =>
        {
            foreach (SyncAgent a in Agents.Where(a => a.InstanceId == instance))
                a.NotifyOutbox();
        };
        Router.Routed += (instance, text) =>
        {
            SyncAgent? agent = Agents.FirstOrDefault(a => a.InstanceId == instance);
            _log.Write(agent?.Name ?? instance, text);
        };
    }

    public IServiceProvider Services { get; }

    public ClientStore Store { get; }

    public WriteRouter Router { get; }

    public SyncModel Model => Store.Model;

    public SyncLog Log => _log;

    public string ClientId => Store.ClientId;

    public IReadOnlyList<SyncAgent> Agents
    {
        get
        {
            lock (_agents)
                return [.. _agents];
        }
    }

    /// <param name="configure">Registers the application's DbContext factory.</param>
    public static ServiceProvider BuildServices(string dbPath, SyncModel model, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddClientServices(dbPath, model);
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Starts an agent for the owner at <paramref name="address"/> (link off). The agent learns the owner's instance id on
    /// the first link, and <see cref="SyncAgent.InstanceId"/> stays null until then.
    /// </summary>
    public SyncAgent Connect(string address, string name, AgentOptions? options = null)
    {
        var agent = new SyncAgent(Store, address, name, options, _log);
        lock (_agents)
            _agents.Add(agent);
        agent.Start();
        return agent;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (SyncAgent a in Agents)
            await a.DisposeAsync();
        if (Services is IAsyncDisposable d)
            await d.DisposeAsync();
    }
}
