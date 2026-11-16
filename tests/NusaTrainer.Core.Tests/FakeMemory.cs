using NusaTrainer.Core.Memory;

namespace NusaTrainer.Core.Tests;

/// <summary>Memori palsu: satu blok byte pada alamat Base, dengan satu module "game.exe" yang menutupi seluruhnya.</summary>
public sealed class FakeMemory : IProcessMemory
{
    public const ulong Base = 0x140000000;
    public byte[] Data { get; }
    public int ProcessId => 1234;
    public bool Is64Bit { get; set; } = true;
    public bool HasExited { get; set; }
    public int WriteCount { get; private set; }

    public FakeMemory(int size = 0x10000) => Data = new byte[size];

    public bool TryRead(ulong address, Span<byte> buffer)
    {
        if (address < Base || address + (ulong)buffer.Length > Base + (ulong)Data.Length) return false;
        Data.AsSpan((int)(address - Base), buffer.Length).CopyTo(buffer);
        return true;
    }

    public bool TryWrite(ulong address, ReadOnlySpan<byte> data)
    {
        if (address < Base || address + (ulong)data.Length > Base + (ulong)Data.Length) return false;
        data.CopyTo(Data.AsSpan((int)(address - Base)));
        WriteCount++;
        return true;
    }

    public IReadOnlyList<MemoryRegion> GetRegions() =>
    [
        new(Base, 0x1000, true, false, true),                          // "kode"
        new(Base + 0x1000, (ulong)Data.Length - 0x1000, true, true, false), // "data"
    ];

    public IReadOnlyList<ModuleInfo> GetModules() => [new("game.exe", Base, (ulong)Data.Length, null)];

    public void Put(int offset, ReadOnlySpan<byte> bytes) => bytes.CopyTo(Data.AsSpan(offset));
    public void PutInt(int offset, int v) => BitConverter.GetBytes(v).CopyTo(Data, offset);
    public void PutLong(int offset, long v) => BitConverter.GetBytes(v).CopyTo(Data, offset);
    public int GetInt(int offset) => BitConverter.ToInt32(Data, offset);
    public float GetFloat(int offset) => BitConverter.ToSingle(Data, offset);

    public void Dispose() { }
}
