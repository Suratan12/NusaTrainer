using System.Text.Json;
using System.Text.RegularExpressions;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Games;

public enum GameSource { Steam, Epic, Gog, Manual }

/// <summary>Game yang terdeteksi terpasang di PC.</summary>
public sealed record InstalledGame(
    GameSource Source,
    string SourceId,
    string Name,
    string InstallDir,
    IReadOnlyList<string> Executables,
    bool HasAntiCheat)
{
    /// <summary>Kunci stabil, mis. "steam-292030", "epic-fn", "gog-1207658924", "manual-1a2b3c4d".</summary>
    public string Key => MakeKey(Source, SourceId);

    public static string MakeKey(GameSource source, string id)
    {
        var clean = Regex.Replace(id.ToLowerInvariant(), "[^a-z0-9_-]+", "-").Trim('-');
        if (clean.Length > 48) clean = clean[..48];
        return $"{source.ToString().ToLowerInvariant()}-{clean}";
    }
}

/// <summary>Memindai launcher yang umum dipakai (Steam, Epic Games, GOG) untuk menemukan game terpasang.</summary>
public static partial class InstalledGamesScanner
{
    [GeneratedRegex("\"(?<k>appid|name|installdir)\"\\s+\"(?<v>[^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex AcfField();

    // Bukan game: runtime, redistributable, alat.
    private static readonly string[] SteamSkipNames =
        ["Steamworks Common Redistributables", "Proton", "Steam Linux Runtime", "SteamVR", "Wallpaper Engine", "Source SDK", "Dedicated Server"];
    private static readonly HashSet<string> SteamSkipIds = ["228980", "1070560", "1391110", "1628350", "250820", "431960"];

    private static readonly string[] ExeSkip =
    [
        "unins", "crash", "report", "setup", "redist", "prereq", "dxsetup", "vc_", "vcredist", "dotnet", "helper",
        "cefprocess", "easyanticheat", "battleye", "bssndrpt", "unitycrashhandler", "start_protected_game", "installer",
        "update", "touchup", "cleanup", "dxwebsetup", "physx", "oalinst", "directx", "quicksfv", "notification",
    ];

    // Folder yang hanya berisi installer/runtime/alat engine.
    private static readonly HashSet<string> SkipDirs =
        ["_commonredist", "commonredist", "redist", "redistributables", "directx", "vcredist", "easyanticheat", "battleye",
         "__installer", "support", "engine", "prereqs", "dotnet", "installers", "tools"];

    public static List<InstalledGame> ScanAll(Action<string>? onWarning = null)
    {
        var all = new List<InstalledGame>();
        void Try(string what, Func<IEnumerable<InstalledGame>> f)
        {
            try { all.AddRange(f()); }
            catch (Exception ex) { onWarning?.Invoke($"Pindai {what} gagal: {ex.Message}"); }
        }

        Try("Steam", () => SteamLocator.GetSteamPath() is { } sp ? ScanSteam(sp) : []);
        Try("Epic Games", () => ScanEpic(DefaultEpicManifestDir()));
        if (OperatingSystem.IsWindows()) Try("GOG", ScanGogRegistry);

        // Satu folder bisa muncul dua kali (mis. GOG + Steam); pertahankan yang pertama.
        return all
            .GroupBy(g => Path.GetFullPath(g.InstallDir).TrimEnd('\\', '/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static List<InstalledGame> ScanSteam(string steamPath)
    {
        var result = new List<InstalledGame>();
        foreach (var lib in SteamLocator.GetLibraryFolders(steamPath))
        {
            var apps = Path.Combine(lib, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var acf in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match m in AcfField().Matches(File.ReadAllText(acf)))
                    fields.TryAdd(m.Groups["k"].Value, m.Groups["v"].Value);
                if (!fields.TryGetValue("appid", out var appId) || !fields.TryGetValue("installdir", out var dirName)) continue;
                var name = fields.GetValueOrDefault("name") ?? dirName;
                if (SteamSkipIds.Contains(appId) || SteamSkipNames.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) continue;

                var dir = Path.Combine(apps, "common", dirName);
                if (!Directory.Exists(dir)) continue;
                result.Add(Create(GameSource.Steam, appId, name, dir, null));
            }
        }
        return result;
    }

    public static string DefaultEpicManifestDir() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");

    public static List<InstalledGame> ScanEpic(string manifestsDir)
    {
        var result = new List<InstalledGame>();
        if (!Directory.Exists(manifestsDir)) return result;
        foreach (var item in Directory.EnumerateFiles(manifestsDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(item));
                var r = doc.RootElement;
                string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

                var appName = Str("AppName");
                var name = Str("DisplayName");
                var dir = Str("InstallLocation");
                if (appName is null || name is null || dir is null || !Directory.Exists(dir)) continue;
                if (appName.StartsWith("UE_", StringComparison.OrdinalIgnoreCase)) continue; // Unreal Engine
                if (r.TryGetProperty("bIsApplication", out var isApp) && isApp.ValueKind == JsonValueKind.False) continue;
                if (r.TryGetProperty("AppCategories", out var cats) && cats.ValueKind == JsonValueKind.Array
                    && cats.EnumerateArray().Any(c => c.GetString() is "plugins" or "engines"))
                    continue;

                result.Add(Create(GameSource.Epic, appName, name, dir, Str("LaunchExecutable")));
            }
            catch (JsonException) { }
        }
        return result;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<InstalledGame> ScanGogRegistry()
    {
        var result = new List<InstalledGame>();
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games")
                        ?? Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (key is null) return result;
        foreach (var sub in key.GetSubKeyNames())
        {
            using var g = key.OpenSubKey(sub);
            if (g is null) continue;
            var name = g.GetValue("gameName") as string;
            var dir = g.GetValue("path") as string;
            if (name is null || dir is null || !Directory.Exists(dir)) continue;
            var exe = g.GetValue("exe") as string ?? g.GetValue("exeFile") as string;
            result.Add(Create(GameSource.Gog, g.GetValue("gameID") as string ?? sub, name, dir, exe));
        }
        return result;
    }

    /// <summary>Buat entri game dari folder + exe yang dipilih manual oleh pengguna.</summary>
    public static InstalledGame FromExecutable(string exePath, string? name = null)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
        var id = Hashing.Sha256Hex(System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(exePath).ToLowerInvariant()))[..10];
        var exes = new List<string> { Path.GetFileName(exePath) };
        exes.AddRange(FindExecutables(dir).Where(e => !string.Equals(e, exes[0], StringComparison.OrdinalIgnoreCase)));
        return new InstalledGame(GameSource.Manual, id, name ?? Path.GetFileNameWithoutExtension(exePath), dir, exes, DetectAntiCheat(dir));
    }

    private static InstalledGame Create(GameSource source, string id, string name, string dir, string? preferredExe)
    {
        var exes = FindExecutables(dir);
        if (!string.IsNullOrWhiteSpace(preferredExe))
        {
            var pe = Path.GetFileName(preferredExe.Replace('\\', '/'));
            exes.RemoveAll(e => string.Equals(e, pe, StringComparison.OrdinalIgnoreCase));
            exes.Insert(0, pe);
        }
        return new InstalledGame(source, id, name, dir, exes, DetectAntiCheat(dir));
    }

    /// <summary>Daftar nama file .exe game (tanpa helper/installer), maksimal kedalaman 3 folder.</summary>
    public static List<string> FindExecutables(string dir, int maxDepth = 3, int max = 25)
    {
        var found = new List<(string name, long size)>();
        void Walk(string d, int depth)
        {
            if (found.Count >= max * 2) return;
            try
            {
                foreach (var f in Directory.EnumerateFiles(d, "*.exe"))
                {
                    var n = Path.GetFileName(f);
                    var lower = n.ToLowerInvariant();
                    if (ExeSkip.Any(lower.Contains)) continue;
                    long size = 0;
                    try { size = new FileInfo(f).Length; } catch { }
                    found.Add((n, size));
                }
                if (depth >= maxDepth) return;
                foreach (var sub in Directory.EnumerateDirectories(d))
                {
                    var sn = Path.GetFileName(sub).ToLowerInvariant();
                    if (SkipDirs.Contains(sn)) continue;
                    Walk(sub, depth + 1);
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        Walk(dir, 0);
        // Exe terbesar biasanya game utamanya.
        return found.OrderByDescending(f => f.size).Select(f => f.name)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(max).ToList();
    }

    public static bool DetectAntiCheat(string dir)
    {
        try
        {
            if (Directory.Exists(Path.Combine(dir, "EasyAntiCheat")) || Directory.Exists(Path.Combine(dir, "BattlEye"))) return true;
            if (File.Exists(Path.Combine(dir, "start_protected_game.exe"))) return true;
            return Directory.EnumerateFiles(dir, "*.exe").Any(f =>
                Path.GetFileName(f).StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith("BEService", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }
}
