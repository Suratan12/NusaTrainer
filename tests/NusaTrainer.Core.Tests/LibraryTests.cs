using NusaTrainer.Core.CheatTables;
using NusaTrainer.Core.Games;
using NusaTrainer.Core.Library;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Trainer;

namespace NusaTrainer.Core.Tests;

public class InstalledGamesScannerTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    private string MakeGame(string dir, params string[] exes)
    {
        Directory.CreateDirectory(dir);
        foreach (var e in exes)
        {
            var p = Path.Combine(dir, e);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllBytes(p, new byte[e.Contains("Shipping") ? 5000 : 100]);
        }
        return dir;
    }

    [Fact]
    public void Scans_steam_libraries_and_skips_runtimes()
    {
        var steam = _tmp.Combine("Steam");
        var lib2 = _tmp.Combine("D", "SteamLibrary");
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        Directory.CreateDirectory(Path.Combine(lib2, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n \"0\" {{ \"path\" \"{steam.Replace("\\", "\\\\")}\" }}\n \"1\" {{ \"path\" \"{lib2.Replace("\\", "\\\\")}\" }}\n}}");

        File.WriteAllText(Path.Combine(lib2, "steamapps", "appmanifest_292030.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"292030\"\n\t\"name\"\t\t\"The Witcher 3\"\n\t\"installdir\"\t\t\"The Witcher 3\"\n}");
        MakeGame(Path.Combine(lib2, "steamapps", "common", "The Witcher 3"), "bin/x64/witcher3.exe", "unins000.exe", "_CommonRedist/vc_redist.x64.exe");

        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_228980.acf"),
            "\"AppState\" { \"appid\" \"228980\" \"name\" \"Steamworks Common Redistributables\" \"installdir\" \"Steamworks Shared\" }");
        MakeGame(Path.Combine(steam, "steamapps", "common", "Steamworks Shared"), "x.exe");

        var games = InstalledGamesScanner.ScanSteam(steam);
        var w3 = Assert.Single(games);
        Assert.Equal("292030", w3.SourceId);
        Assert.Equal("steam-292030", w3.Key);
        Assert.Equal(["witcher3.exe"], w3.Executables);
    }

    [Fact]
    public void Scans_epic_manifests_and_detects_anticheat()
    {
        var manifests = _tmp.Combine("Manifests");
        Directory.CreateDirectory(manifests);
        var gameDir = MakeGame(_tmp.Combine("Epic", "Hades"), "x64/Hades.exe");
        var ueDir = MakeGame(_tmp.Combine("Epic", "UE_5.3"), "Engine/Binaries/Win64/UnrealEditor.exe");
        var acDir = MakeGame(_tmp.Combine("Epic", "Shooter"), "Shooter-Win64-Shipping.exe", "EasyAntiCheat/EasyAntiCheat_EOS_Setup.exe");
        File.WriteAllText(Path.Combine(acDir, "start_protected_game.exe"), "x");

        void Item(string file, string app, string name, string dir, string exe) =>
            File.WriteAllText(Path.Combine(manifests, file), System.Text.Json.JsonSerializer.Serialize(new
            {
                AppName = app, DisplayName = name, InstallLocation = dir, LaunchExecutable = exe, bIsApplication = true,
                AppCategories = new[] { "public", "games" },
            }));
        Item("a.item", "Min", "Hades", gameDir, "x64/Hades.exe");
        Item("b.item", "UE_5.3", "Unreal Engine", ueDir, "Engine/Binaries/Win64/UnrealEditor.exe");
        Item("c.item", "Shoot", "Shooter", acDir, "Shooter-Win64-Shipping.exe");
        File.WriteAllText(Path.Combine(manifests, "broken.item"), "{ not json");

        var games = InstalledGamesScanner.ScanEpic(manifests).OrderBy(g => g.Name).ToList();
        Assert.Equal(["Hades", "Shooter"], games.Select(g => g.Name));
        Assert.Equal("Hades.exe", games[0].Executables[0]);
        Assert.False(games[0].HasAntiCheat);
        Assert.True(games[1].HasAntiCheat);
    }

    [Fact]
    public void Matches_by_store_id_then_by_executable()
    {
        var defs = new List<GameDefinition>
        {
            new() { Id = "w3", Name = "W3", SteamAppId = 292030, ProcessNames = ["witcher3"] },
            new() { Id = "hades", Name = "Hades", ProcessNames = ["Hades.exe"] },
        };
        var steamGame = new InstalledGame(GameSource.Steam, "292030", "The Witcher 3", "/x", ["other.exe"], false);
        var epicGame = new InstalledGame(GameSource.Epic, "Min", "Hades", "/y", ["Hades.exe"], false);
        var unknown = new InstalledGame(GameSource.Gog, "1", "Unknown", "/z", ["foo.exe"], false);
        Assert.Equal("w3", LibraryMatcher.Match(steamGame, defs)?.Id);
        Assert.Equal("hades", LibraryMatcher.Match(epicGame, defs)?.Id);
        Assert.Null(LibraryMatcher.Match(unknown, defs));

        var synth = LibraryMatcher.Synthesize(unknown);
        Assert.Equal("gog-1", synth.Id);
        Assert.Equal(["foo"], synth.ProcessNames);
    }

    [Fact]
    public void Manual_game_from_exe()
    {
        var dir = MakeGame(_tmp.Combine("MyGame"), "Game.exe", "Launcher.exe");
        var g = InstalledGamesScanner.FromExecutable(Path.Combine(dir, "Game.exe"), "Game Saya");
        Assert.Equal(GameSource.Manual, g.Source);
        Assert.Equal("Game.exe", g.Executables[0]);
        Assert.StartsWith("manual-", g.Key);
    }
}

public class CheatTableImporterTests
{
    private const string Table = """
        <?xml version="1.0" encoding="utf-8"?>
        <CheatTable CheatEngineTableVersion="45">
          <CheatEntries>
            <CheatEntry>
              <ID>1</ID>
              <Description>"Player"</Description>
              <GroupHeader>1</GroupHeader>
              <CheatEntries>
                <CheatEntry>
                  <ID>2</ID>
                  <Description>"Health"</Description>
                  <VariableType>Float</VariableType>
                  <Address>"Game-Win64-Shipping.exe"+04A1B2C0</Address>
                  <Offsets>
                    <Offset>18</Offset>
                    <Offset>4C8</Offset>
                  </Offsets>
                  <Hotkeys>
                    <Hotkey>
                      <Action>Toggle Activation</Action>
                      <Keys><Key>17</Key><Key>97</Key></Keys>
                      <ID>0</ID>
                    </Hotkey>
                  </Hotkeys>
                </CheatEntry>
                <CheatEntry>
                  <ID>3</ID>
                  <Description>"Gold"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>Game.exe+1234</Address>
                </CheatEntry>
                <CheatEntry>
                  <ID>4</ID>
                  <Description>"Name"</Description>
                  <VariableType>String</VariableType>
                  <Address>Game.exe+2000</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatEntry>
            <CheatEntry>
              <ID>5</ID>
              <Description>"Infinite Ammo"</Description>
              <VariableType>Auto Assembler Script</VariableType>
              <AssemblerScript>[ENABLE] aobscanmodule(x,Game.exe,89 08) ...</AssemblerScript>
              <CheatEntries>
                <CheatEntry>
                  <ID>6</ID>
                  <Description>"Ammo pointer"</Description>
                  <VariableType>4 Bytes</VariableType>
                  <Address>pAmmo</Address>
                </CheatEntry>
              </CheatEntries>
            </CheatEntry>
          </CheatEntries>
        </CheatTable>
        """;

    [Fact]
    public void Imports_pointer_entries_counts_scripts_and_skips_unsupported()
    {
        var r = CheatTableImporter.Import(Table);
        Assert.Equal(2, r.Cheats.Count);
        Assert.Equal(1, r.ScriptEntries);
        Assert.Equal(2, r.UnsupportedEntries); // String + simbol "pAmmo"

        var hp = r.Cheats[0];
        Assert.Equal("Health", hp.Name);
        Assert.Equal("Player", hp.Category);
        Assert.Equal(CheatKind.Number, hp.Type);
        Assert.Equal(DataType.Float, hp.ValueType);
        Assert.Equal("Game-Win64-Shipping.exe", hp.Address!.Module);
        Assert.Equal("0x04A1B2C0", hp.Address.Offset);
        Assert.Equal(["0x4C8", "0x18"], hp.Address.Pointers); // urutan dibalik dari XML CE
        Assert.Equal("Ctrl+Num1", hp.Hotkey);

        Assert.Equal("Game.exe", r.Cheats[1].Address!.Module);
        Assert.Null(r.Cheats[1].Address!.Pointers);
    }

    [Fact]
    public void Imported_cheats_pass_validation()
    {
        var r = CheatTableImporter.Import(Table);
        var g = new GameDefinition { Id = "x", Name = "X", ProcessNames = ["Game"], Cheats = r.Cheats };
        Assert.Empty(Database.GameDefinitionValidator.Validate(g));
    }

    [Fact]
    public void Rejects_non_table() => Assert.Throws<FormatException>(() => CheatTableImporter.Import("hello"));

    [Fact]
    public void Imported_number_cheat_works_against_memory()
    {
        var mem = new FakeMemory();
        // [game.exe+0x100] -> Base+0x2000 ; nilai float di Base+0x2000+0x10
        mem.PutLong(0x100, (long)(FakeMemory.Base + 0x2000));
        BitConverter.GetBytes(25f).CopyTo(mem.Data, 0x2010);
        var ct = """
            <CheatTable><CheatEntries><CheatEntry><ID>1</ID><Description>"HP"</Description><VariableType>Float</VariableType>
            <Address>"game.exe"+100</Address><Offsets><Offset>10</Offset></Offsets></CheatEntry></CheatEntries></CheatTable>
            """;
        var cheat = CheatTableImporter.Import(ct).Cheats.Single();
        var game = new GameDefinition { Id = "g", Name = "G", ProcessNames = ["game"], Cheats = [cheat] };
        using var s = new TrainerSession(game, mem, TimeSpan.FromHours(1));
        Assert.Equal(25, s.ReadValue(cheat.Id));
        s.SetNumber(cheat.Id, 999);
        Assert.Equal(999f, mem.GetFloat(0x2010));
        s.SetToggle(cheat.Id, true);           // kunci
        BitConverter.GetBytes(1f).CopyTo(mem.Data, 0x2010);
        s.Tick();
        Assert.Equal(999f, mem.GetFloat(0x2010));
    }
}

public class UserLibraryTests : IDisposable
{
    private readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Fact]
    public void Roundtrips_tools_and_imported_cheats()
    {
        var path = _tmp.Combine("library.json");
        var lib = new UserLibrary();
        lib.AddTool("steam-1", new ExternalTool { Kind = ExternalToolKind.Trainer, Name = "T", Path = "C:/t.exe", AutoLaunch = true });
        lib.AddImported("steam-1", [new CheatDefinition { Id = "ct-hp", Name = "HP" }]);
        lib.AddImported("steam-1", [new CheatDefinition { Id = "ct-hp", Name = "HP 2" }]);
        lib.Save(path);

        var loaded = UserLibrary.Load(path);
        Assert.True(loaded.ToolsFor(["db-id", "steam-1"]).Single().AutoLaunch);
        Assert.Equal(["ct-hp", "ct-hp-2"], loaded.ImportedFor(["steam-1"]).Select(c => c.Id));
        Assert.True(loaded.RemoveTool(loaded.ToolsFor(["steam-1"])[0].Id));
    }

    [Fact]
    public void Corrupt_file_starts_empty()
    {
        var path = _tmp.Combine("library.json");
        File.WriteAllText(path, "{oops");
        Assert.Empty(UserLibrary.Load(path).Tools);
    }
}
