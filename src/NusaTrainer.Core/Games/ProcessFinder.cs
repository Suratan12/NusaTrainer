using System.Diagnostics;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Games;

public sealed record RunningGame(int ProcessId, string ProcessName, string? ExePath);

public static class ProcessFinder
{
    public static string Normalize(string name)
    {
        var n = name.Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    /// <summary>Snapshot proses berjalan: nama (tanpa .exe, huruf kecil) → daftar PID.</summary>
    public static Dictionary<string, List<int>> Snapshot()
    {
        var map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = Normalize(p.ProcessName);
                if (!map.TryGetValue(name, out var list)) map[name] = list = new();
                list.Add(p.Id);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return map;
    }

    public static RunningGame? Find(GameDefinition game, Dictionary<string, List<int>> snapshot)
    {
        foreach (var pn in game.ProcessNames)
        {
            if (snapshot.TryGetValue(Normalize(pn), out var pids) && pids.Count > 0)
            {
                int pid = pids.Min();
                string? exe = null;
                try { using var p = Process.GetProcessById(pid); exe = p.MainModule?.FileName; } catch { }
                return new RunningGame(pid, Normalize(pn), exe);
            }
        }
        return null;
    }

    /// <summary>Tebak root folder game dari path exe yang berjalan.</summary>
    public static string? GuessGameRoot(GameDefinition game, string? exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return null;
        var dir = Path.GetDirectoryName(exePath);
        if (dir is null) return null;
        if (string.IsNullOrWhiteSpace(game.ExeRelativeDir)) return dir;
        int depth = game.ExeRelativeDir.Split('/', '\\', StringSplitOptions.RemoveEmptyEntries).Length;
        for (int i = 0; i < depth && dir is not null; i++) dir = Path.GetDirectoryName(dir);
        return dir;
    }
}
