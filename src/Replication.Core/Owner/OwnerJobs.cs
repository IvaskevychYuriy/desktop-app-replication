using Microsoft.Extensions.Hosting;

namespace Replication.Owner;

/// <summary>Owner background jobs: version file (5.3), floor refresh (5.2) and tombstone cleanup (10.2).</summary>
public sealed class OwnerJobs(OwnerStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        DateTimeOffset lastPurge = DateTimeOffset.UtcNow;
        DateTimeOffset lastFloor = DateTimeOffset.UtcNow;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                store.WriteVersionFile();
                if (DateTimeOffset.UtcNow - lastFloor > TimeSpan.FromMinutes(1))
                {
                    // A client that left the activity window stops holding the base back.
                    store.RecomputeFloor();
                    lastFloor = DateTimeOffset.UtcNow;
                }
                if (DateTimeOffset.UtcNow - lastPurge > store.Options.PurgeInterval)
                {
                    OwnerStore.PurgeResult r = store.Purge();
                    if (r.Tombstones > 0)
                        store.Options.Log.Write("owner", $"очищення: видалено {r.Tombstones} tombstones, purged_version = {r.PurgedVersion}");
                    store.IncrementalVacuum(256);
                    lastPurge = DateTimeOffset.UtcNow;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                store.Options.Log.Write("owner", "фонова задача: " + e.Message, SyncLogLevel.Bad);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            store.WriteVersionFile();
        }
        catch { }
    }
}
