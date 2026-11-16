using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace NusaTrainer.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public async Task<string?> PickFolderAsync(string title)
    {
        var result = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
