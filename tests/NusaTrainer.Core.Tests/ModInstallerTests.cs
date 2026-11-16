using System.IO.Compression;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Mods;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Tests;

public class ModInstallerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _gameDir;
    private readonly ModInstaller _installer;

    public ModInstallerTests()
    {
        _gameDir = _tmp.Combine("Game");
        Directory.CreateDirectory(Path.Combine(_gameDir, "data"));
        File.WriteAllText(Path.Combine(_gameDir, "data", "textures.pak"), "ASLI");
        _installer = new ModInstaller(new HttpClient(), _tmp.Combine("state"));
    }

    public void Dispose() => _tmp.Dispose();

    private ModDefinition MakeMod(string id, params (string path, string content)[] files)
    {
        var zip = _tmp.Combine($"{id}-{Guid.NewGuid():N}.zip");
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            foreach (var (p, c) in files)
            {
                using var w = new StreamWriter(z.CreateEntry(p).Open());
                w.Write(c);
            }
        }
        return new ModDefinition
        {
            Id = id, Name = id, DownloadUrl = new Uri(zip).AbsoluteUri,
            Sha256 = Hashing.Sha256HexAsync(zip).GetAwaiter().GetResult(),
        };
    }

    [Fact]
    public async Task Install_backs_up_and_uninstall_restores()
    {
        var mod = MakeMod("hd", ("data/textures.pak", "HD"), ("plugins/new/readme.txt", "baru"));
        await _installer.InstallAsync("g", mod, _gameDir);

        Assert.Equal("HD", File.ReadAllText(Path.Combine(_gameDir, "data", "textures.pak")));
        Assert.True(File.Exists(Path.Combine(_gameDir, "plugins", "new", "readme.txt")));
        Assert.NotNull(_installer.GetInstalled("g", "hd"));

        _installer.Uninstall("g", "hd");
        Assert.Equal("ASLI", File.ReadAllText(Path.Combine(_gameDir, "data", "textures.pak")));
        Assert.False(Directory.Exists(Path.Combine(_gameDir, "plugins")));
        Assert.Null(_installer.GetInstalled("g", "hd"));
    }

    [Fact]
    public async Task Rejects_wrong_hash()
    {
        var mod = MakeMod("bad", ("a.txt", "x"));
        mod.Sha256 = new string('0', 64);
        await Assert.ThrowsAsync<InvalidDataException>(() => _installer.InstallAsync("g", mod, _gameDir));
        Assert.False(File.Exists(Path.Combine(_gameDir, "a.txt")));
    }

    [Fact]
    public async Task Rejects_zip_slip()
    {
        var mod = MakeMod("slip", ("../../escape.txt", "x"));
        await Assert.ThrowsAsync<ModInstallException>(() => _installer.InstallAsync("g", mod, _gameDir));
        Assert.False(File.Exists(_tmp.Combine("escape.txt")));
    }

    [Fact]
    public async Task Detects_conflicts_between_mods()
    {
        await _installer.InstallAsync("g", MakeMod("one", ("shared.dll", "1")), _gameDir);
        var ex = await Assert.ThrowsAsync<ModInstallException>(() =>
            _installer.InstallAsync("g", MakeMod("two", ("shared.dll", "2")), _gameDir));
        Assert.Contains("one", ex.Message);
    }

    [Fact]
    public async Task Reinstall_upgrades_cleanly()
    {
        var v1 = MakeMod("m", ("data/textures.pak", "V1"));
        await _installer.InstallAsync("g", v1, _gameDir);
        var v2 = MakeMod("m", ("data/textures.pak", "V2"));
        await _installer.InstallAsync("g", v2, _gameDir);
        Assert.Equal("V2", File.ReadAllText(Path.Combine(_gameDir, "data", "textures.pak")));
        _installer.Uninstall("g", "m");
        Assert.Equal("ASLI", File.ReadAllText(Path.Combine(_gameDir, "data", "textures.pak")));
    }
}
