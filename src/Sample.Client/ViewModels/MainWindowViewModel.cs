using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Replication;
using Replication.Client;
using Sample.App.ViewModels;
using Sample.Lab;

namespace Sample.Client.ViewModels;

/// <summary>One local database, installed once at start; every added owner is another agent over it.</summary>
public sealed partial class MainWindowViewModel : ObservableObject, IAsyncDisposable
{
    private readonly SyncLog _log = new();
    private readonly ClientReplication _replication;

    [ObservableProperty]
    private string _url = "http://127.0.0.1:5005";

    [ObservableProperty]
    private string _ownerName = "Власник 1";

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private ClientPanelViewModel? _selectedOwner;

    public MainWindowViewModel(string connectionString)
    {
        Feed.Attach(_log);
        _replication = ClientNode.Open(connectionString, _log);
        State = $"БД {_replication.Store.DbPath}, client_id {_replication.ClientId}";
        new DispatcherTimer(TimeSpan.FromMilliseconds(400), DispatcherPriority.Background, (_, _) => Owners.ToList().ForEach(o => o.Refresh())).Start();
    }

    public LogFeed Feed { get; } = new LogFeed();
    public ObservableCollection<ClientPanelViewModel> Owners { get; } = [];
    public string State { get; }

    [RelayCommand]
    private void AddOwner()
    {
        try
        {
            Error = null;
            string name = OwnerName.Trim();
            if (!Uri.TryCreate(Url.Trim(), UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("Потрібна адреса виду http://host:port");
            string address = uri.GetLeftPart(UriPartial.Authority);
            if (name.Length == 0)
                throw new ArgumentException("Потрібна назва власника");
            if (Owners.Any(o => string.Equals(o.Node.Agent.Address, address, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Цього власника вже додано");
            if (Owners.Any(o => o.Node.Label == name))
                throw new ArgumentException("Назва вже зайнята");

            ClientNode node = ClientNode.Connect(_replication, _log, name, address);
            node.Link = true;
            var panel = new ClientPanelViewModel(node);
            Owners.Add(panel);
            SelectedOwner = panel;
            OwnerName = $"Власник {Owners.Count + 1}";
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    public ValueTask DisposeAsync() => _replication.DisposeAsync();
}
