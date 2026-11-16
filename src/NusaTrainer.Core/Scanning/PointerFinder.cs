using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Scanning;

/// <summary>
/// Mencari rantai pointer statis (module+offset → [ptr]+off …) menuju sebuah alamat heap,
/// sehingga cheat hasil pemindaian tetap berfungsi setelah game dibuka ulang.
/// Versi ringan dari "Pointer scan" Cheat Engine: maksimal 3 level, offset struktur ≤ maxOffset.
/// </summary>
public static class PointerFinder
{
    public sealed record PointerPath(string Module, ulong ModuleOffset, IReadOnlyList<ulong> Offsets)
    {
        public AddressSpec ToSpec() => new()
        {
            Module = Module,
            Offset = $"0x{ModuleOffset:X}",
            Pointers = Offsets.Count > 0 ? Offsets.Select(o => $"0x{o:X}").ToList() : null,
        };

        public override string ToString() =>
            $"\"{Module}\"+{ModuleOffset:X}" + string.Concat(Offsets.Select(o => $" → +{o:X}"));
    }

    private const int Chunk = 4 * 1024 * 1024;

    public static List<PointerPath> Find(IProcessMemory memory, ulong target, int maxLevel = 3, uint maxOffset = 0x1000,
        int maxResults = 20, IProgress<double>? progress = null, CancellationToken ct = default, int maxTargetsPerLevel = 3000)
    {
        int ptrSize = memory.Is64Bit ? 8 : 4;
        var modules = memory.GetModules();
        var regions = memory.GetRegions().Where(r => r.Readable && r.Writable && !r.Executable).ToList();

        // Region statis = bagian data milik module (exe/dll), alamatnya tetap relatif terhadap base module.
        ModuleInfo? ModuleOf(ulong a) => modules.Cast<ModuleInfo?>().FirstOrDefault(m => a >= m!.Value.BaseAddress && a < m.Value.End);
        bool IsSystemModule(ModuleInfo m) =>
            m.Path is { } p && (p.Contains(@"\Windows\", StringComparison.OrdinalIgnoreCase) || p.StartsWith("/usr/", StringComparison.Ordinal)
                                || p.StartsWith("/lib", StringComparison.Ordinal));

        // Pindai seluruh memori data sekali: kumpulkan (lokasi, nilai pointer) yang menunjuk dekat target mana pun di level berikutnya.
        var results = new List<PointerPath>();
        // targets: alamat yang ingin dicapai di level ini → jalur offset dari alamat itu ke target akhir
        // Nilai statis langsung di dalam module (tanpa pointer).
        if (ModuleOf(target) is { } tm && !IsSystemModule(tm))
            results.Add(new PointerPath(tm.Name, target - tm.BaseAddress, []));

        var targets = new Dictionary<ulong, List<ulong>> { [target] = [] };
        var seen = new HashSet<ulong> { target };

        for (int level = 1; level <= maxLevel && targets.Count > 0 && results.Count < maxResults; level++)
        {
            var sorted = targets.Keys.OrderBy(k => k).ToArray();
            var next = new Dictionary<ulong, List<ulong>>();
            var buffer = new byte[Chunk];
            int ri = 0;
            foreach (var r in regions)
            {
                ri++;
                progress?.Report(((level - 1) + (double)ri / regions.Count) / maxLevel);
                for (ulong pos = r.BaseAddress; pos < r.End; pos += Chunk)
                {
                    ct.ThrowIfCancellationRequested();
                    int len = (int)Math.Min((ulong)Chunk, r.End - pos);
                    var span = buffer.AsSpan(0, len);
                    if (!memory.TryRead(pos, span)) continue;
                    for (int i = 0; i + ptrSize <= len; i += ptrSize)
                    {
                        ulong value = ptrSize == 8 ? BitConverter.ToUInt64(span[i..]) : BitConverter.ToUInt32(span[i..]);
                        if (value == 0) continue;
                        // Cari target t dengan value <= t <= value + maxOffset
                        int idx = LowerBound(sorted, value);
                        for (int k = idx; k < sorted.Length && sorted[k] - value <= maxOffset; k++)
                        {
                            ulong holder = pos + (ulong)i;
                            ulong t = sorted[k];
                            var path = new List<ulong>(targets[t].Count + 1) { t - value };
                            path.AddRange(targets[t]);

                            if (ModuleOf(holder) is { } m && !IsSystemModule(m))
                            {
                                results.Add(new PointerPath(m.Name, holder - m.BaseAddress, path));
                                if (results.Count >= maxResults) goto done;
                            }
                            else if (level < maxLevel && next.Count < maxTargetsPerLevel && seen.Add(holder))
                            {
                                next[holder] = path;
                            }
                        }
                    }
                }
            }
            targets = next;
        }
    done:
        progress?.Report(1);
        // Validasi ulang dan urutkan: rantai terpendek dulu.
        var resolver = new AddressResolver(memory);
        return results
            .Where(p =>
            {
                try { return resolver.Resolve(p.ToSpec()) == target; }
                catch { return false; }
            })
            .OrderBy(p => p.Offsets.Count)
            .ThenBy(p => p.Offsets.Sum(o => (double)o))
            .ToList();
    }

    private static int LowerBound(ulong[] arr, ulong value)
    {
        int lo = 0, hi = arr.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (arr[mid] < value) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
