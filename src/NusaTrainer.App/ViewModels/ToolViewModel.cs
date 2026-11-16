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

/// <summary>Trainer dari katalog database: tautan resmi + (opsional) unduhan langsung terverifikasi.</summary>
public sealed class TrainerLinkViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameViewModel _game;
    private readonly ExternalTrainerLink _link;
    private bool _isWorking;
    private double _progress;
    private string? _message;

    public TrainerLinkViewModel(AppServices services, GameViewModel game, ExternalTrainerLink link)
    {
        _services = services;
        _game = game;
        _link = link;
        Match = TrainerInstaller.CheckVersion(link, game.Installed);
        InstallCommand = new AsyncCommand(InstallAsync, () => !_isWorking && CanDownload);
        OpenCommand = new RelayCommand(() => AppServices.OpenUrl(link.Homepage));
    }

    public string Name => _link.Name;
    public string Meta => string.Join(" · ", new[]
    {
        _link.TrainerVersion is { } tv ? "v" + tv : null,
        _link.Author is { } a ? "oleh " + a : null,
        _link.Notes,
    }.Where(x => x is not null));
    public bool HasMeta => Meta.Length > 0;
    public bool CanDownload => _link.DownloadUrl is not null && _link.Sha256 is not null;

    public VersionMatch Match { get; }
    private string SupportedText => string.Join(", ",
        (_link.GameVersions ?? []).Concat((_link.SteamBuildIds ?? []).Select(b => "build " + b)));
    public string? MatchText => Match switch
    {
        VersionMatch.Match => "✓ Cocok dengan versi game Anda",
        VersionMatch.Mismatch => $"⚠ Dibuat untuk versi lain ({SupportedText}) — bisa tidak berfungsi",
        VersionMatch.Unknown => $"Versi game tidak terdeteksi · mendukung {SupportedText}",
        _ => null,
    };
    public bool HasMatchText => MatchText is not null;
    public string MatchColor => Match switch
    {
        VersionMatch.Match => "#86EFAC",
        VersionMatch.Mismatch => "#FCD34D",
        _ => "#94A3B8",
    };

    public bool IsInstalled => _link.Sha256 is { } sha && _game.Tools.Any(t => t.Path.Contains(sha[..16], StringComparison.OrdinalIgnoreCase));
    public string InstallLabel => IsInstalled ? "Pasang ulang" : "⬇  Unduh & pasang";

    public bool IsWorking { get => _isWorking; private set { Set(ref _isWorking, value); InstallCommand.RaiseCanExecuteChanged(); } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Message
    {
        get => _message;
        private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public AsyncCommand InstallCommand { get; }
    public RelayCommand OpenCommand { get; }

    private async Task InstallAsync()
    {
        if (_game.BlockReason is { } reason) { Message = reason; return; }
        IsWorking = true;
        Progress = 0;
        Message = "Mengunduh langsung dari sumber terverifikasi…";
        try
        {
            var url = _services.ResolveModUrl(_link.DownloadUrl!);
            var progress = new Progress<double>(p => Progress = p * 100);
            var exe = await Task.Run(() => _services.Trainers.InstallAsync(_game.LibraryKey, _link, url, progress));
            _game.AddInstalledTrainer(exe, _link.Name);
            Message = "Terpasang & terverifikasi (SHA-256 cocok). Jalankan dari daftar \"Trainer Anda\" di atas."
                      + (Match == VersionMatch.Mismatch ? " Catatan: versi game Anda berbeda." : "");
            Log.Info($"Trainer katalog dipasang: {_link.Name} → {exe}");
        }
        catch (Exception ex)
        {
            Message = "Gagal: " + ex.Message;
            Log.Error($"Unduh trainer {_link.Name}", ex);
        }
        finally
        {
            IsWorking = false;
            OnPropertyChanged(nameof(IsInstalled));
            OnPropertyChanged(nameof(InstallLabel));
        }
    }
}
