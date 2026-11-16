namespace NusaTrainer.Core.Util;

public static class SemVer
{
    /// <summary>Bandingkan "1.2.3" vs "1.10.0" (suffix seperti "-beta" diabaikan).</summary>
    public static int Compare(string? a, string? b)
    {
        var pa = Parse(a);
        var pb = Parse(b);
        for (int i = 0; i < 4; i++)
        {
            int c = pa[i].CompareTo(pb[i]);
            if (c != 0) return c;
        }
        return 0;
    }

    private static int[] Parse(string? v)
    {
        var result = new int[4];
        if (string.IsNullOrWhiteSpace(v)) return result;
        var core = v.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        var parts = core.Split('.');
        for (int i = 0; i < Math.Min(4, parts.Length); i++)
            int.TryParse(parts[i], out result[i]);
        return result;
    }
}
