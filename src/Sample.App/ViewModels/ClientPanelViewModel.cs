using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sample.Lab;

namespace Sample.App.ViewModels;

/// <summary>
/// A client: one database with a replica per owner (4). Per owner its agent (<see cref="AgentViewModel"/>); the grids show
/// the rows of every replica together, and an edit goes to the agent of the row's owner.
/// </summary>
public sealed partial class ClientPanelViewModel : ObservableObject
{
    public static readonly string[] Statuses = ["новий", "активний", "архів"];

    [ObservableProperty]
    private int _selectedTab;

    [ObservableProperty]
    private RowView? _selectedItem;

    [ObservableProperty]
    private RowView? _selectedCategory;

    [ObservableProperty]
    private AgentViewModel _target;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editPrice = "";

    [ObservableProperty]
    private string _editStatus = "новий";

    [ObservableProperty]
    private string? _error;

    public ClientPanelViewModel(ClientNode node)
        : this(node.Label, [node])
    {
    }

    /// <param name="nodes">The client's replicas, one per owner, all on the same <see cref="ClientReplication"/>.</param>
    public ClientPanelViewModel(string title, IReadOnlyList<ClientNode> nodes)
    {
        Title = title;
        Agents = [.. nodes.Select(n => new AgentViewModel(n, nodes.Count > 1))];
        _target = Agents[0];
    }

    public string Title { get; }

    public IReadOnlyList<AgentViewModel> Agents { get; }

    /// <summary>Several owners: the grids get an owner column, new rows a choice of owner.</summary>
    public bool ManyOwners => Agents.Count > 1;

    private ClientNode First => Agents[0].Node;

    public IReadOnlyList<string> StatusOptions => Statuses;

    public ObservableCollection<RowView> Items { get; } = [];
    public ObservableCollection<RowView> Categories { get; } = [];
    public ObservableCollection<RowView> Log { get; } = [];
    public ObservableCollection<string> Notes { get; } = [];

    partial void OnSelectedTabChanged(int value)
    {
        string table = value switch { 1 => "Category", 2 => "Log", _ => "Item" };
        foreach (AgentViewModel a in Agents)
            a.Node.Agent.SetOpenTables(table);
    }

    partial void OnSelectedItemChanged(RowView? value)
    {
        if (value is null || value.IsArchived)
            return;
        EditName = value.Label;
        EditPrice = value.Price?.ToString() ?? "";
        EditStatus = value.Status ?? "новий";
    }

    public void Refresh()
    {
        try
        {
            foreach (AgentViewModel a in Agents)
                a.Refresh();
            Items.SyncTo(Rows("Item"));
            Categories.SyncTo(Rows("Category"));
            Log.SyncTo(Rows("Log", 30));
            Notes.SyncTo([.. First.Replication.Store.Notes(limit: 20).Select(n => $"{n.At.ToLocalTime():HH:mm:ss} {n.Text}")]);
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
    }

    /// <summary>One owner: as the replica has them. Several: one list of the client's database, the latest change on top.</summary>
    private List<RowView> Rows(string table, int limit = 200) =>
        ManyOwners
            ? [.. Agents.SelectMany(a => Inspect.ClientRows(a.Node, table, limit, newestFirst: true).Select(r => r with { Owner = a.Owner }))
                .OrderByDescending(r => r.ModifiedOn).ThenByDescending(r => r.Version).Take(limit)]
            : Inspect.ClientRows(First, table, limit);

    /// <summary>The replica a row of the grid belongs to and its id; archived rows are read-only.</summary>
    private (ClientNode Node, Guid Id)? Live(RowView? r) =>
        r is { IsArchived: false } && Agents.FirstOrDefault(a => a.Node.Agent.InstanceId == r.InstanceId) is { } a ? (a.Node, Guid.Parse(r.Id)) : null;

    private async Task Run(Func<Task> action)
    {
        try
        {
            Error = null;
            await action();
            Refresh();
        }
        catch (Exception e)
        {
            Error = e.InnerException?.Message ?? e.Message;
        }
    }

    [RelayCommand]
    private Task Save() => Run(() => Live(SelectedItem) is { } x && long.TryParse(EditPrice, out long p)
        ? x.Node.UpdateItemAsync(x.Id, EditName, p, EditStatus)
        : Task.CompletedTask);

    [RelayCommand]
    private Task Delete() => Run(() => Live(SelectedItem) is { } x ? x.Node.DeleteAsync("Item", x.Id) : Task.CompletedTask);

    [RelayCommand]
    private Task Archive() => Run(() => Live(SelectedItem) is { } x ? x.Node.ArchiveByIdAsync("Item", x.Id) : Task.CompletedTask);

    [RelayCommand]
    private Task AddItem() => Run(Target.Node.AddItemAsync);

    [RelayCommand]
    private Task AddCategory() => Run(() => Target.Node.CreateCategoryAsync($"Категорія {Categories.Count + 1}"));

    [RelayCommand]
    private Task DeleteCategory() => Run(() => Live(SelectedCategory) is { } x ? x.Node.DeleteAsync("Category", x.Id) : Task.CompletedTask);

    [RelayCommand]
    private Task ArchiveCategory() => Run(() => Live(SelectedCategory) is { } x ? x.Node.ArchiveByIdAsync("Category", x.Id) : Task.CompletedTask);

    /// <summary>One predicate action per owner: each replica deletes its own rows.</summary>
    [RelayCommand]
    private Task DeleteArchived() => Run(async () =>
    {
        foreach (AgentViewModel a in Agents.Where(a => a.Node.Agent.InstanceId is not null))
            await a.Node.DeleteWhereStatusAsync("архів");
    });
}
