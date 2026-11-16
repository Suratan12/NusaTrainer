using System.Diagnostics;
using NusaTrainer.Core.Database;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Tests;

/// <summary>
/// Uji ujung-ke-ujung: jalankan DemoGame sungguhan, hubungkan trainer, dan pastikan cheat dari
/// database/games/demogame.json benar-benar mengubah memori proses lain.
/// </summary>
public class DemoGameIntegrationTests
{
    [Fact]
    public async Task Cheats_from_database_modify_real_process()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;

        var demoDll = Path.Combine(Path.GetDirectoryName(ValidatorAndMiscTests.FindRepoFile("tools"))!,
            "tools", "DemoGame", "bin", "Debug", "net8.0", "DemoGame.dll");
        Assert.True(File.Exists(demoDll), $"Bangun DemoGame dulu: {demoDll}");

        var game = new DatabaseStore(ValidatorAndMiscTests.FindRepoFile("database")).LoadGames().Single(g => g.Id == "demogame");
        using var proc = Process.Start(new ProcessStartInfo("dotnet", $"\"{demoDll}\" --quiet")
        {
            RedirectStandardOutput = true, UseShellExecute = false,
        })!;
        try
        {
            await Task.Delay(1500); // tunggu game inisialisasi
            using var session = new TrainerSession(game, ProcessMemoryFactory.Open(proc.Id), TimeSpan.FromMilliseconds(50));

            double goldBefore = session.ReadValue("add-gold");
            session.Press("add-gold");
            Assert.True(session.ReadValue("add-gold") >= goldBefore + 1000);

            session.SetToggle("inf-health", true);
            await Task.Delay(1600); // game mengurangi nyawa tiap 500 ms
            Assert.Equal(100, session.ReadValue("inf-health"));

            session.SetToggle("game-speed", true);
            session.SetSliderValue("game-speed", 3.5);
            await Task.Delay(200);
            Assert.Equal(3.5, session.ReadValue("game-speed"));

            session.SetToggle("inf-health", false);
            await Task.Delay(1600);
            Assert.True(session.ReadValue("inf-health") < 100);
        }
        finally
        {
            proc.Kill();
        }
    }
}
