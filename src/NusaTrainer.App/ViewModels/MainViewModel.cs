using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using NusaTrainer.App.Services;
using NusaTrainer.Core.Database;
using NusaTrainer.Core.Games;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Update;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _processTimer;
    private readonly DispatcherTimer _updateTimer;
    private List<GameViewModel> _allGames = new();
    private GameViewModel? _selectedGame;
    private string _searchText = "";
    private string _statusMessage = "Siap.";
    private string _databaseInfo = "";
    private bool _isUpdating;
    private AppReleaseInfo? _appRelease;
    private string? _appUpdateText;
    private double _appUpdateProgress;
    private bool _isSettingsOpen;
    private bool _scanning;

    public ObservableCollection<GameViewModel> FilteredGames { get; } = new();
    public SettingsViewModel Settings { get; }

    public AsyncCommand UpdateDatabaseCommand { get; }
    public AsyncCommand InstallAppUpdateCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }

    public string AppVersionText => "v" + _services.AppVersion;
    public bool IsElevated => !OperatingSystem.IsWindows() || Environment.IsPrivilegedProcess;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Settings = new SettingsViewModel(services, onSaved: () => _ = UpdateDatabaseAsync(silent: false));
        Settings.Closed += () => IsSettingsOpen = false;

        UpdateDatabaseCommand = new AsyncCommand(() => UpdateDatabaseAsync(silent: false), () => !_isUpdating);
        InstallAppUpdateCommand = new AsyncCommand(InstallAppUpdateAsync, () => _appRelease is not null);
        OpenSettingsCommand = new RelayCommand(() => { Settings.Load(); IsSettingsOpen = true; });
        OpenDataFolderCommand = new RelayCommand(() =>
        {
            try { Process.Start(new ProcessStartInfo(AppPaths.DataRoot) { UseShellExecute = true }); } catch { }
        });

        _processTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += async (_, _) => await ScanProcessesAsync();

        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(Math.Max(1, services.Settings.UpdateIntervalHours)) };
        _updateTimer.Tick += async (_, _) => await UpdateDatabaseAsync(silent: true);
    }

    public async Task InitializeAsync()
    {
        try
        {
            if (Directory.Exists(AppPaths.SeedDatabaseDir) && _services.Store.SeedFrom(AppPaths.SeedDatabaseDir))
                Log.Info("Database bawaan disalin ke folder data.");
        }
        catch (Exception ex) { Log.Error("Seed database", ex); }

        ReloadGames();
        _processTimer.Start();
        await ScanProcessesAsync();

        if (_services.Settings.AutoUpdateOnStart)
        {
            await UpdateDatabaseAsync(silent: true);
            _updateTimer.Start();
        }
    }

    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) ApplyFilter(); }
    }

    public GameViewModel? SelectedGame
    {
        get => _selectedGame;
        set { if (Set(ref _selectedGame, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public bool HasSelection => _selectedGame is not null;

    public string StatusMessage { get => _statusMessage; private set => Set(ref _statusMessage, value); }
    public string DatabaseInfo { get => _databaseInfo; private set => Set(ref _databaseInfo, value); }

    public bool IsUpdating
    {
        get => _isUpdating;
        private set { if (Set(ref _isUpdating, value)) UpdateDatabaseCommand.RaiseCanExecuteChanged(); }
    }

    public bool AppUpdateAvailable => _appUpdateText is not null;
    public string? AppUpdateText
    {
        get => _appUpdateText;
        private set { if (Set(ref _appUpdateText, value)) OnPropertyChanged(nameof(AppUpdateAvailable)); }
    }
    public double AppUpdateProgress { get => _appUpdateProgress; private set => Set(ref _appUpdateProgress, value); }

    public bool IsSettingsOpen { get => _isSettingsOpen; set => Set(ref _isSettingsOpen, value); }

    private void ReloadGames()
    {
        var defs = _services.Store.LoadGames(w => Log.Warn(w));
        var old = _allGames.ToDictionary(g => g.Definition.Id, StringComparer.OrdinalIgnoreCase);
        var next = new List<GameViewModel>();
        foreach (var def in defs)
        {
            if (old.Remove(def.Id, out var existing) && existing.Fingerprint == Json.Serialize(def))
            {
                next.Add(existing); // tidak berubah: pertahankan sesi & cheat yang sedang aktif
                continue;
            }
            existing?.Dispose();
            next.Add(new GameViewModel(_services, def));
        }
        foreach (var removed in old.Values) removed.Dispose();

        var selectedId = _selectedGame?.Definition.Id;
        _allGames = next;
        ApplyFilter();
        SelectedGame = _allGames.FirstOrDefault(g => g.Definition.Id == selectedId) ?? FilteredGames.FirstOrDefault();

        var index = _services.Store.LoadIndex();
        DatabaseInfo = index is null
            ? "Database kosong."
            : $"{_allGames.Count} game · {_allGames.Sum(g => g.Definition.Cheats.Count)} cheat\nDiperbarui {index.Updated.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)}";
    }

    private void ApplyFilter()
    {
        var q = _searchText.Trim();
        var items = _allGames
            .Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(g => g.IsRunning)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var selected = _selectedGame;
        FilteredGames.Clear();
        foreach (var g in items) FilteredGames.Add(g);
        if (selected is not null && items.Contains(selected)) SelectedGame = selected;
    }

    private async Task ScanProcessesAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            var games = _allGames;
            var results = await Task.Run(() =>
            {
                var snap = ProcessFinder.Snapshot();
                return games.Select(g => (g, running: ProcessFinder.Find(g.Definition, snap))).ToList();
            });
            bool wasRunning = false;
            foreach (var (g, running) in results)
            {
                bool before = g.IsRunning;
                g.UpdateProcess(running);
                wasRunning |= before != (running is not null);
                // Otomatis pilih game yang baru saja dijalankan.
                if (!before && running is not null) SelectedGame = g;
            }
            if (wasRunning) ApplyFilter();
        }
        catch (Exception ex) { Log.Error("Scan proses", ex); }
        finally { _scanning = false; }
    }

    private async Task UpdateDatabaseAsync(bool silent)
    {
        if (!_services.Settings.IsDatabaseUrlConfigured)
        {
            StatusMessage = "URL database online belum diatur — memakai database bawaan. Buka Pengaturan untuk mengaktifkan auto-update.";
            return;
        }
        IsUpdating = true;
        try
        {
            var progress = new Progress<string>(m => StatusMessage = m);
            var updater = _services.CreateUpdater();
            var result = await Task.Run(() => updater.UpdateAsync(progress));
            _services.Settings.LastUpdateCheck = DateTimeOffset.Now;
            _services.SaveSettings();

            if (result.AppTooOld)
                StatusMessage = $"Database terbaru butuh NusaTrainer versi {result.Index.MinAppVersion ?? "lebih baru"}. Perbarui aplikasi.";
            if (result.Changed) ReloadGames();

            if (_services.Settings.CheckAppUpdates && result.Index.App is { } app
                && SemVer.Compare(app.Version, _services.AppVersion) > 0)
            {
                _appRelease = app;
                AppUpdateText = $"NusaTrainer {app.Version} tersedia." + (app.Notes is { } n ? " " + n : "");
                InstallAppUpdateCommand.RaiseCanExecuteChanged();
            }
            Log.Info($"Update database: +{result.Added} ~{result.Updated} -{result.Removed}");
        }
        catch (Exception ex)
        {
            StatusMessage = (silent ? "Pembaruan otomatis gagal: " : "Gagal memperbarui: ") + ex.Message;
            Log.Error("Update database", ex);
        }
        finally { IsUpdating = false; }
    }

    private async Task InstallAppUpdateAsync()
    {
        if (_appRelease is null) return;
        if (!OperatingSystem.IsWindows()) { StatusMessage = "Self-update hanya tersedia di Windows."; return; }
        try
        {
            StatusMessage = $"Mengunduh NusaTrainer {_appRelease.Version}…";
            var progress = new Progress<double>(p => AppUpdateProgress = p * 100);
            var staging = await Task.Run(() => _services.AppUpdater.DownloadAndStageAsync(_appRelease, progress));
            StatusMessage = "Memasang pembaruan — aplikasi akan dibuka ulang…";
            foreach (var g in _allGames) g.Dispose(); // kembalikan semua patch sebelum keluar
            AppUpdater.LaunchInstallerAndExit(staging);
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime d)
                d.Shutdown();
        }
        catch (Exception ex)
        {
            StatusMessage = "Gagal memperbarui aplikasi: " + ex.Message;
            Log.Error("App update", ex);
        }
    }

    public void Dispose()
    {
        _processTimer.Stop();
        _updateTimer.Stop();
        foreach (var g in _allGames) g.Dispose();
        _services.Dispose();
    }
}
