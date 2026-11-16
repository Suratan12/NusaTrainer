using NusaTrainer.App.Services;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public sealed class ModViewModel : ViewModelBase
{
    private readonly AppServices _services;
    private readonly GameViewModel _game;
    private string? _installedVersion;
    private bool _isWorking;
    private double _progress;
    private string? _message;

    public ModDefinition Definition { get; }
    public string Name => Definition.Name;
    public string? Description => Definition.Description;
    public string Meta => string.Join("  ·  ", new[]
    {
        "v" + Definition.Version,
        Definition.Author is { } a ? "oleh " + a : null,
        Definition.SizeBytes is { } s ? $"{s / 1024.0 / 1024.0:0.#} MB" : null,
    }.Where(x => x is not null));
    public string? Requirements => Definition.Requirements;
    public bool HasRequirements => !string.IsNullOrWhiteSpace(Definition.Requirements);

    public AsyncCommand InstallCommand { get; }
    public AsyncCommand UninstallCommand { get; }

    public ModViewModel(AppServices services, GameViewModel game, ModDefinition definition)
    {
        _services = services;
        _game = game;
        Definition = definition;
        _installedVersion = services.Mods.GetInstalled(game.Definition.Id, definition.Id)?.Version;
        InstallCommand = new AsyncCommand(InstallAsync, () => !_isWorking && !_game.IsRunning);
        UninstallCommand = new AsyncCommand(UninstallAsync, () => !_isWorking && IsInstalled && !_game.IsRunning);
    }

    public bool IsInstalled => _installedVersion is not null;
    public bool HasUpdate => IsInstalled && _installedVersion != Definition.Version;
    public string InstallLabel => HasUpdate ? "Perbarui" : IsInstalled ? "Pasang ulang" : "Pasang";
    public string StatusText => IsInstalled ? (HasUpdate ? $"Terpasang v{_installedVersion} — ada update" : "Terpasang") : "Belum terpasang";

    public bool IsWorking { get => _isWorking; private set => Set(ref _isWorking, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Message
    {
        get => _message;
        private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => !string.IsNullOrEmpty(_message);

    public void RefreshCommands()
    {
        InstallCommand.RaiseCanExecuteChanged();
        UninstallCommand.RaiseCanExecuteChanged();
    }

    private async Task InstallAsync()
    {
        var dir = _game.GameFolder ?? await _game.AskForFolderAsync();
        if (dir is null) { Message = "Pilih folder instalasi game dulu."; return; }
        IsWorking = true; Progress = 0; Message = "Mengunduh…";
        RefreshCommands();
        try
        {
            var def = Definition;
            var resolved = new ModDefinition
            {
                Id = def.Id, Name = def.Name, Version = def.Version, Sha256 = def.Sha256, InstallPath = def.InstallPath,
                DownloadUrl = _services.ResolveModUrl(def.DownloadUrl),
            };
            var progress = new Progress<double>(p => Progress = p * 100);
            await Task.Run(() => _services.Mods.InstallAsync(_game.Definition.Id, resolved, dir, progress));
            _installedVersion = def.Version;
            Message = "Berhasil dipasang. File asli game sudah dicadangkan.";
            Log.Info($"Mod {def.Id} dipasang ke {dir}");
        }
        catch (Exception ex)
        {
            Message = "Gagal: " + ex.Message;
            Log.Error($"Pasang mod {Definition.Id}", ex);
        }
        finally
        {
            IsWorking = false;
            RaiseState();
        }
    }

    private async Task UninstallAsync()
    {
        IsWorking = true; Message = null;
        RefreshCommands();
        try
        {
            await Task.Run(() => _services.Mods.Uninstall(_game.Definition.Id, Definition.Id));
            _installedVersion = null;
            Message = "Mod dicopot dan file asli dikembalikan.";
        }
        catch (Exception ex)
        {
            Message = "Gagal: " + ex.Message;
        }
        finally
        {
            IsWorking = false;
            RaiseState();
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsInstalled));
        OnPropertyChanged(nameof(HasUpdate));
        OnPropertyChanged(nameof(InstallLabel));
        OnPropertyChanged(nameof(StatusText));
        RefreshCommands();
    }
}
