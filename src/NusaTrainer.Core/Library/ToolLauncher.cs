using System.Diagnostics;

namespace NusaTrainer.Core.Library;

public static class ToolLauncher
{
    /// <summary>Jalankan trainer .exe, atau buka tabel .CT dengan Cheat Engine.</summary>
    public static void Launch(ExternalTool tool)
    {
        if (!File.Exists(tool.Path))
            throw new FileNotFoundException($"File tidak ditemukan: {tool.Path}. Hapus lalu tambahkan ulang.");

        if (tool.Kind == ExternalToolKind.Trainer)
        {
            Process.Start(new ProcessStartInfo(tool.Path)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(tool.Path)!,
            });
            return;
        }

        var ce = FindCheatEngine();
        if (ce is not null)
        {
            Process.Start(new ProcessStartInfo(ce) { UseShellExecute = true, ArgumentList = { tool.Path } });
            return;
        }
        // Coba asosiasi file .CT (bila Cheat Engine terpasang di lokasi lain).
        try { Process.Start(new ProcessStartInfo(tool.Path) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Cheat Engine tidak ditemukan. Pasang dari cheatengine.org (hati-hati: tolak tawaran software tambahan saat instalasi).", ex);
        }
    }

    public static string? FindCheatEngine()
    {
        if (!OperatingSystem.IsWindows()) return null;
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "Cheat Engine*").OrderByDescending(d => d))
            {
                foreach (var exe in new[] { "cheatengine-x86_64.exe", "cheatengine-x86_64-SSE4-AVX2.exe", "Cheat Engine.exe" })
                {
                    var p = Path.Combine(dir, exe);
                    if (File.Exists(p)) return p;
                }
            }
        }
        return null;
    }
}
