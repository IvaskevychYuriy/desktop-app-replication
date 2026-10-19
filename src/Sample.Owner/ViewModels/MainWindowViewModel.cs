using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Replication;
using Sample.App.ViewModels;
using Sample.Lab;

namespace Sample.Owner.ViewModels;

public sealed class MainWindowViewModel
{
    public MainWindowViewModel(IServiceProvider services)
    {
        OwnerNode node = services.GetRequiredService<OwnerNode>();
        Feed.Attach(services.GetRequiredService<SyncLog>());
        Owner = new OwnerPanelViewModel(node);
        State = $"gRPC-сервер {node.Address}, БД {node.DbPath}";
        new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Owner.Refresh()).Start();
    }

    public OwnerPanelViewModel Owner { get; }
    public LogFeed Feed { get; } = new LogFeed();
    public string State { get; }
}
