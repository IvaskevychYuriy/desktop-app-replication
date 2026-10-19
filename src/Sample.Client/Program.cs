using Avalonia;

namespace Sample.Client;

/// <summary>Usage: <c>ReplicationClient [--db client.db]</c>.</summary>
public static class Program
{
    public static string DbPath { get; private set; } = "client.db";

    [STAThread]
    public static int Main(string[] args)
    {
        int i = Array.IndexOf(args, "--db");
        if (i >= 0 && i + 1 < args.Length)
            DbPath = args[i + 1];
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<ClientApp>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
