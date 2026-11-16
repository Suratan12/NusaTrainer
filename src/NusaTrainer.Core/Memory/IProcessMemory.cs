namespace NusaTrainer.Core.Memory;

public readonly record struct MemoryRegion(ulong BaseAddress, ulong Size, bool Readable, bool Writable, bool Executable)
{
    public ulong End => BaseAddress + Size;
}

public readonly record struct ModuleInfo(string Name, ulong BaseAddress, ulong Size, string? Path)
{
    public ulong End => BaseAddress + Size;
}

/// <summary>Abstraksi akses memori proses lain (Windows: ReadProcessMemory/WriteProcessMemory).</summary>
public interface IProcessMemory : IDisposable
{
    int ProcessId { get; }
    bool Is64Bit { get; }
    bool HasExited { get; }

    /// <summary>Membaca tepat buffer.Length byte. Return false bila gagal.</summary>
    bool TryRead(ulong address, Span<byte> buffer);

    /// <summary>Menulis byte. Untuk halaman kode, implementasi mengubah proteksi sementara.</summary>
    bool TryWrite(ulong address, ReadOnlySpan<byte> data);

    IReadOnlyList<MemoryRegion> GetRegions();

    IReadOnlyList<ModuleInfo> GetModules();
}

public static class ProcessMemoryExtensions
{
    public static ModuleInfo? FindModule(this IProcessMemory memory, string name)
    {
        var target = Path.GetFileName(name);
        foreach (var m in memory.GetModules())
        {
            if (string.Equals(m.Name, target, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        // Toleransi: cocokkan tanpa ekstensi
        var bare = Path.GetFileNameWithoutExtension(target);
        foreach (var m in memory.GetModules())
        {
            if (string.Equals(Path.GetFileNameWithoutExtension(m.Name), bare, StringComparison.OrdinalIgnoreCase))
                return m;
        }
        return null;
    }

    public static ulong ReadPointer(this IProcessMemory memory, ulong address)
    {
        Span<byte> buf = stackalloc byte[8];
        int size = memory.Is64Bit ? 8 : 4;
        if (!memory.TryRead(address, buf[..size]))
            throw new MemoryAccessException($"Gagal membaca pointer di 0x{address:X}.");
        return memory.Is64Bit ? BitConverter.ToUInt64(buf) : BitConverter.ToUInt32(buf);
    }

    public static byte[] ReadBytes(this IProcessMemory memory, ulong address, int count)
    {
        var buf = new byte[count];
        if (!memory.TryRead(address, buf))
            throw new MemoryAccessException($"Gagal membaca {count} byte di 0x{address:X}.");
        return buf;
    }

    public static void WriteBytes(this IProcessMemory memory, ulong address, ReadOnlySpan<byte> data)
    {
        if (!memory.TryWrite(address, data))
            throw new MemoryAccessException($"Gagal menulis {data.Length} byte di 0x{address:X}.");
    }
}

public sealed class MemoryAccessException(string message) : Exception(message);
