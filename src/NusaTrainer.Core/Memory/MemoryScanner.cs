namespace NusaTrainer.Core.Memory;

public static class MemoryScanner
{
    private const int ChunkSize = 4 * 1024 * 1024;

    /// <summary>Cari pola di region proses. Mengembalikan alamat kecocokan pertama atau null.</summary>
    public static ulong? FindFirst(IProcessMemory memory, AobPattern pattern, string region = "any",
        ModuleInfo? module = null, CancellationToken ct = default)
    {
        foreach (var r in memory.GetRegions())
        {
            if (!r.Readable) continue;
            if (!RegionMatches(r, region)) continue;

            ulong start = r.BaseAddress, end = r.End;
            if (module is { } m)
            {
                if (end <= m.BaseAddress || start >= m.End) continue;
                start = Math.Max(start, m.BaseAddress);
                end = Math.Min(end, m.End);
            }

            var hit = ScanRange(memory, pattern, start, end, ct);
            if (hit is not null) return hit;
        }
        return null;
    }

    private static bool RegionMatches(MemoryRegion r, string region) => region.ToLowerInvariant() switch
    {
        "code" => r.Executable,
        "data" => r.Writable && !r.Executable,
        _ => true,
    };

    private static ulong? ScanRange(IProcessMemory memory, AobPattern pattern, ulong start, ulong end, CancellationToken ct)
    {
        int overlap = pattern.Length - 1;
        var buffer = new byte[ChunkSize + overlap];
        ulong pos = start;
        while (pos < end)
        {
            ct.ThrowIfCancellationRequested();
            int toRead = (int)Math.Min((ulong)buffer.Length, end - pos);
            if (toRead < pattern.Length) break;
            var span = buffer.AsSpan(0, toRead);
            if (memory.TryRead(pos, span))
            {
                int idx = pattern.IndexOf(span);
                if (idx >= 0) return pos + (ulong)idx;
            }
            if ((ulong)toRead < (ulong)buffer.Length) break; // potongan terakhir
            pos += (ulong)(toRead - overlap);
        }
        return null;
    }
}
