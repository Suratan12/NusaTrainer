using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Database;

/// <summary>Database lokal di disk: &lt;root&gt;/index.json + &lt;root&gt;/games/*.json.</summary>
public sealed class DatabaseStore(string root)
{
    public string Root { get; } = root;
    public string IndexPath => Path.Combine(Root, "index.json");

    public DatabaseIndex? LoadIndex()
    {
        if (!File.Exists(IndexPath)) return null;
        try { return Json.Deserialize<DatabaseIndex>(File.ReadAllBytes(IndexPath)); }
        catch { return null; }
    }

    public string GamePath(IndexEntry entry) => SafeCombine(Root, entry.File);

    /// <summary>Muat semua game valid sesuai index. File yang hilang/korup/tidak cocok hash dilewati.</summary>
    public List<GameDefinition> LoadGames(Action<string>? onWarning = null)
    {
        var index = LoadIndex();
        var games = new List<GameDefinition>();
        if (index is null) return games;
        foreach (var e in index.Games)
        {
            try
            {
                var path = GamePath(e);
                if (!File.Exists(path)) { onWarning?.Invoke($"File {e.File} hilang."); continue; }
                var bytes = File.ReadAllBytes(path);
                if (!Hashing.HashEquals(Hashing.Sha256Hex(bytes), e.Sha256)) { onWarning?.Invoke($"Hash {e.File} tidak cocok."); continue; }
                var g = Json.Deserialize<GameDefinition>(bytes);
                var errors = GameDefinitionValidator.Validate(g);
                if (errors.Count > 0) { onWarning?.Invoke($"{e.File}: {string.Join("; ", errors)}"); continue; }
                games.Add(g);
            }
            catch (Exception ex)
            {
                onWarning?.Invoke($"Gagal memuat {e.File}: {ex.Message}");
            }
        }
        return games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>
    /// Salin database bawaan (ikut dengan aplikasi) bila database lokal belum ada
    /// atau lebih lama dari bawaan.
    /// </summary>
    public bool SeedFrom(string seedRoot)
    {
        var seedIndexPath = Path.Combine(seedRoot, "index.json");
        if (!File.Exists(seedIndexPath)) return false;
        var seed = Json.Deserialize<DatabaseIndex>(File.ReadAllBytes(seedIndexPath));
        var local = LoadIndex();
        if (local is not null && local.Updated >= seed.Updated) return false;

        foreach (var e in seed.Games)
        {
            var src = SafeCombine(seedRoot, e.File);
            if (File.Exists(src)) AtomicFile.WriteAllBytes(GamePath(e), File.ReadAllBytes(src));
        }
        AtomicFile.WriteAllBytes(IndexPath, File.ReadAllBytes(seedIndexPath));
        var sig = seedIndexPath + ".sig";
        if (File.Exists(sig)) AtomicFile.WriteAllBytes(IndexPath + ".sig", File.ReadAllBytes(sig));
        return true;
    }

    /// <summary>Gabungkan path relatif dengan aman (menolak '..' keluar dari root).</summary>
    public static string SafeCombine(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('\\', '/')));
        var rootWithSep = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!full.StartsWith(rootWithSep, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException($"Path '{relative}' keluar dari folder database.");
        return full;
    }
}
