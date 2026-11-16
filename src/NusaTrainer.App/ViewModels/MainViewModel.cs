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
    private List<InstalledGame> _installed = new();
    private bool _showInstalledOnly;
    private bool _isScanningLibrary;

    public ObservableCollection<GameViewModel> FilteredGames { get; } = new();
    public SettingsViewModel Settings { get; }

    public AsyncCommand UpdateDatabaseCommand { get; }
    public AsyncCommand InstallAppUpdateCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public AsyncCommand RescanCommand { get; }
    public AsyncCommand AddManualGameCommand { get; }
    public RelayCommand ShowInstalledCommand { get; }
    public RelayCommand ShowAllCommand { get; }

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

        RescanCommand = new AsyncCommand(() => ScanInstalledAsync(announce: true));
        AddManualGameCommand = new AsyncCommand(AddManualGameAsync);
        ShowInstalledCommand = new RelayCommand(() => ShowInstalledOnly = true);
        ShowAllCommand = new RelayCommand(() => ShowInstalledOnly = false);

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
        await ScanInstalledAsync(announce: false);
        ShowInstalledOnly = _allGames.Any(g => g.IsInstalled);
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

    public bool ShowInstalledOnly
    {
        get => _showInstalledOnly;
        set
        {
            if (!Set(ref _showInstalledOnly, value)) return;
            OnPropertyChanged(nameof(ShowAll));
            ApplyFilter();
        }
    }
    public bool ShowAll => !_showInstalledOnly;
    public string InstalledTabText => $"Terpasang ({_allGames.Count(g => g.IsInstalled)})";
    public string AllTabText => $"Semua ({_allGames.Count})";
    public bool IsScanningLibrary { get => _isScanningLibrary; private set => Set(ref _isScanningLibrary, value); }
    public bool ShowEmptyInstalledHint => _showInstalledOnly && FilteredGames.Count == 0 && _searchText.Length == 0;

    private async Task ScanInstalledAsync(bool announce)
    {
        IsScanningLibrary = true;
        try
        {
            var manual = _services.Library;
            _installed = await Task.Run(() =>
            {
                var list = InstalledGamesScanner.ScanAll(w => Log.Warn(w));
                foreach (var m in manual.ManualInstalled())
                    if (!list.Any(g => string.Equals(g.InstallDir, m.InstallDir, StringComparison.OrdinalIgnoreCase))) list.Add(m);
                return list;
            });
            Log.Info($"Game terpasang terdeteksi: {_installed.Count}");
            ReloadGames();
            if (announce) StatusMessage = $"{_installed.Count} game terpasang ditemukan (Steam, Epic Games, GOG, manual).";
        }
        catch (Exception ex)
        {
            Log.Error("Pindai game terpasang", ex);
            StatusMessage = "Gagal memindai game terpasang: " + ex.Message;
        }
        finally { IsScanningLibrary = false; }
    }

    private async Task AddManualGameAsync()
    {
        var exe = await _services.PickFileAsync("Pilih file .exe game", "Game (.exe)", ["*.exe"]);
        if (exe is null) return;
        if (!_services.Library.ManualGames.Any(m => string.Equals(m.ExePath, exe, StringComparison.OrdinalIgnoreCase)))
        {
            var name = Path.GetFileName(Path.GetDirectoryName(exe)) is { Length: > 0 } dir ? dir : Path.GetFileNameWithoutExtension(exe);
            _services.Library.ManualGames.Add(new Core.Library.ManualGame { ExePath = exe, Name = name });
            _services.SaveLibrary();
        }
        await ScanInstalledAsync(announce: false);
        ShowInstalledOnly = true;
        SelectedGame = _allGames.FirstOrDefault(g => g.Installed is { } i
            && string.Equals(i.InstallDir, Path.GetDirectoryName(exe), StringComparison.OrdinalIgnoreCase)) ?? SelectedGame;
        StatusMessage = "Game ditambahkan ke pustaka.";
    }

    private void ReloadGames()
    {
        var defs = _services.Store.LoadGames(w => Log.Warn(w));

        // Pasangkan game terpasang dengan database; sisanya jadi entri "tanpa cheat bawaan".
        var pairs = new List<(GameDefinition def, InstalledGame? inst)>();
        var usedDefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var inst in _installed)
        {
            var match = LibraryMatcher.Match(inst, defs);
            if (match is not null && usedDefs.Add(match.Id)) pairs.Add((match, inst));
            else if (match is null) pairs.Add((LibraryMatcher.Synthesize(inst), inst));
        }
        foreach (var d in defs.Where(d => !usedDefs.Contains(d.Id))) pairs.Add((d, null));

        var old = _allGames.ToDictionary(g => g.Definition.Id, StringComparer.OrdinalIgnoreCase);
        var next = new List<GameViewModel>();
        foreach (var (def, inst) in pairs)
        {
            var candidate = new GameViewModel(_services, def, inst, RequestReload);
            if (old.Remove(def.Id, out var existing) && existing.Fingerprint == candidate.Fingerprint)
            {
                candidate.Dispose();
                next.Add(existing); // tidak berubah: pertahankan sesi & cheat yang sedang aktif
                continue;
            }
            existing?.Dispose();
            candidate.ShowPendingMessage();
            next.Add(candidate);
        }
        foreach (var removed in old.Values) removed.Dispose();

        var selectedId = _selectedGame?.Definition.Id;
        _allGames = next;
        ApplyFilter();
        SelectedGame = _allGames.FirstOrDefault(g => g.Definition.Id == selectedId) ?? FilteredGames.FirstOrDefault();
        OnPropertyChanged(nameof(InstalledTabText));
        OnPropertyChanged(nameof(AllTabText));

        var index = _services.Store.LoadIndex();
        int dbGames = defs.Count;
        DatabaseInfo = index is null
            ? "Database kosong."
            : $"Database: {dbGames} game · {defs.Sum(d => d.Cheats.Count)} cheat\nDiperbarui {index.Updated.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.CurrentCulture)}";
    }

    private void RequestReload() => Dispatcher.UIThread.Post(ReloadGames);

    private void ApplyFilter()
    {
        var q = _searchText.Trim();
        var items = _allGames
            .Where(g => !_showInstalledOnly || g.IsInstalled)
            .Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(g => g.IsRunning)
            .ThenByDescending(g => g.HasCheats || g.Tools.Count > 0)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        var selected = _selectedGame;
        FilteredGames.Clear();
        foreach (var g in items) FilteredGames.Add(g);
        if (selected is not null && items.Contains(selected)) SelectedGame = selected;
        else if (selected is not null) SelectedGame = items.FirstOrDefault();
        OnPropertyChanged(nameof(ShowEmptyInstalledHint));
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
                if (!before && running is not null)
                {
                    if (!FilteredGames.Contains(g)) ShowInstalledOnly = false;
                    SelectedGame = g;
                }
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
