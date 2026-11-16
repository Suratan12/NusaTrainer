using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media;
using Avalonia.Threading;
using NusaTrainer.App.Services;
using NusaTrainer.Core.CheatTables;
using NusaTrainer.Core.Games;
using NusaTrainer.Core.Library;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Safety;
using NusaTrainer.Core.Trainer;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public enum TrainerStatus { NotRunning, Connecting, Connected, Blocked, Error }

public sealed class GameViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly Action _requestReload;
    private readonly List<IDisposable> _hotkeys = new();
    private readonly DispatcherTimer _valueTimer;
    private TrainerSession? _session;
    private TrainerStatus _status = TrainerStatus.NotRunning;
    private string? _statusDetail;
    private string? _gameFolder;
    private string? _toolsMessage;
    private int? _attachedPid;
    private int? _blockedPid;
    private int? _autoLaunchedPid;

    /// <summary>Definisi efektif (database + cheat hasil impor tabel pengguna).</summary>
    public GameDefinition Definition { get; }
    /// <summary>Game terpasang di PC (null bila hanya ada di database).</summary>
    public InstalledGame? Installed { get; }
    /// <summary>Untuk mendeteksi perubahan saat database/pustaka dimuat ulang.</summary>
    public string Fingerprint { get; }

    /// <summary>Kunci penyimpanan trainer/tabel pengguna.</summary>
    public string LibraryKey => Installed?.Key ?? Definition.Id;
    private IEnumerable<string> Keys => new[] { Definition.Id, Installed?.Key }.OfType<string>().Distinct();

    public string Name => Definition.Name;
    public string? Description => Definition.Description;
    public string Initials { get; }
    public IBrush AccentBrush { get; }
    public IBrush HeaderBrush { get; }
    public int CheatCount => Definition.Cheats.Count;
    public bool HasCheats => CheatCount > 0;
    public bool HasNoCheats => CheatCount == 0;
    public bool IsInstalled => Installed is not null;
    public bool HasAntiCheat => Installed?.HasAntiCheat == true || Definition.OnlineMultiplayer;
    public string SourceLabel => Installed is null ? "Belum terpasang" : LibraryMatcher.SourceLabel(Installed.Source);

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            parts.Add(CheatCount > 0 ? $"{CheatCount} cheat" : "Tanpa cheat bawaan");
            if (Tools.Count > 0) parts.Add($"{Tools.Count} alat");
            parts.Add(SourceLabel);
            return string.Join(" · ", parts);
        }
    }

    public string CheatTabHeader => $"Cheat ({CheatCount})";
    public string ModTabHeader => $"Mod ({Definition.Mods.Count})";
    public string ToolsTabHeader => $"Trainer & Tabel ({Tools.Count})";
    public string? SupportedVersion => Definition.SupportedGameVersion is { } v ? "Mendukung versi game: " + v : null;
    public bool HasSupportedVersion => Definition.SupportedGameVersion is not null;
    public string ProcessNamesText => string.Join(", ", Definition.ProcessNames.Take(6).Select(p => ProcessFinder.Normalize(p) + ".exe"));
    public string AuthorText => Installed is not null && Definition.Id == Installed.Key
        ? "Game ini belum ada di database NusaTrainer."
        : $"Definisi oleh {Definition.Author ?? "komunitas"} · revisi {Definition.Revision}";
    public bool HasMods => Definition.Mods.Count > 0;
    public bool HasNoMods => Definition.Mods.Count == 0;
    public bool CanLaunch => Installed is not null || Definition.SteamAppId is not null;

    public List<CheatViewModel> Cheats { get; }
    public List<ModViewModel> Mods { get; }
    public ObservableCollection<ToolViewModel> Tools { get; } = new();
    public List<TrainerLinkViewModel> TrainerLinks { get; }
    public ScannerViewModel Scanner { get; }
    private int _selectedTab;
    public int SelectedTabIndex { get => _selectedTab; set => Set(ref _selectedTab, value); }
    public RelayCommand OpenScannerCommand => new(() => SelectedTabIndex = 1);
    /// <summary>PID game yang sedang berjalan (untuk pemindai), atau null.</summary>
    public int? RunningPid => _attachedPid;
    public string? GameVersionText => Installed?.VersionText is { } v ? "Versi game: " + v : null;
    public bool HasGameVersion => GameVersionText is not null;
    public bool HasTrainerLinks => TrainerLinks.Count > 0;
    public bool HasNoTools => Tools.Count == 0;
    public int ImportedCount { get; }
    public bool HasImported => ImportedCount > 0;

    public RelayCommand LaunchCommand { get; }
    public AsyncCommand PickFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncCommand AddTrainerCommand { get; }
    public AsyncCommand ImportTableCommand { get; }
    public RelayCommand RemoveImportedCommand { get; }

    public GameViewModel(AppServices services, GameDefinition definition, InstalledGame? installed, Action requestReload)
    {
        _services = services;
        _requestReload = requestReload;
        Installed = installed;

        // Gabungkan cheat hasil impor tabel pengguna ke salinan definisi.
        var imported = services.Library.ImportedFor(new[] { definition.Id, installed?.Key }.OfType<string>());
        Definition = Json.Deserialize<GameDefinition>(Json.Serialize(definition));
        var existing = new HashSet<string>(Definition.Cheats.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var ic in imported.Where(x => existing.Add(x.Id)))
        {
            Definition.Cheats.Add(ic);
            ImportedCount++;
        }
        Fingerprint = Json.Serialize(Definition) + "|" + installed?.InstallDir + "|" + installed?.HasAntiCheat;

        var words = Definition.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        Initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        if (Initials.Length == 0) Initials = "?";

        var accent = Color.TryParse(Definition.AccentColor ?? "", out var c) ? c : Color.Parse("#14B8A6");
        AccentBrush = new SolidColorBrush(accent);
        HeaderBrush = new LinearGradientBrush
        {
            StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
            EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(150, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(40, accent.R, accent.G, accent.B), 0.55),
                new GradientStop(Color.FromArgb(0, 0, 0, 0), 1),
            },
        };

        string? lastCategory = null;
        // Kelompokkan per kategori (urutan kemunculan pertama dipertahankan).
        Cheats = Definition.Cheats.GroupBy(x => x.Category).SelectMany(g => g).Select(x =>
        {
            var vm = new CheatViewModel(x) { ShowCategoryHeader = x.Category != lastCategory };
            lastCategory = x.Category;
            return vm;
        }).ToList();
        Mods = Definition.Mods.Select(m => new ModViewModel(services, this, m)).ToList();
        foreach (var t in services.Library.ToolsFor(Keys)) Tools.Add(new ToolViewModel(services, this, t));
        TrainerLinks = Definition.ExternalTrainers.Select(t => new TrainerLinkViewModel(services, this, t)).ToList();
        Scanner = new ScannerViewModel(services, this);

        _gameFolder = services.Settings.GameFolders.TryGetValue(Definition.Id, out var f) && Directory.Exists(f) ? f
            : installed?.InstallDir
              ?? (Definition.SteamAppId is { } appId ? SafeFindSteam(appId) : null);

        LaunchCommand = new RelayCommand(Launch, () => CanLaunch);
        PickFolderCommand = new AsyncCommand(async () => await AskForFolderAsync());
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _gameFolder is not null);
        AddTrainerCommand = new AsyncCommand(AddTrainerAsync);
        ImportTableCommand = new AsyncCommand(ImportTableAsync);
        RemoveImportedCommand = new RelayCommand(RemoveImported, () => HasImported);

        _valueTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _valueTimer.Tick += (_, _) =>
        {
            var numbers = Cheats.Where(x => x.IsNumber).ToList();
            if (numbers.Count > 0) Task.Run(() => { foreach (var n in numbers) n.RefreshCurrentValue(); });
        };
    }

    /// <summary>Alasan alat/trainer tidak boleh dijalankan (game online/anti-cheat), atau null.</summary>
    public string? BlockReason => HasAntiCheat
        ? "Game ini memakai anti-cheat atau mode online. NusaTrainer tidak menjalankan trainer/tabel untuk game seperti ini — risiko ban akun dan merugikan pemain lain."
        : null;

    public bool IsRunning => _status is TrainerStatus.Connected or TrainerStatus.Connecting or TrainerStatus.Blocked
                             || (_status == TrainerStatus.Error && _attachedPid is not null);
    public bool IsConnected => _status == TrainerStatus.Connected;

    public TrainerStatus Status
    {
        get => _status;
        private set
        {
            if (!Set(ref _status, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(StatusBrush));
            foreach (var m in Mods) m.RefreshCommands();
        }
    }

    public string? StatusDetail
    {
        get => _statusDetail;
        private set { if (Set(ref _statusDetail, value)) OnPropertyChanged(nameof(HasStatusDetail)); }
    }
    public bool HasStatusDetail => !string.IsNullOrEmpty(_statusDetail);

    public string? ToolsMessage
    {
        get => _toolsMessage;
        private set { if (Set(ref _toolsMessage, value)) OnPropertyChanged(nameof(HasToolsMessage)); }
    }
    public bool HasToolsMessage => !string.IsNullOrEmpty(_toolsMessage);

    public string StatusText => _status switch
    {
        TrainerStatus.NotRunning => "Game belum berjalan",
        TrainerStatus.Connecting => "Menghubungkan…",
        TrainerStatus.Connected => HasCheats ? "Terhubung — cheat siap" : "Game berjalan",
        TrainerStatus.Blocked => "Diblokir",
        _ => "Gagal terhubung",
    };

    public IBrush StatusBrush => _status switch
    {
        TrainerStatus.Connected => new SolidColorBrush(Color.Parse("#22C55E")),
        TrainerStatus.Connecting => new SolidColorBrush(Color.Parse("#EAB308")),
        TrainerStatus.NotRunning => new SolidColorBrush(Color.Parse("#64748B")),
        _ => new SolidColorBrush(Color.Parse("#EF4444")),
    };

    public string? GameFolder
    {
        get => _gameFolder;
        private set
        {
            if (!Set(ref _gameFolder, value)) return;
            OnPropertyChanged(nameof(GameFolderText));
            OpenFolderCommand.RaiseCanExecuteChanged();
        }
    }
    public string GameFolderText => _gameFolder ?? "Belum diketahui — jalankan game sekali atau pilih folder secara manual.";

    public async Task<string?> AskForFolderAsync()
    {
        var picked = await _services.PickFolderAsync($"Pilih folder instalasi {Definition.Name}");
        if (picked is null) return null;
        _services.Settings.GameFolders[Definition.Id] = picked;
        _services.SaveSettings();
        GameFolder = picked;
        return picked;
    }

    // ---------------- Trainer & tabel ----------------

    private async Task AddTrainerAsync()
    {
        var path = await _services.PickFileAsync($"Pilih trainer untuk {Definition.Name}", "Trainer (.exe)", ["*.exe"]);
        if (path is null) return;
        var tool = new ExternalTool { Kind = ExternalToolKind.Trainer, Name = Path.GetFileNameWithoutExtension(path), Path = path };
        _services.Library.AddTool(LibraryKey, tool);
        _services.SaveLibrary();
        Tools.Add(new ToolViewModel(_services, this, tool));
        ToolsMessage = "Trainer ditambahkan. Aktifkan \"Jalankan otomatis\" agar trainer terbuka setiap kali game dimulai.";
        RaiseToolsChanged();
    }

    private async Task ImportTableAsync()
    {
        var path = await _services.PickFileAsync($"Impor tabel Cheat Engine untuk {Definition.Name}", "Tabel Cheat Engine (.CT)", ["*.CT", "*.ct"]);
        if (path is null) return;
        try
        {
            var result = await Task.Run(() => CheatTableImporter.Import(File.ReadAllText(path)));

            // Simpan salinan tabel agar tetap ada walau file aslinya dipindah.
            Directory.CreateDirectory(UserLibrary.TablesDir);
            var copy = Path.Combine(UserLibrary.TablesDir, $"{LibraryKey}-{Path.GetFileName(path)}");
            File.Copy(path, copy, true);

            var parts = new List<string>();
            if (result.Cheats.Count > 0)
            {
                _services.Library.AddImported(LibraryKey, result.Cheats);
                parts.Add($"{result.Cheats.Count} cheat diimpor ke tab Cheat");
            }
            if (result.ScriptEntries > 0 || result.Cheats.Count == 0)
            {
                if (!_services.Library.ToolsFor(Keys).Any(t => string.Equals(t.Path, copy, StringComparison.OrdinalIgnoreCase)))
                    _services.Library.AddTool(LibraryKey, new ExternalTool
                    {
                        Kind = ExternalToolKind.CheatTable, Name = Path.GetFileNameWithoutExtension(path), Path = copy,
                    });
                parts.Add(result.ScriptEntries > 0
                    ? $"{result.ScriptEntries} skrip ditambahkan sebagai tabel yang dibuka lewat Cheat Engine"
                    : "tabel ditambahkan untuk dibuka lewat Cheat Engine");
            }
            if (result.UnsupportedEntries > 0) parts.Add($"{result.UnsupportedEntries} entri lain dilewati");
            _services.SaveLibrary();
            Log.Info($"Impor tabel {path}: {string.Join("; ", parts)}");
            _pendingMessage = string.Join("; ", parts) + ".";
            _requestReload(); // bangun ulang tampilan dengan cheat baru
        }
        catch (Exception ex)
        {
            ToolsMessage = "Gagal mengimpor: " + ex.Message;
        }
    }

    // Pesan yang dibawa ke GameViewModel baru setelah reload.
    private static string? _pendingMessage;
    public void ShowPendingMessage()
    {
        if (_pendingMessage is null) return;
        ToolsMessage = _pendingMessage;
        _pendingMessage = null;
    }

    /// <summary>Muat ulang tampilan game (mis. setelah cheat baru ditambahkan) dan tampilkan pesan.</summary>
    public void ReloadWithMessage(string message)
    {
        _pendingMessage = message;
        _requestReload();
    }

    /// <summary>Daftarkan trainer hasil unduhan katalog sebagai alat game.</summary>
    public void AddInstalledTrainer(string exePath, string name)
    {
        if (!Tools.Any(t => string.Equals(t.Path, exePath, StringComparison.OrdinalIgnoreCase)))
        {
            var tool = new ExternalTool { Kind = ExternalToolKind.Trainer, Name = name, Path = exePath };
            _services.Library.AddTool(LibraryKey, tool);
            _services.SaveLibrary();
            Tools.Add(new ToolViewModel(_services, this, tool));
            RaiseToolsChanged();
        }
        ToolsMessage = $"Trainer \"{name}\" siap. Aktifkan \"Jalankan otomatis\" agar terbuka setiap kali game dimulai.";
    }

    private void RemoveImported()
    {
        _services.Library.ClearImported(Keys);
        _services.SaveLibrary();
        _pendingMessage = "Cheat hasil impor dihapus.";
        _requestReload();
    }

    public void RemoveTool(ToolViewModel tool)
    {
        _services.Library.RemoveTool(tool.Tool.Id);
        _services.SaveLibrary();
        Tools.Remove(tool);
        if (tool.Tool.Kind == ExternalToolKind.CheatTable && tool.Tool.Path.StartsWith(UserLibrary.TablesDir, StringComparison.OrdinalIgnoreCase))
        {
            try { File.Delete(tool.Tool.Path); } catch { }
        }
        RaiseToolsChanged();
    }

    private void RaiseToolsChanged()
    {
        OnPropertyChanged(nameof(ToolsTabHeader));
        OnPropertyChanged(nameof(HasNoTools));
        OnPropertyChanged(nameof(Subtitle));
    }

    // ---------------- Proses game ----------------

    /// <summary>Dipanggil watcher proses (thread UI) dengan hasil pencarian proses.</summary>
    public void UpdateProcess(RunningGame? running)
    {
        if (running is null)
        {
            if (_attachedPid is not null || _status != TrainerStatus.NotRunning) Detach(TrainerStatus.NotRunning, null);
            _blockedPid = null;
            return;
        }

        if (GameFolder is null && ProcessFinder.GuessGameRoot(Definition, running.ExePath) is { } root && Directory.Exists(root))
            GameFolder = root;

        if (_autoLaunchedPid != running.ProcessId)
        {
            _autoLaunchedPid = running.ProcessId;
            foreach (var t in Tools.Where(t => t.AutoLaunch)) t.Launch(manual: false);
        }

        if (_attachedPid == running.ProcessId || _blockedPid == running.ProcessId) return;
        if (_session is not null) Detach(TrainerStatus.NotRunning, null);
        _ = AttachAsync(running.ProcessId);
    }

    private async Task AttachAsync(int pid)
    {
        _attachedPid = pid;
        OnPropertyChanged(nameof(RunningPid));
        Status = TrainerStatus.Connecting;
        StatusDetail = null;

        if (AntiCheatGuard.CheckDefinition(Definition) is { } defReason)
        {
            _blockedPid = pid;
            Status = TrainerStatus.Blocked;
            StatusDetail = defReason;
            return;
        }

        // Tanpa cheat bawaan, cukup tandai berjalan (trainer eksternal tetap bisa dipakai).
        if (!HasCheats)
        {
            Status = TrainerStatus.Connected;
            StatusDetail = Tools.Count > 0
                ? $"PID {pid}. Gunakan trainer/tabel Anda di tab \"Trainer & Tabel\"."
                : $"PID {pid}. Belum ada cheat — pakai tab \"Pemindai Nilai\" untuk mencari uang/nyawa, atau tambahkan trainer.";
            return;
        }

        try
        {
            // Beri game waktu memuat modul sebelum dicek anti-cheat.
            await Task.Delay(1500);
            var session = await Task.Run(() =>
            {
                var mem = ProcessMemoryFactory.Open(pid);
                try
                {
                    if (AntiCheatGuard.CheckProcess(mem) is { } reason)
                        throw new BlockedException(reason);
                    return new TrainerSession(Definition, mem);
                }
                catch
                {
                    mem.Dispose();
                    throw;
                }
            });

            if (_attachedPid != pid) { session.Dispose(); return; } // proses sudah berganti
            _session = session;
            session.CheatFailed += (id, msg) => OnUi(() => Cheats.FirstOrDefault(x => x.Definition.Id == id)?.OnFailed(msg));
            foreach (var x in Cheats) x.Attach(session);
            RegisterHotkeys();
            _valueTimer.Start();
            Status = TrainerStatus.Connected;
            StatusDetail = $"PID {pid}. Aktifkan cheat di bawah atau pakai hotkey saat bermain.";
            Log.Info($"Terhubung ke {Definition.Name} (PID {pid})");
        }
        catch (BlockedException ex)
        {
            _blockedPid = pid;
            Status = TrainerStatus.Blocked;
            StatusDetail = ex.Message;
            Log.Warn($"{Definition.Name}: {ex.Message}");
        }
        catch (Exception ex)
        {
            _blockedPid = pid; // jangan coba terus-menerus; coba lagi bila game dibuka ulang
            Status = TrainerStatus.Error;
            StatusDetail = ex.Message;
            Log.Error($"Gagal attach ke {Definition.Name}", ex);
        }
    }

    private void RegisterHotkeys()
    {
        foreach (var x in Cheats)
        {
            var d = x.Definition;
            void Reg(string? combo, Action action)
            {
                if (combo is null) return;
                if (_services.Hotkeys.Register(combo, () => OnUi(action)) is { } h) _hotkeys.Add(h);
            }
            Reg(d.Hotkey, x.ToggleFromHotkey);
            Reg(d.HotkeyIncrease, () => x.StepFromHotkey(+1));
            Reg(d.HotkeyDecrease, () => x.StepFromHotkey(-1));
        }
    }

    private void Detach(TrainerStatus newStatus, string? detail)
    {
        _valueTimer.Stop();
        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        foreach (var x in Cheats) x.Attach(null);
        var s = _session;
        _session = null;
        _attachedPid = null;
        if (s is not null) Task.Run(s.Dispose);
        Status = newStatus;
        StatusDetail = detail;
        OnPropertyChanged(nameof(RunningPid));
    }

    private void Launch()
    {
        try
        {
            if (Installed?.Source == GameSource.Epic)
                Start($"com.epicgames.launcher://apps/{Uri.EscapeDataString(Installed.SourceId)}?action=launch&silent=true");
            else if (Definition.SteamAppId is { } id && (Installed is null || Installed.Source == GameSource.Steam))
                Start($"steam://rungameid/{id}");
            else if (Installed is not null && FindExe(Installed) is { } exe)
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! });
            else
                StatusDetail = "File exe game tidak ditemukan.";
        }
        catch (Exception ex) { StatusDetail = "Gagal menjalankan game: " + ex.Message; }

        static void Start(string uri) => Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
    }

    private static string? FindExe(InstalledGame g)
    {
        if (g.Executables.Count == 0) return null;
        var name = g.Executables[0];
        var direct = Path.Combine(g.InstallDir, name);
        if (File.Exists(direct)) return direct;
        try
        {
            return Directory.EnumerateFiles(g.InstallDir, name, new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3 })
                .FirstOrDefault();
        }
        catch { return null; }
    }

    private void OpenFolder()
    {
        if (_gameFolder is null) return;
        try { Process.Start(new ProcessStartInfo(_gameFolder) { UseShellExecute = true }); } catch { }
    }

    private static string? SafeFindSteam(int appId)
    {
        try { return SteamLocator.FindGameDir(appId); } catch { return null; }
    }

    public void Dispose()
    {
        Scanner.Dispose();
        Detach(TrainerStatus.NotRunning, null);
    }

    private sealed class BlockedException(string message) : Exception(message);
}
