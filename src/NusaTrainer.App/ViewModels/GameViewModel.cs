using System.Diagnostics;
using Avalonia.Media;
using NusaTrainer.App.Services;
using NusaTrainer.Core.Games;
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
    private readonly List<IDisposable> _hotkeys = new();
    private TrainerSession? _session;
    private TrainerStatus _status = TrainerStatus.NotRunning;
    private string? _statusDetail;
    private string? _gameFolder;
    private int? _attachedPid;
    private int? _blockedPid;

    public GameDefinition Definition { get; }
    /// <summary>JSON definisi — untuk mendeteksi perubahan saat database diperbarui.</summary>
    public string Fingerprint { get; }

    public string Name => Definition.Name;
    public string? Description => Definition.Description;
    public string Initials { get; }
    public IBrush AccentBrush { get; }
    public IBrush HeaderBrush { get; }
    public string Subtitle => $"{Definition.Cheats.Count} cheat" + (Definition.Mods.Count > 0 ? $" · {Definition.Mods.Count} mod" : "");
    public string CheatTabHeader => $"Cheat ({Definition.Cheats.Count})";
    public string ModTabHeader => $"Mod ({Definition.Mods.Count})";
    public string? SupportedVersion => Definition.SupportedGameVersion is { } v ? "Mendukung versi game: " + v : null;
    public bool HasSupportedVersion => Definition.SupportedGameVersion is not null;
    public string ProcessNamesText => string.Join(", ", Definition.ProcessNames.Select(p => ProcessFinder.Normalize(p) + ".exe"));
    public string AuthorText => $"Definisi oleh {Definition.Author ?? "komunitas"} · revisi {Definition.Revision}";
    public bool HasMods => Definition.Mods.Count > 0;
    public bool HasNoMods => Definition.Mods.Count == 0;
    public bool CanLaunch => Definition.SteamAppId is not null;

    public List<CheatViewModel> Cheats { get; }
    public List<ModViewModel> Mods { get; }

    public RelayCommand LaunchCommand { get; }
    public AsyncCommand PickFolderCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public GameViewModel(AppServices services, GameDefinition definition)
    {
        _services = services;
        Definition = definition;
        Fingerprint = Json.Serialize(definition);

        var words = definition.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => char.IsLetterOrDigit(w[0])).ToList();
        Initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));

        var accent = Color.TryParse(definition.AccentColor ?? "", out var c) ? c : Color.Parse("#14B8A6");
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
        Cheats = definition.Cheats.GroupBy(c => c.Category).SelectMany(g => g).Select(c =>
        {
            var vm = new CheatViewModel(c) { ShowCategoryHeader = c.Category != lastCategory };
            lastCategory = c.Category;
            return vm;
        }).ToList();
        Mods = definition.Mods.Select(m => new ModViewModel(services, this, m)).ToList();

        _gameFolder = services.Settings.GameFolders.TryGetValue(definition.Id, out var f) && Directory.Exists(f) ? f
            : definition.SteamAppId is { } appId ? SafeFindSteam(appId) : null;

        LaunchCommand = new RelayCommand(Launch, () => CanLaunch);
        PickFolderCommand = new AsyncCommand(async () => await AskForFolderAsync());
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _gameFolder is not null);
    }

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

    public string StatusText => _status switch
    {
        TrainerStatus.NotRunning => "Game belum berjalan",
        TrainerStatus.Connecting => "Menghubungkan…",
        TrainerStatus.Connected => "Terhubung — cheat siap",
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

        if (_attachedPid == running.ProcessId || _blockedPid == running.ProcessId) return;
        if (_session is not null) Detach(TrainerStatus.NotRunning, null);
        _ = AttachAsync(running.ProcessId);
    }

    private async Task AttachAsync(int pid)
    {
        _attachedPid = pid;
        Status = TrainerStatus.Connecting;
        StatusDetail = null;

        if (AntiCheatGuard.CheckDefinition(Definition) is { } defReason)
        {
            _blockedPid = pid;
            Status = TrainerStatus.Blocked;
            StatusDetail = defReason;
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
            session.CheatFailed += (id, msg) => OnUi(() => Cheats.FirstOrDefault(c => c.Definition.Id == id)?.OnFailed(msg));
            foreach (var c in Cheats) c.Attach(session);
            RegisterHotkeys();
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
        foreach (var c in Cheats)
        {
            var d = c.Definition;
            void Reg(string? combo, Action action)
            {
                if (combo is null) return;
                if (_services.Hotkeys.Register(combo, () => OnUi(action)) is { } h) _hotkeys.Add(h);
            }
            Reg(d.Hotkey, c.ToggleFromHotkey);
            Reg(d.HotkeyIncrease, () => c.StepFromHotkey(+1));
            Reg(d.HotkeyDecrease, () => c.StepFromHotkey(-1));
        }
    }

    private void Detach(TrainerStatus newStatus, string? detail)
    {
        foreach (var h in _hotkeys) h.Dispose();
        _hotkeys.Clear();
        foreach (var c in Cheats) c.Attach(null);
        var s = _session;
        _session = null;
        _attachedPid = null;
        if (s is not null) Task.Run(s.Dispose);
        Status = newStatus;
        StatusDetail = detail;
    }

    private void Launch()
    {
        if (Definition.SteamAppId is not { } id) return;
        try { Process.Start(new ProcessStartInfo($"steam://rungameid/{id}") { UseShellExecute = true }); }
        catch (Exception ex) { StatusDetail = "Gagal membuka Steam: " + ex.Message; }
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

    public void Dispose() => Detach(TrainerStatus.NotRunning, null);

    private sealed class BlockedException(string message) : Exception(message);
}
