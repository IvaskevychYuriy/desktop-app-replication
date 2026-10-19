using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Replication.Owner;

/// <summary>
/// Standalone gRPC server for an owner process without a web host of its own (lab, tests).
/// An app that already has one registers the owner with <see cref="OwnerServiceCollectionExtensions.AddReplicationOwner(IServiceCollection, OwnerStore)"/>.
/// </summary>
public sealed class OwnerHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private OwnerHost(WebApplication app, OwnerStore store, int port)
    {
        _app = app;
        Store = store;
        Port = port;
    }

    public OwnerStore Store { get; }
    public int Port { get; }
    public string Address => $"http://127.0.0.1:{Port}";

    /// <param name="port">0 picks a free port.</param>
    /// <param name="listenAnywhere">Listen on all interfaces instead of loopback only.</param>
    public static async Task<OwnerHost> StartAsync(OwnerStore store, int port = 0, bool listenAnywhere = false)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            // Plain HTTP/2 (h2c), no TLS in v1 (2).
            void Listen(ListenOptions o) => o.Protocols = HttpProtocols.Http2;
            if (listenAnywhere)
                k.ListenAnyIP(port, Listen);
            else
                k.Listen(System.Net.IPAddress.Loopback, port, Listen);
        });
        builder.Services.AddReplicationOwner(store);
        WebApplication app = builder.Build();
        app.MapReplicationOwner();
        await app.StartAsync();
        int actualPort = BindingAddress.Parse(app.Urls.First()).Port;
        store.Options.Log.Write("owner", $"gRPC-сервер слухає порт {actualPort}, instance_id {store.InstanceId}", SyncLogLevel.Ok);
        return new OwnerHost(app, store, actualPort);
    }

    public async ValueTask DisposeAsync()
    {
        using (var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            await _app.StopAsync(stop.Token);
        await _app.DisposeAsync();
    }
}
