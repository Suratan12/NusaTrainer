using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Games;

/// <summary>Mencocokkan game terpasang dengan definisi di database.</summary>
public static class LibraryMatcher
{
    public static GameDefinition? Match(InstalledGame game, IReadOnlyList<GameDefinition> definitions)
    {
        foreach (var d in definitions)
        {
            switch (game.Source)
            {
                case GameSource.Steam when d.SteamAppId?.ToString() == game.SourceId:
                case GameSource.Epic when d.EpicAppNames?.Contains(game.SourceId, StringComparer.OrdinalIgnoreCase) == true:
                case GameSource.Gog when d.GogIds?.Contains(game.SourceId, StringComparer.OrdinalIgnoreCase) == true:
                    return d;
            }
        }

        // Cadangan: nama proses di database sama dengan salah satu exe di folder game.
        var exes = new HashSet<string>(game.Executables.Select(ProcessFinder.Normalize), StringComparer.OrdinalIgnoreCase);
        return definitions.FirstOrDefault(d => d.ProcessNames.Any(p => exes.Contains(ProcessFinder.Normalize(p))));
    }

    /// <summary>Definisi sederhana untuk game terpasang yang belum ada di database (cheat bisa ditambah lewat impor tabel).</summary>
    public static GameDefinition Synthesize(InstalledGame game) => new()
    {
        Id = game.Key,
        Name = game.Name,
        Description = $"Terdeteksi dari {SourceLabel(game.Source)}. Belum ada cheat bawaan di database — cari uang/nyawa dengan Pemindai Nilai, impor tabel Cheat Engine, atau pakai trainer.",
        ProcessNames = game.Executables.Select(ProcessFinder.Normalize).ToList(),
        SteamAppId = game.Source == GameSource.Steam && int.TryParse(game.SourceId, out var id) ? id : null,
        EpicAppNames = game.Source == GameSource.Epic ? [game.SourceId] : null,
        GogIds = game.Source == GameSource.Gog ? [game.SourceId] : null,
        AccentColor = AccentFor(game.Name),
    };

    public static string SourceLabel(GameSource s) => s switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        _ => "folder manual",
    };

    private static readonly string[] Palette = ["#6366F1", "#0EA5E9", "#F59E0B", "#EF4444", "#10B981", "#A855F7", "#EC4899", "#14B8A6", "#F97316"];
    private static string AccentFor(string name) => Palette[(uint)name.Aggregate(17, (h, c) => h * 31 + c) % Palette.Length];
}
