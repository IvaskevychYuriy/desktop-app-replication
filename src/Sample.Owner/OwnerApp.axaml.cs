using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Sample.Owner.ViewModels;
using Sample.Owner.Views;

namespace Sample.Owner;

public partial class OwnerApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow { DataContext = new MainWindowViewModel(Program.Services) };
        base.OnFrameworkInitializationCompleted();
    }
}
