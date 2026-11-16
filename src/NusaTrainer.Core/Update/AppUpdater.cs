using System.Diagnostics;
using System.IO.Compression;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Update;

/// <summary>
/// Self-update aplikasi: unduh zip rilis (hash diverifikasi; hash ada di index.json yang ditandatangani),
/// ekstrak ke folder staging, lalu jalankan skrip kecil yang menunggu aplikasi tertutup, menyalin file baru,
/// dan membuka ulang aplikasi.
/// </summary>
public sealed class AppUpdater(HttpClient http)
{
    public async Task<string> DownloadAndStageAsync(AppReleaseInfo release, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var root = Path.Combine(AppPaths.UpdatesDir, release.Version);
        var zip = Path.Combine(root, "release.zip");
        var staging = Path.Combine(root, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);

        await Downloader.DownloadVerifiedAsync(http, release.DownloadUrl, zip, release.Sha256, progress, ct);

        Directory.CreateDirectory(staging);
        var stagingFull = Path.GetFullPath(staging) + Path.DirectorySeparatorChar;
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var e in archive.Entries)
            {
                var dest = Path.GetFullPath(Path.Combine(staging, e.FullName));
                if (!dest.StartsWith(stagingFull, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Arsip update berisi path berbahaya.");
                if (string.IsNullOrEmpty(e.Name)) { Directory.CreateDirectory(dest); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
            }
        }

        // Zip rilis boleh berisi satu folder induk; ambil isinya.
        var dirs = Directory.GetDirectories(staging);
        if (Directory.GetFiles(staging).Length == 0 && dirs.Length == 1) staging = dirs[0];
        if (!File.Exists(Path.Combine(staging, "NusaTrainer.exe")) && OperatingSystem.IsWindows())
            throw new InvalidDataException("Paket update tidak berisi NusaTrainer.exe.");
        return staging;
    }

    /// <summary>Jalankan skrip pemasang lalu aplikasi harus segera keluar.</summary>
    public static void LaunchInstallerAndExit(string stagingDir)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var appDir = AppPaths.AppDir.TrimEnd('\\', '/');
        var exe = Path.Combine(appDir, "NusaTrainer.exe");
        int pid = Environment.ProcessId;
        var script = Path.Combine(Path.GetTempPath(), $"nusatrainer-update-{Guid.NewGuid():N}.cmd");
        File.WriteAllText(script, $"""
            @echo off
            :wait
            tasklist /FI "PID eq {pid}" 2>nul | find "{pid}" >nul && (timeout /t 1 /nobreak >nul & goto wait)
            robocopy "{stagingDir.TrimEnd('\\')}" "{appDir}" /E /NFL /NDL /NJH /NJS /NP /R:5 /W:1 >nul
            start "" "{exe}"
            (goto) 2>nul & del "%~f0"
            """);
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }
}
