using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Replication;
using Replication.Model;
using Replication.Owner;
using Sample.Domain;

namespace Sample.Owner;

public static class OwnerExtensions
{
    /// <summary>Wires the replication owner over the sample database at <paramref name="connectionString"/>.</summary>
    public static IServiceCollection AddSampleOwner(this IServiceCollection services, string connectionString, Action<OwnerOptions>? configure = null)
    {
        services.TryAddSingleton<SyncLog>();
        return services.AddReplicationOwner(connectionString, () =>
        {
            using SampleDbContext db = SampleDbContext.Open(connectionString);
            return SyncModel.From(db);
        }, configure);
    }

    /// <summary>Creates the schema and installs replication triggers. Call before the host starts.</summary>
    public static async Task InitSampleOwnerAsync(this IServiceProvider services, string connectionString)
    {
        await using (SampleDbContext db = SampleDbContext.Open(connectionString))
            await db.Database.EnsureCreatedAsync();
        services.InstallReplicationOwner();
    }
}
