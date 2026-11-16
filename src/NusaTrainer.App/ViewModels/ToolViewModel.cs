using NusaTrainer.App.Services;
using NusaTrainer.Core.Library;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

/// <summary>Trainer .exe atau tabel .CT milik pengguna.</summary>
public sealed class ToolViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameViewModel _game;
    private string? _message;

    public ExternalTool Tool { get; }
    public string Name => Tool.Name;
    public string Path => Tool.Path;
    public bool IsTrainer => Tool.Kind == ExternalToolKind.Trainer;
    public string KindLabel => IsTrainer ? "Trainer" : "Tabel Cheat Engine";
    public string LaunchLabel => IsTrainer ? "▶  Jalankan" : "Buka di Cheat Engine";
    public bool Exists => File.Exists(Tool.Path);

    public RelayCommand LaunchCommand { get; }
    public RelayCommand RemoveCommand { get; }

    public ToolViewModel(AppServices services, GameViewModel game, ExternalTool tool)
    {
        _services = services;
        _game = game;
        Tool = tool;
        LaunchCommand = new RelayCommand(() => Launch(manual: true));
        RemoveCommand = new RelayCommand(() => _game.RemoveTool(this));
    }

    public bool AutoLaunch
    {
        get => Tool.AutoLaunch;
        set
        {
            if (Tool.AutoLaunch == value) return;
            Tool.AutoLaunch = value;
            _services.SaveLibrary();
            OnPropertyChanged();
        }
    }

    public string? Message
    {
        get => _message;
        set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public void Launch(bool manual)
    {
        if (_game.BlockReason is { } reason)
        {
            Message = reason;
            return;
        }
        try
        {
            ToolLauncher.Launch(Tool);
            Message = manual ? null : "Dijalankan otomatis karena game terdeteksi.";
            Log.Info($"Alat dijalankan: {Tool.Path}");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            Log.Warn($"Gagal menjalankan {Tool.Path}: {ex.Message}");
        }
    }
}

public sealed class TrainerLinkViewModel(ExternalTrainerLink link)
{
    public string Name => link.Name;
    public string Meta => string.Join(" · ", new[] { link.Author is { } a ? "oleh " + a : null, link.Notes }.Where(x => x is not null));
    public bool HasMeta => Meta.Length > 0;
    public RelayCommand OpenCommand { get; } = new(() => AppServices.OpenUrl(link.Homepage));
}
