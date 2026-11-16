using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.RegularExpressions;
using NusaTrainer.App.Services;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Safety;
using NusaTrainer.Core.Scanning;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.ViewModels;

public sealed record ScanKindOption(string Label, ScanValueKind Kind)
{
    public override string ToString() => Label;
}

/// <summary>Pemindai nilai terpandu: cari uang/nyawa/material dari angka yang terlihat di game.</summary>
public sealed class ScannerViewModel : ViewModelBase, IDisposable
{
    private readonly AppServices _services;
    private readonly GameViewModel _game;
    private IProcessMemory? _memory;
    private int? _memoryPid;
    private ValueScanner? _scanner;
    private string _cheatName = "Uang";
    private string _valueText = "";
    private ScanKindOption _kind;
    private bool _isBusy;
    private double _progress;
    private string? _message;
    private bool _hasScanned;
    private CancellationTokenSource? _cts;

    public static IReadOnlyList<ScanKindOption> Kinds { get; } =
    [
        new("Otomatis (bulat + desimal) — disarankan", ScanValueKind.Auto),
        new("Bilangan bulat 4 byte", ScanValueKind.Int32),
        new("Desimal (float)", ScanValueKind.Float),
        new("Desimal presisi (double)", ScanValueKind.Double),
        new("Bilangan bulat 8 byte", ScanValueKind.Int64),
    ];
    public IReadOnlyList<ScanKindOption> KindOptions => Kinds;

    public ObservableCollection<ScanResultViewModel> Results { get; } = new();

    public AsyncCommand FirstScanCommand { get; }
    public AsyncCommand NextExactCommand { get; }
    public AsyncCommand IncreasedCommand { get; }
    public AsyncCommand DecreasedCommand { get; }
    public AsyncCommand ChangedCommand { get; }
    public AsyncCommand UnchangedCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public RelayCommand ResetCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand<string> PresetCommand { get; }

    public ScannerViewModel(AppServices services, GameViewModel game)
    {
        _services = services;
        _game = game;
        _kind = Kinds[0];
        FirstScanCommand = new AsyncCommand(FirstScanAsync, () => !_isBusy);
        NextExactCommand = new AsyncCommand(() => NextAsync(NextScanMode.Exact), CanNext);
        IncreasedCommand = new AsyncCommand(() => NextAsync(NextScanMode.Increased), CanNext);
        DecreasedCommand = new AsyncCommand(() => NextAsync(NextScanMode.Decreased), CanNext);
        ChangedCommand = new AsyncCommand(() => NextAsync(NextScanMode.Changed), CanNext);
        UnchangedCommand = new AsyncCommand(() => NextAsync(NextScanMode.Unchanged), CanNext);
        RefreshCommand = new AsyncCommand(RefreshResultsAsync, () => _hasScanned && !_isBusy);
        ResetCommand = new RelayCommand(Reset);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
        PresetCommand = new RelayCommand<string>(name => CheatName = name ?? "Uang");
    }

    private bool CanNext() => _hasScanned && !_isBusy;

    public string CheatName { get => _cheatName; set => Set(ref _cheatName, value); }
    public string ValueText { get => _valueText; set => Set(ref _valueText, value); }
    public ScanKindOption SelectedKind { get => _kind; set => Set(ref _kind, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            foreach (var c in new[] { FirstScanCommand, NextExactCommand, IncreasedCommand, DecreasedCommand, ChangedCommand, UnchangedCommand, RefreshCommand })
                c.RaiseCanExecuteChanged();
        }
    }

    public bool HasScanned
    {
        get => _hasScanned;
        private set
        {
            if (!Set(ref _hasScanned, value)) return;
            OnPropertyChanged(nameof(IsFirstStep));
            IsBusy = _isBusy; // segarkan CanExecute
            foreach (var c in new[] { NextExactCommand, IncreasedCommand, DecreasedCommand, ChangedCommand, UnchangedCommand, RefreshCommand })
                c.RaiseCanExecuteChanged();
        }
    }
    public bool IsFirstStep => !_hasScanned;

    public string? Message
    {
        get => _message;
        private set { if (Set(ref _message, value)) OnPropertyChanged(nameof(HasMessage)); }
    }
    public bool HasMessage => !string.IsNullOrEmpty(_message);
    public bool HasResults => Results.Count > 0;

    private bool TryParseValue(out double value) =>
        double.TryParse(_valueText.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, out value)
        || double.TryParse(_valueText.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private IProcessMemory? EnsureMemory()
    {
        if (_game.BlockReason is { } reason) { Message = reason; return null; }
        var pid = _game.RunningPid;
        if (pid is null) { Message = "Jalankan game dulu, lalu masuk ke dalam permainan (bukan menu utama)."; return null; }
        if (_memory is not null && _memoryPid == pid && !_memory.HasExited) return _memory;

        _memory?.Dispose();
        _memory = null;
        _scanner = null;
        HasScanned = false;
        try
        {
            var mem = ProcessMemoryFactory.Open(pid.Value);
            if (AntiCheatGuard.CheckProcess(mem) is { } ac) { mem.Dispose(); Message = ac; return null; }
            _memory = mem;
            _memoryPid = pid;
            return mem;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            return null;
        }
    }

    private async Task FirstScanAsync()
    {
        if (!TryParseValue(out var value)) { Message = "Ketik angka yang terlihat di game (mis. jumlah uang)."; return; }
        var mem = EnsureMemory();
        if (mem is null) return;
        _scanner = new ValueScanner(mem);
        await RunAsync(async ct =>
        {
            var p = new Progress<double>(x => Progress = x * 100);
            int n = await Task.Run(() => _scanner.FirstScan(value, _kind.Kind, p, ct), ct);
            HasScanned = true;
            Message = n == 0
                ? "Tidak ada yang cocok. Periksa angkanya, atau coba tipe nilai lain."
                : $"{n:N0} hasil{(_scanner.Truncated ? " (dibatasi)" : "")}. Sekarang ubah nilainya di game (beli/jual, terkena damage, pakai material), ketik nilai baru, lalu klik \"Pindai lagi\".";
        });
    }

    private async Task NextAsync(NextScanMode mode)
    {
        if (_scanner is null || _memory is null || _memory.HasExited) { Reset(); Message = "Game ditutup — mulai pindai dari awal."; return; }
        double value = 0;
        if (mode == NextScanMode.Exact && !TryParseValue(out value)) { Message = "Ketik nilai yang sekarang terlihat di game."; return; }
        await RunAsync(async ct =>
        {
            var p = new Progress<double>(x => Progress = x * 100);
            int n = await Task.Run(() => _scanner.NextScan(mode, value, p, ct), ct);
            Message = n switch
            {
                0 => "Tidak ada yang tersisa. Mungkin nilainya disimpan berbeda — klik \"Mulai ulang\" dan coba tipe nilai lain.",
                <= 5 => $"{n} hasil — tinggal sedikit! Klik \"Jadikan cheat\" pada hasil yang nilainya sama dengan di game.",
                _ => $"{n:N0} hasil. Ubah nilainya lagi di game lalu pindai lagi untuk mempersempit.",
            };
        });
    }

    private async Task RunAsync(Func<CancellationToken, Task> work)
    {
        _cts = new CancellationTokenSource();
        IsBusy = true;
        Progress = 0;
        try
        {
            await work(_cts.Token);
            await RefreshResultsAsync();
        }
        catch (OperationCanceledException) { Message = "Dibatalkan."; }
        catch (Exception ex)
        {
            Message = "Gagal: " + ex.Message;
            Log.Error("Pemindai", ex);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private async Task RefreshResultsAsync()
    {
        if (_scanner is null) return;
        var scanner = _scanner;
        var hits = await Task.Run(() => scanner.Count <= 200 ? scanner.GetResults(50) : scanner.GetResults(20));
        Results.Clear();
        foreach (var h in hits) Results.Add(new ScanResultViewModel(this, h));
        OnPropertyChanged(nameof(HasResults));
    }

    private void Reset()
    {
        _scanner?.Reset();
        _scanner = null;
        Results.Clear();
        OnPropertyChanged(nameof(HasResults));
        HasScanned = false;
        Message = null;
    }

    /// <summary>Jadikan hasil pemindaian cheat "angka" — coba cari pointer permanen dulu.</summary>
    public async Task AddAsCheatAsync(ScanResultViewModel result)
    {
        var mem = _memory;
        if (mem is null || mem.HasExited) { Message = "Game sudah ditutup."; return; }
        var name = string.IsNullOrWhiteSpace(_cheatName) ? "Nilai" : _cheatName.Trim();
        await RunAsync(async ct =>
        {
            Message = "Mencari pointer permanen agar cheat tetap berfungsi setelah game dibuka ulang…";
            var p = new Progress<double>(x => Progress = x * 100);
            var paths = await Task.Run(() => PointerFinder.Find(mem, result.Hit.Address, maxLevel: 3, progress: p, ct: ct), ct);

            AddressSpec spec;
            string description;
            if (paths.FirstOrDefault() is { } best)
            {
                spec = best.ToSpec();
                description = $"Dari pemindai · pointer permanen {best}";
            }
            else
            {
                spec = new AddressSpec { Offset = $"0x{result.Hit.Address:X}" };
                description = "Dari pemindai · alamat sementara — pindai ulang setelah game dibuka ulang.";
            }

            var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            var cheat = new CheatDefinition
            {
                Id = "scan-" + (slug.Length > 0 ? slug : "nilai"),
                Name = name,
                Category = "Hasil Pemindai",
                Type = CheatKind.Number,
                ValueType = result.Hit.Type,
                Address = spec,
                Description = description,
            };
            _services.Library.AddImported(_game.LibraryKey, [cheat]);
            _services.SaveLibrary();
            Log.Info($"Cheat dari pemindai: {name} → {description}");
            _game.ReloadWithMessage(paths.Count > 0
                ? $"Cheat \"{name}\" ditambahkan ke tab Cheat (pointer permanen ditemukan)."
                : $"Cheat \"{name}\" ditambahkan ke tab Cheat. Pointer permanen tidak ditemukan, jadi alamat ini hanya berlaku sampai game ditutup.");
        });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _memory?.Dispose();
        _memory = null;
    }
}

public sealed class ScanResultViewModel
{
    private readonly ScannerViewModel _owner;
    public ScanHit Hit { get; }
    public string AddressText => $"0x{Hit.Address:X}";
    public string TypeText => Hit.Type switch
    {
        DataType.Int32 => "bulat 4B",
        DataType.Int64 => "bulat 8B",
        DataType.Float => "float",
        DataType.Double => "double",
        _ => Hit.Type.ToString(),
    };
    public string ValueText => double.IsNaN(Hit.Value) ? "?" :
        Hit.Type is DataType.Float or DataType.Double ? Hit.Value.ToString("0.###", CultureInfo.CurrentCulture) : Hit.Value.ToString("N0", CultureInfo.CurrentCulture);
    public AsyncCommand AddCommand { get; }

    public ScanResultViewModel(ScannerViewModel owner, ScanHit hit)
    {
        _owner = owner;
        Hit = hit;
        AddCommand = new AsyncCommand(() => _owner.AddAsCheatAsync(this));
    }
}

public sealed class RelayCommand<T>(Action<T?> execute) : System.Windows.Input.ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter is T t ? t : default);
}
