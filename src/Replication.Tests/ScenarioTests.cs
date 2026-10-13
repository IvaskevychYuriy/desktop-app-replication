using System.Collections.Concurrent;
using NUnit.Framework;
using Sample.Lab;

namespace Replication.Tests;

/// <summary>Every scenario of the demo page, run against the real core over gRPC on localhost.</summary>
public sealed class ScenarioTests
{
    public static IEnumerable<string> Ids => Scenarios.All.Select(s => s.Id);

    [TestCaseSource(nameof(Ids))]
    public async Task Scenario_converges(string id)
    {
        var log = new SyncLog();
        var lines = new ConcurrentQueue<string>();
        log.Written += e => lines.Enqueue($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Text}");
        await using Lab lab = await Lab.StartAsync(log: log);
        var run = new ScenarioRunner(lab, Scenarios.Find(id));
        List<string> problems;
        try
        {
            await run.SetupAsync();
            await lab.SettleAsync();
            while (!run.Done)
            {
                await run.NextAsync();
                await lab.SettleAsync(TimeSpan.FromSeconds(60));
            }
            problems = await run.VerifyAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            foreach (string l in lines)
                TestContext.Out.WriteLine(l);
        }
        Assert.That(problems, Is.Empty);
    }
}
