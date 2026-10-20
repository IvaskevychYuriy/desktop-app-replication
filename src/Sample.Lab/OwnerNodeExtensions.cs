using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Replication;
using Replication.Owner;

namespace Sample.Lab;

public static class OwnerNodeExtensions
{
    /// <summary>The owner for an app that has its own web host: replication services plus <see cref="OwnerNode"/> over <paramref name="connectionString"/>.</summary>
    public static IServiceCollection AddOwnerNode(this IServiceCollection services, string connectionString, Action<OwnerOptions>? configure = null)
    {
        services.TryAddSingleton<SyncLog>();
        services.AddReplicationOwner(connectionString, () => OwnerNode.ModelOf(connectionString), configure);
        services.AddSingleton(sp => new OwnerNode(
            sp.GetRequiredService<OwnerStore>(),
            sp.GetRequiredService<SyncLog>(),
            () => sp.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault() ?? ""));
        return services;
    }

    /// <summary>Creates the schema and installs replication. Call before the host starts.</summary>
    public static async Task InitOwnerNodeAsync(this IServiceProvider services)
    {
        OwnerNode node = services.GetRequiredService<OwnerNode>();
        await OwnerNode.EnsureCreatedAsync(node.Store.ConnectionString);
        services.InstallReplicationOwner();
    }
}
