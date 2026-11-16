using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Scanning;

public enum ScanValueKind
{
    /// <summary>Bilangan bulat 4 byte + desimal (float) sekaligus — cocok untuk sebagian besar game.</summary>
    Auto,
    Int32,
    Float,
    Double,
    Int64,
}

public enum NextScanMode
{
    /// <summary>Nilai sekarang sama dengan angka yang diketik.</summary>
    Exact,
    Increased,
    Decreased,
    Changed,
    Unchanged,
}

public readonly record struct ScanHit(ulong Address, DataType Type, double Value);

/// <summary>
/// Pemindai nilai terpandu (mirip "First Scan / Next Scan"): cari angka yang terlihat di game,
/// lalu persempit setelah nilainya berubah. Hanya memindai memori data yang bisa ditulis, dengan alignment 4 byte.
/// </summary>
public sealed class ValueScanner
{
    private const int ChunkSize = 4 * 1024 * 1024;
    /// <summary>Batas hasil agar memori aplikasi tetap wajar.</summary>
    public const int MaxResults = 4_000_000;

    private readonly IProcessMemory _memory;
    private ulong[] _addresses = [];
    private DataType[] _types = [];
    private double[] _values = [];
    private int _count;

    public ValueScanner(IProcessMemory memory) => _memory = memory;

    public int Count => _count;
    public bool HasScanned { get; private set; }
    public bool Truncated { get; private set; }

    /// <summary>Toleransi untuk desimal: angka yang ditampilkan game biasanya dibulatkan.</summary>
    public static bool FloatMatches(double actual, double target) =>
        !double.IsNaN(actual) && Math.Abs(actual - target) < (Math.Abs(target) >= 10 ? 0.5 : 0.05);

    public void Reset()
    {
        _addresses = [];
        _types = [];
        _values = [];
        _count = 0;
        HasScanned = false;
        Truncated = false;
    }

    public int FirstScan(double value, ScanValueKind kind, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        Reset();
        var addresses = new List<ulong>();
        var types = new List<DataType>();
        var values = new List<double>();

        bool wantInt = kind is ScanValueKind.Auto or ScanValueKind.Int32;
        bool wantFloat = kind is ScanValueKind.Auto or ScanValueKind.Float;
        bool wantDouble = kind == ScanValueKind.Double;
        bool wantInt64 = kind == ScanValueKind.Int64;
        bool isWhole = value == Math.Floor(value);
        int intValue = isWhole && value is >= int.MinValue and <= int.MaxValue ? (int)value : 0;
        bool intPossible = wantInt && isWhole && value is >= int.MinValue and <= int.MaxValue;
        long longValue = isWhole ? (long)value : 0;

        var regions = _memory.GetRegions().Where(r => r.Readable && r.Writable && !r.Executable).ToList();
        ulong total = (ulong)regions.Sum(r => (decimal)r.Size), done = 0;
        var buffer = new byte[ChunkSize];

        foreach (var region in regions)
        {
            for (ulong pos = region.BaseAddress; pos < region.End; pos += ChunkSize)
            {
                ct.ThrowIfCancellationRequested();
                int len = (int)Math.Min((ulong)ChunkSize, region.End - pos);
                done += (ulong)len;
                var span = buffer.AsSpan(0, len);
                if (!_memory.TryRead(pos, span)) continue;

                for (int i = 0; i + 4 <= len; i += 4)
                {
                    if (intPossible && BitConverter.ToInt32(span[i..]) == intValue)
                        Add(pos + (ulong)i, DataType.Int32, intValue);
                    if (wantFloat)
                    {
                        float f = BitConverter.ToSingle(span[i..]);
                        if (FloatMatches(f, value)) Add(pos + (ulong)i, DataType.Float, f);
                    }
                    if ((wantDouble || wantInt64) && (i & 7) == 0 && i + 8 <= len)
                    {
                        if (wantDouble)
                        {
                            double d = BitConverter.ToDouble(span[i..]);
                            if (FloatMatches(d, value)) Add(pos + (ulong)i, DataType.Double, d);
                        }
                        if (wantInt64 && isWhole && BitConverter.ToInt64(span[i..]) == longValue)
                            Add(pos + (ulong)i, DataType.Int64, longValue);
                    }
                    if (addresses.Count >= MaxResults) { Truncated = true; goto finish; }
                }
            }
            if (total > 0) progress?.Report((double)done / total);
        }
    finish:
        _addresses = addresses.ToArray();
        _types = types.ToArray();
        _values = values.ToArray();
        _count = _addresses.Length;
        HasScanned = true;
        progress?.Report(1);
        return _count;

        void Add(ulong a, DataType t, double v)
        {
            addresses.Add(a);
            types.Add(t);
            values.Add(v);
        }
    }

    public int NextScan(NextScanMode mode, double value = 0, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!HasScanned) throw new InvalidOperationException("Lakukan pindai pertama dulu.");
        int kept = 0;
        Span<byte> buf = stackalloc byte[8];
        for (int i = 0; i < _count; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report((double)i / Math.Max(1, _count));
            }
            var type = _types[i];
            int size = ValueCodec.SizeOf(type);
            if (!_memory.TryRead(_addresses[i], buf[..size])) continue;
            double now = ValueCodec.Decode(type, buf[..size]);
            double before = _values[i];
            bool isFloat = type is DataType.Float or DataType.Double;

            bool keep = mode switch
            {
                NextScanMode.Exact => isFloat ? FloatMatches(now, value) : now == value,
                NextScanMode.Increased => now > before,
                NextScanMode.Decreased => now < before,
                NextScanMode.Changed => isFloat ? Math.Abs(now - before) > 1e-6 : now != before,
                NextScanMode.Unchanged => isFloat ? Math.Abs(now - before) <= 1e-6 : now == before,
                _ => false,
            };
            if (double.IsNaN(now) || double.IsInfinity(now)) keep = false;
            if (!keep) continue;
            _addresses[kept] = _addresses[i];
            _types[kept] = type;
            _values[kept] = now;
            kept++;
        }
        _count = kept;
        Truncated = false;
        progress?.Report(1);
        return _count;
    }

    /// <summary>Ambil hasil teratas dengan nilai terbaru dari memori.</summary>
    public List<ScanHit> GetResults(int max = 100)
    {
        var list = new List<ScanHit>();
        Span<byte> buf = stackalloc byte[8];
        for (int i = 0; i < _count && list.Count < max; i++)
        {
            int size = ValueCodec.SizeOf(_types[i]);
            double v = _memory.TryRead(_addresses[i], buf[..size]) ? ValueCodec.Decode(_types[i], buf[..size]) : double.NaN;
            list.Add(new ScanHit(_addresses[i], _types[i], v));
        }
        return list;
    }
}
