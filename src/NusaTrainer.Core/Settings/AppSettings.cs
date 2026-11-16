using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Settings;

public sealed class AppSettings
{
    /// <summary>URL dasar database (folder berisi index.json), mis. https://user.github.io/NusaTrainer/</summary>
    public string DatabaseUrl { get; set; } = "";
    /// <summary>Public key ECDSA (PEM) untuk memverifikasi index.json.sig.</summary>
    public string PublicKeyPem { get; set; } = "";
    public bool RequireSignature { get; set; } = true;
    public bool AutoUpdateOnStart { get; set; } = true;
    public int UpdateIntervalHours { get; set; } = 6;
    public bool CheckAppUpdates { get; set; } = true;
    /// <summary>Folder game yang dipilih manual: gameId → path.</summary>
    public Dictionary<string, string> GameFolders { get; set; } = new();
    public DateTimeOffset? LastUpdateCheck { get; set; }

    public bool IsDatabaseUrlConfigured =>
        Uri.TryCreate(DatabaseUrl, UriKind.Absolute, out _) && !DatabaseUrl.Contains("USERNAME", StringComparison.Ordinal);

    public static AppSettings Load(string userPath, string? defaultsPath)
    {
        AppSettings? s = null;
        if (File.Exists(userPath))
        {
            try { s = Json.Deserialize<AppSettings>(File.ReadAllBytes(userPath)); } catch { }
        }
        if (s is null && defaultsPath is not null && File.Exists(defaultsPath))
        {
            try { s = Json.Deserialize<AppSettings>(File.ReadAllBytes(defaultsPath)); } catch { }
        }
        s ??= new AppSettings();

        // Kunci publik & URL dari file bawaan selalu dipakai bila pengguna belum mengisi sendiri.
        if (defaultsPath is not null && File.Exists(defaultsPath))
        {
            try
            {
                var d = Json.Deserialize<AppSettings>(File.ReadAllBytes(defaultsPath));
                if (string.IsNullOrWhiteSpace(s.PublicKeyPem)) s.PublicKeyPem = d.PublicKeyPem;
                if (!s.IsDatabaseUrlConfigured && d.IsDatabaseUrlConfigured) s.DatabaseUrl = d.DatabaseUrl;
            }
            catch { }
        }
        return s;
    }

    public void Save(string path) => AtomicFile.WriteAllBytes(path, Json.SerializeToUtf8(this));
}
