using NusaTrainer.Core.Games;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Library;

public enum ExternalToolKind { Trainer, CheatTable }

/// <summary>Trainer (.exe) atau tabel Cheat Engine (.CT) milik pengguna untuk sebuah game.</summary>
public sealed class ExternalTool
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public ExternalToolKind Kind { get; set; }
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    /// <summary>Jalankan otomatis saat game terdeteksi berjalan.</summary>
    public bool AutoLaunch { get; set; }
}

public sealed class ManualGame
{
    public string ExePath { get; set; } = "";
    public string Name { get; set; } = "";
}

/// <summary>Data milik pengguna (bukan dari database): game manual, trainer/tabel, dan cheat hasil impor tabel.</summary>
public sealed class UserLibrary
{
    public List<ManualGame> ManualGames { get; set; } = new();
    /// <summary>Kunci game → alat. Kunci = id database atau kunci game terpasang (mis. "steam-292030").</summary>
    public Dictionary<string, List<ExternalTool>> Tools { get; set; } = new();
    /// <summary>Kunci game → cheat hasil impor tabel Cheat Engine.</summary>
    public Dictionary<string, List<CheatDefinition>> ImportedCheats { get; set; } = new();

    public static string FilePath => System.IO.Path.Combine(AppPaths.DataRoot, "library.json");
    public static string TablesDir => System.IO.Path.Combine(AppPaths.DataRoot, "tables");

    public static UserLibrary Load(string? path = null)
    {
        path ??= FilePath;
        if (!File.Exists(path)) return new UserLibrary();
        try { return Json.Deserialize<UserLibrary>(File.ReadAllBytes(path)); }
        catch (Exception ex)
        {
            Log.Error("library.json rusak — dicadangkan dan dimulai dari kosong", ex);
            try { File.Copy(path, path + ".bak", true); } catch { }
            return new UserLibrary();
        }
    }

    public void Save(string? path = null) => AtomicFile.WriteAllBytes(path ?? FilePath, Json.SerializeToUtf8(this));

    public List<ExternalTool> ToolsFor(IEnumerable<string> keys) =>
        keys.Distinct().SelectMany(k => Tools.TryGetValue(k, out var l) ? l : []).ToList();

    public List<CheatDefinition> ImportedFor(IEnumerable<string> keys) =>
        keys.Distinct().SelectMany(k => ImportedCheats.TryGetValue(k, out var l) ? l : []).ToList();

    public void AddTool(string key, ExternalTool tool)
    {
        if (!Tools.TryGetValue(key, out var list)) Tools[key] = list = new();
        list.Add(tool);
    }

    public bool RemoveTool(string toolId)
    {
        foreach (var list in Tools.Values)
            if (list.RemoveAll(t => t.Id == toolId) > 0) return true;
        return false;
    }

    /// <summary>Gabungkan cheat impor (id duplikat diberi akhiran).</summary>
    public void AddImported(string key, IEnumerable<CheatDefinition> cheats)
    {
        if (!ImportedCheats.TryGetValue(key, out var list)) ImportedCheats[key] = list = new();
        var used = new HashSet<string>(list.Select(c => c.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var c in cheats)
        {
            var id = c.Id;
            for (int i = 2; !used.Add(id); i++) id = $"{c.Id}-{i}";
            c.Id = id;
            list.Add(c);
        }
    }

    public void ClearImported(IEnumerable<string> keys)
    {
        foreach (var k in keys) ImportedCheats.Remove(k);
    }

    public IEnumerable<InstalledGame> ManualInstalled() =>
        ManualGames.Where(m => File.Exists(m.ExePath)).Select(m => InstalledGamesScanner.FromExecutable(m.ExePath, m.Name));
}
