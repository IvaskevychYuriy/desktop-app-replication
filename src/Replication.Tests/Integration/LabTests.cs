using NUnit.Framework;
using Replication.Owner;
using Sample.Lab;

namespace Replication.Tests.Integration;

/// <summary>
/// Base for tests on the lab: an owner seeded with 3 categories, 9 items and 3 log entries, and two clients, each with
/// its own SQLite file, over real gRPC on localhost. The sync log of the test is written to the test output.
/// </summary>
public abstract class LabTests
{
    private readonly List<SyncLogEntry> _log = [];
    private Lab? _lab;

    protected Lab Lab => _lab ?? throw new InvalidOperationException("call StartAsync first");

    protected async Task<Lab> StartAsync(Action<OwnerOptions>? configureOwner = null)
    {
        var log = new SyncLog();
        log.Written += e =>
        {
            lock (_log)
                _log.Add(e);
        };
        _lab = await Lab.StartAsync(log: log, configureOwner: configureOwner);
        return _lab;
    }

    /// <summary>A lab with both clients online and caught up.</summary>
    protected async Task<Lab> StartSyncedAsync(Action<OwnerOptions>? configureOwner = null)
    {
        Lab lab = await StartAsync(configureOwner);
        await lab.SyncNowAsync(lab.C1);
        await lab.SyncNowAsync(lab.C2);
        await lab.SettleAsync();
        return lab;
    }

    /// <summary>Lines the owner wrote about the session of <paramref name="client"/>.</summary>
    protected List<string> OwnerLogAbout(ClientNode client)
    {
        string who = $"[{client.Replication.ClientId[..8]}] ";
        lock (_log)
            return [.. _log.Where(e => e.Source == "owner" && e.Text.StartsWith(who, StringComparison.Ordinal)).Select(e => e.Text[who.Length..])];
    }

    /// <summary>Lines the agent of <paramref name="client"/> wrote.</summary>
    protected List<string> ClientLog(ClientNode client)
    {
        lock (_log)
            return [.. _log.Where(e => e.Source == client.Agent.Name).Select(e => e.Text)];
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
}
