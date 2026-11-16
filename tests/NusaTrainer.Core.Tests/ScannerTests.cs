using System.Diagnostics;
using System.IO.Compression;
using NusaTrainer.Core.Database;
using NusaTrainer.Core.Games;
using NusaTrainer.Core.Library;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Scanning;
using NusaTrainer.Core.Trainer;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Tests;

public class ValueScannerTests
{
    [Fact]
    public void Narrows_down_to_the_real_value()
    {
        var mem = new FakeMemory(0x20000);
        mem.PutInt(0x2000, 1500); // uang asli
        mem.PutInt(0x3004, 1500); // kebetulan sama
        mem.PutInt(0x4008, 1500);
        var s = new ValueScanner(mem);
        Assert.Equal(3, s.FirstScan(1500, ScanValueKind.Int32));

        mem.PutInt(0x2000, 1350); // beli sesuatu di game
        Assert.Equal(1, s.NextScan(NextScanMode.Exact, 1350));
        var hit = Assert.Single(s.GetResults());
        Assert.Equal(FakeMemory.Base + 0x2000, hit.Address);
        Assert.Equal(DataType.Int32, hit.Type);
    }

    [Fact]
    public void Auto_mode_finds_float_health_with_rounding_tolerance()
    {
        var mem = new FakeMemory(0x20000);
        BitConverter.GetBytes(87.6f).CopyTo(mem.Data, 0x2100); // game menampilkan "88"
        var s = new ValueScanner(mem);
        s.FirstScan(88, ScanValueKind.Auto);
        Assert.Contains(s.GetResults(), h => h.Address == FakeMemory.Base + 0x2100 && h.Type == DataType.Float);

        BitConverter.GetBytes(60f).CopyTo(mem.Data, 0x2100); // terkena damage
        s.NextScan(NextScanMode.Decreased);
        Assert.Contains(s.GetResults(), h => h.Address == FakeMemory.Base + 0x2100);
        Assert.Equal(1, s.Count);
    }

    [Fact]
    public void Ignores_code_regions()
    {
        var mem = new FakeMemory(0x20000);
        mem.PutInt(0x10, 777); // region kode
        var s = new ValueScanner(mem);
        Assert.Equal(0, s.FirstScan(777, ScanValueKind.Int32));
    }

    [Fact]
    public void Unchanged_and_increased_modes()
    {
        var mem = new FakeMemory(0x20000);
        mem.PutInt(0x2000, 5);
        mem.PutInt(0x2004, 5);
        var s = new ValueScanner(mem);
        s.FirstScan(5, ScanValueKind.Int32);
        mem.PutInt(0x2004, 9);
        s.NextScan(NextScanMode.Increased);
        Assert.Equal(FakeMemory.Base + 0x2004, Assert.Single(s.GetResults()).Address);
        s.NextScan(NextScanMode.Unchanged);
        Assert.Equal(1, s.Count);
    }
}

public class PointerFinderTests
{
    [Fact]
    public void Finds_two_level_pointer_chain_from_static_module_data()
    {
        var mem = new FakeMemory(0x20000) { ModuleSize = 0x2000 }; // game.exe = 0x0..0x2000, sisanya heap
        ulong objA = FakeMemory.Base + 0x8000, objB = FakeMemory.Base + 0xA000;
        mem.PutLong(0x1800, (long)objA);          // static: [game.exe+1800] = A
        mem.PutLong(0x8020, (long)objB);          // A+0x20 = B
        ulong target = objB + 0x44;
        mem.PutInt(0xA044, 1234);

        var paths = PointerFinder.Find(mem, target);
        var best = paths.First();
        Assert.Equal("game.exe", best.Module);
        Assert.Equal(0x1800UL, best.ModuleOffset);
        Assert.Equal([0x20UL, 0x44UL], best.Offsets);
        Assert.Equal(target, new AddressResolver(mem).Resolve(best.ToSpec()));
    }

    [Fact]
    public void Static_value_needs_no_pointer()
    {
        var mem = new FakeMemory(0x20000) { ModuleSize = 0x4000 };
        var paths = PointerFinder.Find(mem, FakeMemory.Base + 0x3000);
        var p = paths.First();
        Assert.Empty(p.Offsets);
        Assert.Equal(0x3000UL, p.ModuleOffset);
    }
}

public class TrainerInstallerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public async Task Downloads_verifies_and_extracts_main_exe()
    {
        var zip = _tmp.Combine("trainer.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var w = z.CreateEntry("Game Trainer/Game Trainer.exe").Open()) w.Write(new byte[5000]);
            using (var w = z.CreateEntry("Game Trainer/readme.txt").Open()) w.Write(new byte[10]);
            using (var w = z.CreateEntry("Game Trainer/unins000.exe").Open()) w.Write(new byte[9000]);
        }
        var link = new ExternalTrainerLink { Name = "T", Homepage = "https://x", Sha256 = await Hashing.Sha256HexAsync(zip) };
        var installer = new TrainerInstaller(new HttpClient(), _tmp.Combine("trainers"));
        var exe = await installer.InstallAsync("steam-1", link, new Uri(zip).AbsoluteUri);
        Assert.Equal("Game Trainer.exe", Path.GetFileName(exe));
        Assert.True(File.Exists(exe));
    }

    [Fact]
    public async Task Rejects_tampered_download()
    {
        var file = _tmp.Combine("trainer.exe");
        File.WriteAllBytes(file, new byte[100]);
        var link = new ExternalTrainerLink { Name = "T", Homepage = "https://x", Sha256 = new string('a', 64) };
        var installer = new TrainerInstaller(new HttpClient(), _tmp.Combine("trainers"));
        await Assert.ThrowsAsync<InvalidDataException>(() => installer.InstallAsync("g", link, new Uri(file).AbsoluteUri));
    }

    [Fact]
    public void Version_matching()
    {
        var link = new ExternalTrainerLink { GameVersions = ["1.5.78"], SteamBuildIds = ["9001"] };
        InstalledGame G(string? v, string? b) => new(GameSource.Steam, "1", "G", "/g", [], false) { Version = v, BuildId = b };
        Assert.Equal(VersionMatch.Match, TrainerInstaller.CheckVersion(link, G("1.5.78.11833", null)));
        Assert.Equal(VersionMatch.Match, TrainerInstaller.CheckVersion(link, G(null, "9001")));
        Assert.Equal(VersionMatch.Mismatch, TrainerInstaller.CheckVersion(link, G("1.4.0", "8000")));
        Assert.Equal(VersionMatch.Unknown, TrainerInstaller.CheckVersion(link, G(null, null)));
        Assert.Equal(VersionMatch.Any, TrainerInstaller.CheckVersion(new ExternalTrainerLink(), G("1", null)));
    }

    [Fact]
    public void Validator_requires_hash_for_download_url()
    {
        var g = new GameDefinition
        {
            Id = "g", Name = "G", ProcessNames = ["g"],
            ExternalTrainers = [new ExternalTrainerLink { Name = "T", Homepage = "https://a", DownloadUrl = "https://a/t.rar" }],
        };
        var errors = GameDefinitionValidator.Validate(g);
        Assert.Contains(errors, e => e.Contains("sha256"));
        Assert.Contains(errors, e => e.Contains(".zip"));
    }
}

/// <summary>Pemindai nilai terhadap proses DemoGame sungguhan.</summary>
public class ScannerIntegrationTests
{
    [Fact]
    public async Task Finds_gold_in_real_process_by_value()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        var demoDll = Path.Combine(Path.GetDirectoryName(ValidatorAndMiscTests.FindRepoFile("tools"))!,
            "tools", "DemoGame", "bin", "Debug", "net8.0", "DemoGame.dll");
        var game = new DatabaseStore(ValidatorAndMiscTests.FindRepoFile("database")).LoadGames().Single(g => g.Id == "demogame");
        using var proc = Process.Start(new ProcessStartInfo("dotnet", $"\"{demoDll}\" --quiet") { RedirectStandardOutput = true })!;
        try
        {
            await Task.Delay(1500);
            using var mem = ProcessMemoryFactory.Open(proc.Id);
            using var session = new TrainerSession(game, ProcessMemoryFactory.Open(proc.Id), TimeSpan.FromHours(1));
            var goldAddr = new AddressResolver(mem).Resolve(game.Cheats.Single(c => c.Id == "add-gold").Address!);

            // Buat nilai unik supaya pemindaian pertama tidak terlalu banyak hasil.
            session.SetNumber("edit-gold", 4242);
            var scanner = new ValueScanner(mem);
            scanner.FirstScan(4242, ScanValueKind.Auto);
            session.SetNumber("edit-gold", 31337); // "nilai di game berubah"
            scanner.NextScan(NextScanMode.Exact, 31337);

            Assert.Contains(scanner.GetResults(), h => h.Address == goldAddr && h.Type == DataType.Int32);
            Assert.True(scanner.Count < 20, $"terlalu banyak hasil: {scanner.Count}");
        }
        finally { proc.Kill(); }
    }
}
