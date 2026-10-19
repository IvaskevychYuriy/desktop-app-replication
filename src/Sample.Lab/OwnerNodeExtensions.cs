using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Replication;
using Replication.Owner;

namespace Sample.Lab;

public static class OwnerNodeExtensions
{
    /// <summary>The owner for an app that has its own web host: replication services plus <see cref="OwnerNode"/> over <paramref name="dbPath"/>.</summary>
    public static IServiceCollection AddOwnerNode(this IServiceCollection services, string dbPath, Action<OwnerOptions>? configure = null)
    {
        services.TryAddSingleton<SyncLog>();
        services.AddReplicationOwner(dbPath, () => OwnerNode.ModelOf(dbPath), configure);
        services.AddSingleton(sp => new OwnerNode(
            sp.GetRequiredService<OwnerStore>(),
            sp.GetRequiredService<SyncLog>(),
            () => sp.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? ""));
        return services;
    }

    /// <summary>Creates the schema, installs replication and seeds an empty database. Call before the host starts.</summary>
    public static async Task InitOwnerNodeAsync(this IServiceProvider services)
    {
        OwnerNode node = services.GetRequiredService<OwnerNode>();
        bool empty = await OwnerNode.EnsureCreatedAsync(node.DbPath);
        services.InstallReplicationOwner();
        if (empty)
            await node.SeedAsync();
    }
}
