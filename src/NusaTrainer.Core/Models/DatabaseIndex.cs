namespace NusaTrainer.Core.Models;

/// <summary>index.json — daftar isi database yang ditandatangani (index.json.sig).</summary>
public sealed class DatabaseIndex
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;
    public DateTimeOffset Updated { get; set; }
    /// <summary>Versi aplikasi minimum yang dibutuhkan database ini.</summary>
    public string? MinAppVersion { get; set; }
    public List<IndexEntry> Games { get; set; } = new();
    /// <summary>Info rilis aplikasi terbaru (untuk self-update).</summary>
    public AppReleaseInfo? App { get; set; }
}

public sealed class IndexEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Revision { get; set; }
    /// <summary>Path relatif terhadap URL database, mis. "games/demogame.json".</summary>
    public string File { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public int CheatCount { get; set; }
    public int ModCount { get; set; }
}

public sealed class AppReleaseInfo
{
    public string Version { get; set; } = "0.0.0";
    /// <summary>URL .zip build win-x64.</summary>
    public string DownloadUrl { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public string? Notes { get; set; }
}
