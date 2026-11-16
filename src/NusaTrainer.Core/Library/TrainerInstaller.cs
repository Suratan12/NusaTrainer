using System.IO.Compression;
using NusaTrainer.Core.Games;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Library;

public enum VersionMatch { Match, Mismatch, Unknown, Any }

/// <summary>
/// Mengunduh trainer dari katalog terverifikasi (URL langsung + SHA-256 di database yang ditandatangani),
/// mengekstraknya ke folder aplikasi, dan mendaftarkannya sebagai alat game.
/// </summary>
public sealed class TrainerInstaller(HttpClient http, string root)
{
    public static VersionMatch CheckVersion(ExternalTrainerLink link, InstalledGame? game)
    {
        bool hasVersions = link.GameVersions is { Count: > 0 };
        bool hasBuilds = link.SteamBuildIds is { Count: > 0 };
        if (!hasVersions && !hasBuilds) return VersionMatch.Any;
        if (game is null || (game.Version is null && game.BuildId is null)) return VersionMatch.Unknown;

        if (hasBuilds && game.BuildId is { } b && link.SteamBuildIds!.Contains(b)) return VersionMatch.Match;
        if (hasVersions && game.Version is { } v
            && link.GameVersions!.Any(gv => v.StartsWith(gv.Trim(), StringComparison.OrdinalIgnoreCase)))
            return VersionMatch.Match;

        // Tidak cocok hanya bila informasi yang relevan memang ada.
        bool comparable = (hasBuilds && game.BuildId is not null) || (hasVersions && game.Version is not null);
        return comparable ? VersionMatch.Mismatch : VersionMatch.Unknown;
    }

    /// <summary>Unduh, verifikasi, ekstrak. Mengembalikan path .exe trainer.</summary>
    public async Task<string> InstallAsync(string gameKey, ExternalTrainerLink link, string resolvedUrl,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (link.Sha256 is null) throw new InvalidOperationException("Entri trainer tidak punya SHA-256.");
        var sha = link.Sha256.ToLowerInvariant();
        var dir = Path.Combine(root, Sanitize(gameKey), sha[..16]);
        var fileName = Path.GetFileName(new Uri(resolvedUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "trainer.zip";
        var download = Path.Combine(dir, "_download", fileName);

        if (!File.Exists(download) || !Hashing.HashEquals(await Hashing.Sha256HexAsync(download, ct), sha))
            await Downloader.DownloadVerifiedAsync(http, resolvedUrl, download, sha, progress, ct);

        var extractDir = Path.Combine(dir, "files");
        if (Directory.Exists(extractDir)) Directory.Delete(extractDir, true);
        Directory.CreateDirectory(extractDir);

        if (fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exe = Path.Combine(extractDir, fileName);
            File.Copy(download, exe, true);
            return exe;
        }

        var root2 = Path.GetFullPath(extractDir) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(download))
        {
            foreach (var e in zip.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(extractDir, e.FullName));
                if (!dest.StartsWith(root2, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Arsip trainer berisi path berbahaya.");
                if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        }

        // Trainer utama = exe terbesar (abaikan uninstaller/helper).
        var exes = Directory.EnumerateFiles(extractDir, "*.exe", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith("unins", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => new FileInfo(f).Length)
            .ToList();
        return exes.FirstOrDefault() ?? throw new InvalidDataException("Arsip trainer tidak berisi file .exe.");
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_'));
}
