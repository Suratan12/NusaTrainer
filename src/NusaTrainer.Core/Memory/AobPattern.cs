using System.Globalization;

namespace NusaTrainer.Core.Memory;

/// <summary>Pola array-of-bytes dengan wildcard, mis. "48 8B ?? 05 ? 90".</summary>
public sealed class AobPattern
{
    public byte[] Bytes { get; }
    /// <summary>true = byte harus cocok; false = wildcard.</summary>
    public bool[] Mask { get; }
    public int Length => Bytes.Length;

    private AobPattern(byte[] bytes, bool[] mask)
    {
        Bytes = bytes;
        Mask = mask;
    }

    public static AobPattern Parse(string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            throw new FormatException("Pola AOB kosong.");

        var tokens = pattern.Split((char[])[' ', '\t', ','], StringSplitOptions.RemoveEmptyEntries);
        var bytes = new byte[tokens.Length];
        var mask = new bool[tokens.Length];
        for (int i = 0; i < tokens.Length; i++)
        {
            var t = tokens[i];
            if (t is "?" or "??" or "**")
            {
                mask[i] = false;
                continue;
            }
            if (t.Length != 2 || !byte.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"Token AOB tidak valid: '{t}'.");
            mask[i] = true;
        }
        if (!mask.Any(m => m))
            throw new FormatException("Pola AOB harus punya minimal satu byte pasti.");
        return new AobPattern(bytes, mask);
    }

    /// <summary>Cari kemunculan pertama dalam buffer mulai dari <paramref name="start"/>. -1 bila tidak ada.</summary>
    public int IndexOf(ReadOnlySpan<byte> data, int start = 0)
    {
        // Gunakan byte pasti pertama sebagai jangkar untuk IndexOf yang di-vektorisasi.
        int anchor = Array.IndexOf(Mask, true);
        byte anchorByte = Bytes[anchor];
        int last = data.Length - Length;
        int i = start;
        while (i <= last)
        {
            int found = data.Slice(i + anchor, last - i + 1).IndexOf(anchorByte);
            if (found < 0) return -1;
            i += found;
            if (MatchesAt(data, i)) return i;
            i++;
        }
        return -1;
    }

    public bool MatchesAt(ReadOnlySpan<byte> data, int offset)
    {
        for (int j = 0; j < Length; j++)
        {
            if (Mask[j] && data[offset + j] != Bytes[j]) return false;
        }
        return true;
    }

    public static byte[] ParseBytes(string hex)
    {
        var p = Parse(hex);
        if (p.Mask.Any(m => !m)) throw new FormatException("Byte patch tidak boleh berisi wildcard.");
        return p.Bytes;
    }
}
