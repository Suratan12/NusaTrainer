using System.Globalization;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Trainer;

/// <summary>Menerjemahkan <see cref="AddressSpec"/> menjadi alamat absolut. Hasil AOB scan di-cache.</summary>
public sealed class AddressResolver(IProcessMemory memory)
{
    // Cache hasil scan mentah per (pola, module, region) — beberapa cheat sering berbagi pola yang sama.
    private readonly Dictionary<string, ulong> _scanCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public ulong Resolve(AddressSpec spec, CancellationToken ct = default)
    {
        ulong address = ResolveBase(spec, ct);
        if (spec.Pointers is { Count: > 0 })
        {
            foreach (var off in spec.Pointers)
            {
                ulong ptr = memory.ReadPointer(address);
                if (ptr == 0)
                    throw new MemoryAccessException("Pointer bernilai null — masuk ke dalam game (bukan menu utama) lalu coba lagi.");
                address = ptr + (ulong)ParseHex(off);
            }
        }
        return address;
    }

    private ulong ResolveBase(AddressSpec spec, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(spec.Pattern))
        {
            var key = $"{spec.Pattern}|{spec.Module}|{spec.Region}";
            ulong hit;
            bool cached;
            lock (_lock) cached = _scanCache.TryGetValue(key, out hit);
            if (!cached)
            {
                hit = Scan(spec, ct);
                lock (_lock) _scanCache[key] = hit;
            }

            ulong address = (ulong)((long)hit + spec.PatternOffset);
            if (spec.Rip is { } rip)
            {
                ulong instr = (ulong)((long)hit + rip.InstructionOffset);
                var disp = BitConverter.ToInt32(memory.ReadBytes(instr + (ulong)rip.DisplacementOffset, 4));
                address = (ulong)((long)instr + rip.InstructionLength + disp);
            }
            return address;
        }

        if (!string.IsNullOrWhiteSpace(spec.Module))
        {
            var module = memory.FindModule(spec.Module)
                         ?? throw new MemoryAccessException($"Module '{spec.Module}' tidak ditemukan di proses game.");
            return module.BaseAddress + (ulong)ParseHex(spec.Offset ?? "0");
        }

        if (!string.IsNullOrWhiteSpace(spec.Offset))
            return (ulong)ParseHex(spec.Offset); // alamat absolut (jarang dipakai)

        throw new InvalidOperationException("AddressSpec harus punya 'pattern' atau 'module'.");
    }

    private ulong Scan(AddressSpec spec, CancellationToken ct)
    {
        ModuleInfo? module = null;
        if (!string.IsNullOrWhiteSpace(spec.Module))
        {
            module = memory.FindModule(spec.Module)
                     ?? throw new MemoryAccessException($"Module '{spec.Module}' tidak ditemukan di proses game.");
        }

        var pattern = AobPattern.Parse(spec.Pattern);
        return MemoryScanner.FindFirst(memory, pattern, spec.Region, module, ct)
               ?? throw new MemoryAccessException("Pola AOB tidak ditemukan. Versi game mungkin tidak cocok dengan database, atau data belum dimuat.");
    }

    public void ClearCache()
    {
        lock (_lock) _scanCache.Clear();
    }

    public static long ParseHex(string value)
    {
        var s = value.Trim();
        bool neg = s.StartsWith('-');
        if (neg || s.StartsWith('+')) s = s[1..];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        var v = long.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return neg ? -v : v;
    }
}
