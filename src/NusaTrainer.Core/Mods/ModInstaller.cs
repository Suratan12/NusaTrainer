using System.IO.Compression;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Mods;

public sealed class InstalledMod
{
    public string GameId { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Version { get; set; } = "";
    public string GameDir { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    /// <summary>File yang dipasang (relatif terhadap GameDir).</summary>
    public List<InstalledFile> Files { get; set; } = new();
    /// <summary>Folder yang dibuat oleh pemasangan (relatif), dihapus saat uninstall bila kosong.</summary>
    public List<string> CreatedDirectories { get; set; } = new();
}

public sealed class InstalledFile
{
    public string Path { get; set; } = "";
    /// <summary>true = file asli game sudah dicadangkan dan akan dikembalikan saat uninstall.</summary>
    public bool BackedUp { get; set; }
}

public sealed class ModInstallException(string message) : Exception(message);

/// <summary>
/// Memasang mod (.zip) ke folder game dengan aman:
/// verifikasi SHA-256, proteksi zip-slip, cadangan file asli, dan uninstall yang mengembalikan semuanya.
/// </summary>
public sealed class ModInstaller(HttpClient http, string stateRoot)
{
    private string CacheDir => System.IO.Path.Combine(stateRoot, "cache", "mods");
    private string ManifestDir(string gameId) => System.IO.Path.Combine(stateRoot, "installed", gameId);
    private string ManifestPath(string gameId, string modId) => System.IO.Path.Combine(ManifestDir(gameId), modId + ".json");
    private string BackupDir(string gameId, string modId) => System.IO.Path.Combine(stateRoot, "backups", gameId, modId);

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public InstalledMod? GetInstalled(string gameId, string modId)
    {
        var p = ManifestPath(gameId, modId);
        if (!File.Exists(p)) return null;
        try { return Json.Deserialize<InstalledMod>(File.ReadAllBytes(p)); } catch { return null; }
    }

    public List<InstalledMod> GetInstalled(string gameId)
    {
        var dir = ManifestDir(gameId);
        if (!Directory.Exists(dir)) return new();
        var list = new List<InstalledMod>();
        foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
        {
            try { list.Add(Json.Deserialize<InstalledMod>(File.ReadAllBytes(f))); } catch { }
        }
        return list;
    }

    public async Task<InstalledMod> InstallAsync(string gameId, ModDefinition mod, string gameDir,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(gameDir)) throw new ModInstallException("Folder game tidak ditemukan.");
        gameDir = System.IO.Path.GetFullPath(gameDir);

        // 1. Unduh (pakai cache bila hash sama)
        var zipPath = System.IO.Path.Combine(CacheDir, mod.Sha256.ToLowerInvariant() + ".zip");
        if (!File.Exists(zipPath) || !Hashing.HashEquals(await Hashing.Sha256HexAsync(zipPath, ct), mod.Sha256))
            await Downloader.DownloadVerifiedAsync(http, mod.DownloadUrl, zipPath, mod.Sha256,
                progress is null ? null : new Progress<double>(p => progress.Report(p * 0.8)), ct);

        // 2. Upgrade: copot versi lama dulu
        if (GetInstalled(gameId, mod.Id) is not null)
            Uninstall(gameId, mod.Id);

        var targetRoot = DatabasePathGuard(gameDir, mod.InstallPath);
        var ownedByOthers = GetInstalled(gameId)
            .SelectMany(m => m.Files.Select(f => (mod: m.ModId, path: f.Path)))
            .ToDictionary(x => x.path, x => x.mod, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        using var zip = ZipFile.OpenRead(zipPath);
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (entries.Count == 0) throw new ModInstallException("Arsip mod kosong.");

        // 3. Rencanakan + validasi semua path sebelum menyentuh disk
        var plan = new List<(ZipArchiveEntry entry, string dest, string rel)>();
        foreach (var e in entries)
        {
            var dest = System.IO.Path.GetFullPath(System.IO.Path.Combine(targetRoot, e.FullName));
            if (!dest.StartsWith(WithSep(targetRoot), PathComparison))
                throw new ModInstallException($"Arsip berisi path berbahaya: {e.FullName}");
            var rel = System.IO.Path.GetRelativePath(gameDir, dest);
            if (ownedByOthers.TryGetValue(rel, out var other))
                throw new ModInstallException($"Bentrok dengan mod '{other}' (file {rel}). Copot mod tersebut dulu.");
            plan.Add((e, dest, rel));
        }

        var installed = new InstalledMod
        {
            GameId = gameId, ModId = mod.Id, Version = mod.Version, GameDir = gameDir, InstalledAt = DateTimeOffset.Now,
        };
        var backupRoot = BackupDir(gameId, mod.Id);
        if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);

        try
        {
            int i = 0;
            foreach (var (entry, dest, rel) in plan)
            {
                ct.ThrowIfCancellationRequested();
                CreateDirs(gameDir, System.IO.Path.GetDirectoryName(dest)!, installed.CreatedDirectories);
                bool backedUp = false;
                if (File.Exists(dest))
                {
                    var backup = System.IO.Path.Combine(backupRoot, rel);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(backup)!);
                    File.Copy(dest, backup, true);
                    backedUp = true;
                }
                installed.Files.Add(new InstalledFile { Path = rel, BackedUp = backedUp });
                entry.ExtractToFile(dest, overwrite: true);
                progress?.Report(0.8 + 0.2 * (++i) / plan.Count);
            }
        }
        catch
        {
            // Rollback sebagian pemasangan
            Revert(installed, backupRoot);
            throw;
        }

        AtomicFile.WriteAllBytes(ManifestPath(gameId, mod.Id), Json.SerializeToUtf8(installed));
        progress?.Report(1);
        return installed;
    }

    public void Uninstall(string gameId, string modId)
    {
        var installed = GetInstalled(gameId, modId) ?? throw new ModInstallException("Mod tidak terpasang.");
        Revert(installed, BackupDir(gameId, modId));
        File.Delete(ManifestPath(gameId, modId));
        var backupRoot = BackupDir(gameId, modId);
        if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
    }

    private static void Revert(InstalledMod installed, string backupRoot)
    {
        foreach (var f in installed.Files)
        {
            var dest = System.IO.Path.Combine(installed.GameDir, f.Path);
            if (File.Exists(dest)) File.Delete(dest);
            if (f.BackedUp)
            {
                var backup = System.IO.Path.Combine(backupRoot, f.Path);
                if (File.Exists(backup)) File.Copy(backup, dest, true);
            }
        }
        foreach (var d in installed.CreatedDirectories.OrderByDescending(d => d.Length))
        {
            var full = System.IO.Path.Combine(installed.GameDir, d);
            if (Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
                Directory.Delete(full);
        }
    }

    private static void CreateDirs(string gameDir, string dir, List<string> created)
    {
        var missing = new Stack<string>();
        var cur = dir;
        while (!Directory.Exists(cur))
        {
            missing.Push(cur);
            cur = System.IO.Path.GetDirectoryName(cur)!;
        }
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            Directory.CreateDirectory(d);
            created.Add(System.IO.Path.GetRelativePath(gameDir, d));
        }
    }

    private static string DatabasePathGuard(string gameDir, string installPath)
    {
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(gameDir, installPath ?? ""));
        if (!(full + System.IO.Path.DirectorySeparatorChar).StartsWith(WithSep(gameDir), PathComparison))
            throw new ModInstallException("installPath keluar dari folder game.");
        return full;
    }

    private static string WithSep(string p) =>
        p.EndsWith(System.IO.Path.DirectorySeparatorChar) ? p : p + System.IO.Path.DirectorySeparatorChar;
}
