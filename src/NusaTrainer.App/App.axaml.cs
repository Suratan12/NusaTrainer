using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using NusaTrainer.App.Services;
using NusaTrainer.App.ViewModels;
using NusaTrainer.App.Views;

namespace NusaTrainer.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new AppServices();
            var window = new MainWindow();
            services.PickFolderAsync = window.PickFolderAsync;
            var vm = new MainViewModel(services);
            window.DataContext = vm;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += (_, _) => vm.Dispose();
            window.Opened += async (_, _) => await vm.InitializeAsync();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
