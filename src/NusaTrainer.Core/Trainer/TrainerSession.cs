using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Trainer;

/// <summary>
/// Sesi trainer yang terhubung ke satu proses game. Mengelola status tiap cheat,
/// menulis nilai yang dikunci (freeze) secara periodik, dan mengembalikan patch saat dilepas.
/// </summary>
public sealed class TrainerSession : IDisposable
{
    private sealed class State
    {
        public bool Enabled;
        public double SliderValue;
        public byte[]? OriginalBytes;   // untuk patch / restoreOnDisable
        public ulong PatchedAddress;
    }

    private readonly IProcessMemory _memory;
    private readonly AddressResolver _resolver;
    private readonly Dictionary<string, CheatDefinition> _cheats;
    private readonly Dictionary<string, State> _states = new();
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _disposed;

    public GameDefinition Game { get; }
    public IProcessMemory Memory => _memory;

    /// <summary>Dipanggil saat cheat yang dikunci gagal ditulis (cheatId, pesan). Cheat otomatis dimatikan.</summary>
    public event Action<string, string>? CheatFailed;

    public TrainerSession(GameDefinition game, IProcessMemory memory, TimeSpan? tickInterval = null)
    {
        Game = game;
        _memory = memory;
        _resolver = new AddressResolver(memory);
        _cheats = game.Cheats.ToDictionary(c => c.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var c in game.Cheats)
            _states[c.Id] = new State { SliderValue = c.Default };
        var interval = tickInterval ?? TimeSpan.FromMilliseconds(100);
        _timer = new Timer(_ => SafeTick(), null, interval, interval);
    }

    public bool IsEnabled(string cheatId) { lock (_lock) return Get(cheatId).state.Enabled; }
    public double GetSliderValue(string cheatId) { lock (_lock) return Get(cheatId).state.SliderValue; }

    /// <summary>Membaca nilai saat ini dari memori game (untuk ditampilkan).</summary>
    public double ReadValue(string cheatId)
    {
        var (cheat, _) = Get(cheatId);
        if (cheat.Address is null) throw new InvalidOperationException("Cheat ini tidak punya alamat nilai.");
        var addr = _resolver.Resolve(cheat.Address);
        return ValueCodec.Decode(cheat.ValueType, _memory.ReadBytes(addr, ValueCodec.SizeOf(cheat.ValueType)));
    }

    public void SetToggle(string cheatId, bool enabled)
    {
        lock (_lock)
        {
            var (cheat, state) = Get(cheatId);
            if (state.Enabled == enabled) return;

            if (cheat.Patch is not null)
            {
                if (enabled) ApplyPatch(cheat, state); else RevertPatch(state);
                state.Enabled = enabled;
                return;
            }

            if (cheat.Type is CheatKind.Slider or CheatKind.Number)
            {
                // Toggle pada slider/angka = kunci nilai tersebut.
                if (enabled) WriteValue(cheat, state.SliderValue);
                state.Enabled = enabled;
                return;
            }

            var addressSpec = RequireAddress(cheat);
            if (enabled)
            {
                ulong addr = _resolver.Resolve(addressSpec);
                if (cheat.RestoreOnDisable)
                {
                    state.OriginalBytes = _memory.ReadBytes(addr, ValueCodec.SizeOf(cheat.ValueType));
                    state.PatchedAddress = addr;
                }
                ApplyOperation(cheat, cheat.Value);
            }
            else if (state.OriginalBytes is not null)
            {
                _memory.TryWrite(state.PatchedAddress, state.OriginalBytes);
                state.OriginalBytes = null;
            }
            state.Enabled = enabled;
        }
    }

    public void SetSliderValue(string cheatId, double value)
    {
        lock (_lock)
        {
            var (cheat, state) = Get(cheatId);
            if (cheat.Type == CheatKind.Slider) value = Math.Clamp(value, cheat.Min, cheat.Max);
            state.SliderValue = value;
            if (cheat.Type == CheatKind.Number || cheat.Operation != CheatOperation.Freeze || state.Enabled)
                WriteValue(cheat, value);
        }
    }

    /// <summary>Nilai angka (Number) / slider yang tersimpan.</summary>
    public void SetNumber(string cheatId, double value) => SetSliderValue(cheatId, value);

    /// <summary>Untuk cheat Button (mis. "Tambah 1000 emas").</summary>
    public void Press(string cheatId)
    {
        lock (_lock)
        {
            var (cheat, _) = Get(cheatId);
            ApplyOperation(cheat, cheat.Value);
        }
    }

    public void Tick()
    {
        List<(string id, string message)>? failures = null;
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var (id, state) in _states)
            {
                if (!state.Enabled) continue;
                var cheat = _cheats[id];
                if (cheat.Patch is not null) continue;
                bool freeze = cheat.Type == CheatKind.Number || cheat.Operation == CheatOperation.Freeze;
                if (!freeze) continue;
                try
                {
                    WriteValue(cheat, cheat.Type is CheatKind.Slider or CheatKind.Number ? state.SliderValue : cheat.Value);
                }
                catch (Exception ex)
                {
                    state.Enabled = false;
                    (failures ??= new()).Add((id, ex.Message));
                }
            }
        }
        if (failures is not null)
            foreach (var (id, msg) in failures) CheatFailed?.Invoke(id, msg);
    }

    private int _ticking;

    private void SafeTick()
    {
        // Jangan biarkan callback timer menumpuk bila satu tick lambat (mis. AOB scan pertama).
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try
        {
            if (_memory.HasExited) return;
            Tick();
        }
        catch { /* sesi akan dibersihkan oleh pemanggil saat proses keluar */ }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    private void ApplyOperation(CheatDefinition cheat, double value)
    {
        if (cheat.Operation == CheatOperation.Add)
        {
            var addr = _resolver.Resolve(RequireAddress(cheat));
            var size = ValueCodec.SizeOf(cheat.ValueType);
            double current = ValueCodec.Decode(cheat.ValueType, _memory.ReadBytes(addr, size));
            _memory.WriteBytes(addr, ValueCodec.Encode(cheat.ValueType, current + value));
        }
        else
        {
            WriteValue(cheat, value);
        }
    }

    private void WriteValue(CheatDefinition cheat, double value)
    {
        var addr = _resolver.Resolve(RequireAddress(cheat));
        _memory.WriteBytes(addr, ValueCodec.Encode(cheat.ValueType, value));
    }

    private void ApplyPatch(CheatDefinition cheat, State state)
    {
        var patch = cheat.Patch!;
        var bytes = AobPattern.ParseBytes(patch.Bytes);
        ulong addr = _resolver.Resolve(patch.Address);
        var original = _memory.ReadBytes(addr, bytes.Length);
        if (!string.IsNullOrWhiteSpace(patch.ExpectedOriginal))
        {
            var expected = AobPattern.Parse(patch.ExpectedOriginal);
            if (expected.Length != bytes.Length || !expected.MatchesAt(original, 0))
                throw new MemoryAccessException("Byte asli tidak sesuai — versi game kemungkinan berbeda. Patch dibatalkan demi keamanan.");
        }
        _memory.WriteBytes(addr, bytes);
        state.OriginalBytes = original;
        state.PatchedAddress = addr;
    }

    private void RevertPatch(State state)
    {
        if (state.OriginalBytes is null) return;
        _memory.TryWrite(state.PatchedAddress, state.OriginalBytes);
        state.OriginalBytes = null;
    }

    private static AddressSpec RequireAddress(CheatDefinition cheat) =>
        cheat.Address ?? throw new InvalidOperationException($"Cheat '{cheat.Id}' tidak punya 'address'.");

    private (CheatDefinition cheat, State state) Get(string id)
    {
        if (!_cheats.TryGetValue(id, out var cheat))
            throw new KeyNotFoundException($"Cheat '{id}' tidak ada.");
        return (cheat, _states[cheat.Id]);
    }

    /// <summary>Matikan semua cheat dan kembalikan semua patch kode.</summary>
    public void DisableAll()
    {
        lock (_lock)
        {
            foreach (var (id, state) in _states)
            {
                if (!state.Enabled) continue;
                try
                {
                    if (state.OriginalBytes is not null && !_memory.HasExited)
                        _memory.TryWrite(state.PatchedAddress, state.OriginalBytes);
                }
                catch { /* abaikan */ }
                state.OriginalBytes = null;
                state.Enabled = false;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _timer.Dispose();
        DisableAll();
        lock (_lock) _disposed = true;
        _memory.Dispose();
    }
}
