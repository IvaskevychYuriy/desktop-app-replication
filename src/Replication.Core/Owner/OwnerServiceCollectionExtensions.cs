using System.IO.Compression;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Replication.Model;

namespace Replication.Owner;

public static class OwnerServiceCollectionExtensions
{
    /// <summary>Registers the owner: store, snapshots, gRPC, Kestrel HTTP/2 windows and the background jobs.</summary>
    public static IServiceCollection AddReplicationOwner(this IServiceCollection services, OwnerStore store)
    {
        services.AddSingleton(store);
        return services.AddReplicationOwnerServices();
    }

    /// <param name="model">Called once, when the store is first resolved.</param>
    /// <param name="configure">Runs before <see cref="OwnerOptions.Log"/> falls back to a registered <see cref="SyncLog"/>.</param>
    public static IServiceCollection AddReplicationOwner(this IServiceCollection services, string dbPath, Func<SyncModel> model, Action<OwnerOptions>? configure = null)
    {
        services.AddSingleton(sp =>
        {
            var options = new OwnerOptions { Log = sp.GetService<SyncLog>() ?? SyncLog.Null };
            configure?.Invoke(options);
            return new OwnerStore(dbPath, model(), options);
        });
        return services.AddReplicationOwnerServices();
    }

    private static IServiceCollection AddReplicationOwnerServices(this IServiceCollection services)
    {
        services.TryAddSingleton<SnapshotStore>();
        services.AddGrpc(o =>
        {
            // Gzip: 3-5x on table data (6.1).
            o.ResponseCompressionAlgorithm = "gzip";
            o.ResponseCompressionLevel = CompressionLevel.Optimal;
            o.MaxReceiveMessageSize = 32 * 1024 * 1024;
            o.MaxSendMessageSize = 32 * 1024 * 1024;
            o.EnableDetailedErrors = true;
        });
        services.AddOptions<KestrelServerOptions>().Configure<OwnerStore>((k, store) =>
        {
            k.Limits.Http2.InitialStreamWindowSize = store.Options.Http2StreamWindowBytes;
            k.Limits.Http2.InitialConnectionWindowSize = store.Options.Http2ConnectionWindowBytes;
            k.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(20);
            k.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
        });
        services.AddHostedService<OwnerJobs>();
        return services;
    }

    public static IEndpointConventionBuilder MapReplicationOwner(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapGrpcService<SyncGrpcService>();

    /// <summary>Creates service tables and triggers (<see cref="OwnerStore.Install"/>). Call after EF migrations, before the host starts.</summary>
    public static IServiceProvider InstallReplicationOwner(this IServiceProvider services)
    {
        services.GetRequiredService<OwnerStore>().Install();
        return services;
    }

    public static T InstallReplicationOwner<T>(this T host) where T : IHost
    {
        host.Services.InstallReplicationOwner();
        return host;
    }
}
