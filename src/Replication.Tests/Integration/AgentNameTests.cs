using NUnit.Framework;
using Replication.Client;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>The agent's name is the log source; the app sets it, nothing stores it.</summary>
[TestFixture]
public sealed class AgentNameTests : LabTests
{
    [Test]
    public async Task Setting_the_name_changes_the_log_source()
    {
        Lab lab = await StartSyncedAsync();
        var sources = new List<string>();
        lab.C1.Replication.Log.Written += e =>
        {
            lock (sources)
                sources.Add(e.Source);
        };

        lab.C1.Agent.Name = "Склад-2";
        await lab.Owner.AddLogAsync(1);
        await lab.SettleAsync();

        Assert.That(lab.C1.Agent.Name, Is.EqualTo("Склад-2"));
        lock (sources)
            Assert.That(sources, Does.Contain("Склад-2"));
    }

    [Test]
    public async Task Empty_name_is_refused()
    {
        Lab lab = await StartAsync();

        Assert.That(() => lab.C1.Agent.Name = " ", Throws.InstanceOf<ArgumentException>());
    }

    [Test]
    public async Task Start_learns_the_instance_on_the_first_link()
    {
        Lab lab = await StartAsync();
        ClientReplication replication = ClientNode.Create(Path.Combine(TestDb.NewDir(), "client.db"), new SyncLog());
        await using SyncAgent agent = new(replication.Store, lab.Owner.Address, "клієнт");

        agent.Start();
        Assert.That(agent.InstanceId, Is.Null, "nobody asked the owner yet");
        agent.LinkEnabled = true;
        await Lab.WaitAsync(() => agent.GetStatus().State == AgentState.Online, TimeSpan.FromSeconds(30), "the agent did not get online");

        Assert.That(agent.InstanceId, Is.EqualTo(lab.Owner.Store.InstanceId));
        await replication.DisposeAsync();
    }
}
