using Avalonia;

namespace Sample.Client;

/// <summary>Usage: <c>ReplicationClient [--connection "Data Source=client.db"]</c>.</summary>
public static class Program
{
    public static string ConnectionString { get; private set; } = "Data Source=client.db";

    [STAThread]
    public static int Main(string[] args)
    {
        int i = Array.IndexOf(args, "--connection");
        if (i >= 0 && i + 1 < args.Length)
            ConnectionString = args[i + 1];
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ClientApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
