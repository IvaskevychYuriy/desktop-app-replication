using System.IO.Compression;
using Grpc.AspNetCore.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Replication.Model;

namespace Replication.Owner;

/// <summary>Owner registration for any ASP.NET Core host. The host keeps URLs, ports, protocols and TLS.</summary>
public static class OwnerHostingExtensions
{
    /// <summary>Registers the owner store, the snapshot store and the housekeeping jobs.</summary>
    public static IServiceCollection AddOwnerServices(this IServiceCollection services, string dbPath, SyncModel model, OwnerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return services
            .AddSingleton(new OwnerStore(dbPath, model, options))
            .AddSingleton<SnapshotStore>()
            .AddHostedService<SyncHousekeepingJobsService>();
    }

    /// <summary>Registers gRPC with the sync defaults; <paramref name="configure"/> runs after them.</summary>
    public static IServiceCollection AddSyncGrpcService(this IServiceCollection services, Action<GrpcServiceOptions>? configure = null)
    {
        services.AddGrpc(o =>
        {
            // Gzip: 3-5x on table data (6.1).
            o.ResponseCompressionAlgorithm = "gzip";
            o.ResponseCompressionLevel = CompressionLevel.Optimal;
            o.MaxReceiveMessageSize = 32 * 1024 * 1024;
            o.MaxSendMessageSize = 32 * 1024 * 1024;
            o.EnableDetailedErrors = true;
            configure?.Invoke(o);
        });
        return services;
    }

    /// <summary>HTTP/2 windows from <see cref="OwnerOptions"/> and keep-alive pings; <paramref name="configure"/> runs after them.</summary>
    public static IServiceCollection AddSyncKestrelLimits(this IServiceCollection services, Action<KestrelServerLimits>? configure = null)
    {
        services.AddOptions<KestrelServerOptions>().Configure<OwnerStore>((k, store) =>
        {
            OwnerOptions o = store.Options;
            k.Limits.Http2.InitialStreamWindowSize = o.Http2StreamWindowBytes;
            k.Limits.Http2.InitialConnectionWindowSize = o.Http2ConnectionWindowBytes;
            k.Limits.Http2.KeepAlivePingDelay = TimeSpan.FromSeconds(20);
            k.Limits.Http2.KeepAlivePingTimeout = TimeSpan.FromSeconds(20);
            configure?.Invoke(k.Limits);
        });
        return services;
    }

    public static IEndpointRouteBuilder MapSyncGrpcService(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGrpcService<SyncGrpcService>();
        return endpoints;
    }

    /// <summary>Call after the application's own migrations and before the host starts.</summary>
    public static IHost InstallOwnerStore(this IHost host)
    {
        host.Services.GetRequiredService<OwnerStore>().Install();
        return host;
    }
}
