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

    public async Task<string?> PickFileAsync(string title, string filterName, string[] patterns)
    {
        var result = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(filterName) { Patterns = patterns }],
        });
        return result.Count > 0 ? result[0].TryGetLocalPath() : null;
    }
}
