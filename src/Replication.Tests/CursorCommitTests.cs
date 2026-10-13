using Microsoft.Data.Sqlite;
using NUnit.Framework;
using Replication.Model;
using Sample.Lab;

namespace Replication.Tests;

/// <summary>Design 6.4, 14: the cursors move only with the transaction that applied the batch.</summary>
public sealed class CursorCommitTests
{
    [Test]
    public async Task Batch_whose_cursor_save_fails_arrives_again()
    {
        await using Lab lab = await Lab.StartAsync();
        await lab.SyncNowAsync(lab.C1);
        await lab.SettleAsync();
        using SqliteConnection c = lab.C1.Replication.Store.Open();
        // The rows of the next batch are written, then saving the cursors fails and the transaction rolls back.
        TestDb.Exec(c, """
            CREATE TABLE _test_fault(armed INTEGER NOT NULL);
            INSERT INTO _test_fault VALUES (1);
            CREATE TRIGGER _test_fault_ins BEFORE INSERT ON _sync_cursors
            BEGIN SELECT RAISE(ABORT, 'injected') WHERE (SELECT armed FROM _test_fault) = 1; END;
            CREATE TRIGGER _test_fault_upd BEFORE UPDATE ON _sync_cursors
            BEGIN SELECT RAISE(ABORT, 'injected') WHERE (SELECT armed FROM _test_fault) = 1; END;
            """);

        await lab.Owner.AddLogAsync(5);
        await lab.Owner.NewItemAsync("Ліхтарик", category: "Склад");
        await Lab.WaitAsync(() => lab.C1.Agent.GetStatus().LastError?.Contains("injected", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(30), "збереження курсорів не впало");
        TestDb.Exec(c, "UPDATE _test_fault SET armed = 0");
        await lab.SettleAsync();

        Assert.That(Inspect.Diff(lab.Owner, lab.C1), Is.Empty);
        Dictionary<string, CursorState> saved = lab.C1.Replication.Store.LoadCursors(c, lab.C1.Instance);
        foreach ((string table, CursorState k) in saved)
            Assert.That(lab.C1.Agent.CursorOf(table).ToString(), Is.EqualTo(k.ToString()));
    }

    [Test]
    public void Working_copy_does_not_change_the_published_cursor()
    {
        var published = new CursorState(10, [(20L, 30L)]);
        CursorState copy = published.Clone();
        copy.AddRange(40, 50);
        copy.AddRange(10, 20);
        Assert.That(published.ToString(), Is.EqualTo("10 + (20,30]"));
        Assert.That(copy.ToString(), Is.EqualTo("30 + (40,50]"));
    }
}
