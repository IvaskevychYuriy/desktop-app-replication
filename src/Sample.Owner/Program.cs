using Avalonia;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Replication;
using Replication.Owner;
using Sample.Domain;
using Sample.Lab;

namespace Sample.Owner;

/// <summary>
/// Settings come from appsettings.json, the environment (<c>Owner__ConnectionString</c>, <c>Kestrel__Endpoints__Grpc__Url</c>) or
/// the command line (<c>--Owner:ConnectionString="Data Source=x.db"</c>). <c>Owner:Headless</c> runs without a window.
/// </summary>
public static class Program
{
    public static IServiceProvider Services { get; private set; } = null!;

    [STAThread]
    public static int Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        string connectionString = builder.Configuration["Owner:ConnectionString"] ?? "Data Source=owner.db";
        bool headless = builder.Configuration.GetValue<bool>("Owner:Headless");
        var log = new SyncLog();
        if (headless)
            log.Written += e => Console.WriteLine($"{e.At:HH:mm:ss.fff} [{e.Source}] {e.Text}");
        builder.Services.AddSingleton(log);
        builder.Services.AddOwnerNode(connectionString);
        builder.Services.AddDbContextFactory<SampleDbContext>(o => o.UseSqlite(connectionString).AddInterceptors(new StampInterceptor()));
        builder.Services.AddHostedService<DataGenerator>();

        WebApplication app = builder.Build();
        app.MapReplicationOwner();
        app.Services.InitOwnerNodeAsync().GetAwaiter().GetResult();
        Services = app.Services;

        if (headless)
        {
            app.Run();
            return 0;
        }
        app.Start();
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            app.StopAsync().GetAwaiter().GetResult();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<OwnerApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
