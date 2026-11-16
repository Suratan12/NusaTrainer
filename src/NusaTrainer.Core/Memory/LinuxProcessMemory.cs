using System.Globalization;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NusaTrainer.Core.Memory;

/// <summary>
/// Implementasi Linux via /proc/&lt;pid&gt;/mem. Dipakai untuk pengujian otomatis (CI) dan game native Linux.
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxProcessMemory : IProcessMemory
{
    private readonly SafeFileHandle _mem;

    public int ProcessId { get; }
    public bool Is64Bit => Environment.Is64BitOperatingSystem;

    public LinuxProcessMemory(int pid)
    {
        ProcessId = pid;
        try
        {
            _mem = File.OpenHandle($"/proc/{pid}/mem", FileMode.Open, FileAccess.ReadWrite);
        }
        catch (Exception ex)
        {
            throw new MemoryAccessException($"Gagal membuka memori proses {pid}: {ex.Message}");
        }
    }

    public bool HasExited => !Directory.Exists($"/proc/{ProcessId}");

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        try { return RandomAccess.Read(_mem, buffer, (long)address) == buffer.Length; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public unsafe bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        // Jangan pakai RandomAccess.Write: ia mengulang tanpa henti bila pwrite mengembalikan 0
        // (terjadi saat proses target sudah keluar).
        if (data.IsEmpty) return true;
        bool added = false;
        try
        {
            _mem.DangerousAddRef(ref added);
            fixed (byte* p = data)
            {
                nint written = pwrite((int)_mem.DangerousGetHandle(), p, data.Length, (long)address);
                return written == data.Length;
            }
        }
        catch (ObjectDisposedException) { return false; }
        finally
        {
            if (added) _mem.DangerousRelease();
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
    private static extern unsafe nint pwrite(int fd, byte* buf, nint count, long offset);

    private IEnumerable<(ulong start, ulong end, string perms, string path)> ReadMaps()
    {
        foreach (var line in File.ReadLines($"/proc/{ProcessId}/maps"))
        {
            var parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 5) continue;
            var range = parts[0].Split('-');
            ulong start = ulong.Parse(range[0], NumberStyles.HexNumber);
            ulong end = ulong.Parse(range[1], NumberStyles.HexNumber);
            yield return (start, end, parts[1], parts.Length >= 6 ? parts[5].Trim() : "");
        }
    }

    public IReadOnlyList<MemoryRegion> GetRegions() =>
        ReadMaps()
            .Where(m => m.perms[0] == 'r' && m.path != "[vvar]" && m.path != "[vsyscall]")
            .Select(m => new MemoryRegion(m.start, m.end - m.start, true, m.perms[1] == 'w', m.perms[2] == 'x'))
            .ToList();

    public IReadOnlyList<ModuleInfo> GetModules() =>
        ReadMaps()
            // Hanya file sungguhan; abaikan region anonim/JIT seperti "/memfd:doublemapper (deleted)".
            .Where(m => m.path.StartsWith('/') && !m.path.StartsWith("/memfd:", StringComparison.Ordinal)
                        && !m.path.EndsWith("(deleted)", StringComparison.Ordinal) && !m.path.StartsWith("/dev/", StringComparison.Ordinal))
            .GroupBy(m => m.path)
            .Select(g => new ModuleInfo(Path.GetFileName(g.Key), g.Min(x => x.start), g.Max(x => x.end) - g.Min(x => x.start), g.Key))
            .ToList();

    public void Dispose() => _mem.Dispose();
}
