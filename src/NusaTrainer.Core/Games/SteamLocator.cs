using System.Text.RegularExpressions;

namespace NusaTrainer.Core.Games;

/// <summary>Menemukan folder instalasi game Steam lewat libraryfolders.vdf dan appmanifest_*.acf.</summary>
public static partial class SteamLocator
{
    [GeneratedRegex("\"path\"\\s+\"(?<p>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex PathRegex();

    [GeneratedRegex("\"installdir\"\\s+\"(?<d>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex InstallDirRegex();

    public static string? GetSteamPath()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string p && Directory.Exists(p)) return Path.GetFullPath(p);
            }
            catch { }
            var def = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
            return Directory.Exists(def) ? def : null;
        }
        var linux = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".steam", "steam");
        return Directory.Exists(linux) ? linux : null;
    }

    public static List<string> GetLibraryFolders(string steamPath)
    {
        var result = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match m in PathRegex().Matches(File.ReadAllText(vdf)))
            {
                var p = m.Groups["p"].Value.Replace(@"\\", @"\");
                if (!result.Contains(p, StringComparer.OrdinalIgnoreCase)) result.Add(p);
            }
        }
        return result;
    }

    public static string? FindGameDir(int appId, string? steamPath = null)
    {
        steamPath ??= GetSteamPath();
        if (steamPath is null) return null;
        foreach (var lib in GetLibraryFolders(steamPath))
        {
            var manifest = Path.Combine(lib, "steamapps", $"appmanifest_{appId}.acf");
            if (!File.Exists(manifest)) continue;
            var m = InstallDirRegex().Match(File.ReadAllText(manifest));
            if (!m.Success) continue;
            var dir = Path.Combine(lib, "steamapps", "common", m.Groups["d"].Value);
            if (Directory.Exists(dir)) return dir;
        }
        return null;
    }
}
