using NusaTrainer.Core.Database;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nusa-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class DatabaseUpdaterTests : IDisposable
{
    private readonly TempDir _remote = new();
    private readonly TempDir _local = new();
    private readonly (string priv, string pub) _keys = SignatureVerifier.GenerateKeyPair();
    private static readonly HttpClient Http = new();

    public void Dispose() { _remote.Dispose(); _local.Dispose(); }

    private static GameDefinition Game(string id, int value = 100) => new()
    {
        Id = id, Name = id.ToUpperInvariant(), ProcessNames = [id],
        Cheats = [new CheatDefinition { Id = "hp", Name = "HP", Address = new AddressSpec { Module = id + ".exe", Offset = "0x10" }, Value = value }],
    };

    /// <summary>Tulis database "server" lalu tandatangani index-nya.</summary>
    private DatabaseIndex Publish(string? signWith = null, params GameDefinition[] games)
    {
        Directory.CreateDirectory(_remote.Combine("games"));
        var index = new DatabaseIndex { Updated = DateTimeOffset.UtcNow, App = new AppReleaseInfo { Version = "9.0.0", DownloadUrl = "https://x/y.zip", Sha256 = new string('a', 64) } };
        foreach (var g in games)
        {
            var bytes = Json.SerializeToUtf8(g);
            File.WriteAllBytes(_remote.Combine("games", g.Id + ".json"), bytes);
            index.Games.Add(new IndexEntry { Id = g.Id, Name = g.Name, File = $"games/{g.Id}.json", Sha256 = Hashing.Sha256Hex(bytes) });
        }
        var indexBytes = Json.SerializeToUtf8(index);
        File.WriteAllBytes(_remote.Combine("index.json"), indexBytes);
        File.WriteAllText(_remote.Combine("index.json.sig"), SignatureVerifier.Sign(indexBytes, signWith ?? _keys.priv));
        return index;
    }

    private DatabaseUpdater Updater(string? pub = null, bool require = true, string appVersion = "1.0.0") =>
        new(Http, new DatabaseStore(_local.Path), new Uri(_remote.Path + "/").AbsoluteUri, pub ?? _keys.pub, require, appVersion);

    [Fact]
    public async Task First_update_downloads_everything_and_games_load()
    {
        Publish(null, Game("alpha"), Game("beta"));
        var r = await Updater().UpdateAsync();
        Assert.Equal(2, r.Added);
        Assert.True(r.AppUpdateAvailable);
        var games = new DatabaseStore(_local.Path).LoadGames();
        Assert.Equal(["ALPHA", "BETA"], games.Select(g => g.Name));
    }

    [Fact]
    public async Task Second_update_only_downloads_changes_and_removes_deleted()
    {
        Publish(null, Game("alpha"), Game("beta"));
        await Updater().UpdateAsync();

        Publish(null, Game("alpha", 999), Game("gamma"));
        File.Delete(_remote.Combine("games", "beta.json"));
        var r = await Updater().UpdateAsync();
        Assert.Equal((1, 1, 1), (r.Added, r.Updated, r.Removed));

        var games = new DatabaseStore(_local.Path).LoadGames();
        Assert.Equal(999, games.Single(g => g.Id == "alpha").Cheats[0].Value);
        Assert.DoesNotContain(games, g => g.Id == "beta");

        var again = await Updater().UpdateAsync();
        Assert.False(again.Changed);
    }

    [Fact]
    public async Task Rejects_index_signed_by_other_key()
    {
        var attacker = SignatureVerifier.GenerateKeyPair();
        Publish(attacker.privatePem, Game("evil"));
        var ex = await Assert.ThrowsAsync<DatabaseUpdateException>(() => Updater().UpdateAsync());
        Assert.Contains("Tanda tangan", ex.Message);
        Assert.False(File.Exists(_local.Combine("index.json")));
    }

    [Fact]
    public async Task Rejects_tampered_game_file_and_writes_nothing()
    {
        Publish(null, Game("alpha"), Game("beta"));
        File.WriteAllText(_remote.Combine("games", "beta.json"), "{\"id\":\"beta\"}");
        await Assert.ThrowsAsync<DatabaseUpdateException>(() => Updater().UpdateAsync());
        Assert.False(File.Exists(_local.Combine("games", "alpha.json")));
    }

    [Fact]
    public async Task Rejects_path_traversal_in_index()
    {
        Directory.CreateDirectory(_remote.Combine("games"));
        var index = new DatabaseIndex { Games = [new IndexEntry { Id = "x", File = "../../evil.json", Sha256 = new string('0', 64) }] };
        var bytes = Json.SerializeToUtf8(index);
        File.WriteAllBytes(_remote.Combine("index.json"), bytes);
        File.WriteAllText(_remote.Combine("index.json.sig"), SignatureVerifier.Sign(bytes, _keys.priv));
        await Assert.ThrowsAnyAsync<Exception>(() => Updater().UpdateAsync());
    }

    [Fact]
    public async Task Rejects_online_multiplayer_definition()
    {
        var g = Game("online");
        g.OnlineMultiplayer = true;
        Publish(null, g);
        var ex = await Assert.ThrowsAsync<DatabaseUpdateException>(() => Updater().UpdateAsync());
        Assert.Contains("multiplayer", ex.Message);
    }

    [Fact]
    public async Task Too_old_app_does_not_apply_database()
    {
        Publish(null, Game("alpha"));
        var idx = Json.Deserialize<DatabaseIndex>(File.ReadAllBytes(_remote.Combine("index.json")));
        idx.MinAppVersion = "2.0.0";
        var bytes = Json.SerializeToUtf8(idx);
        File.WriteAllBytes(_remote.Combine("index.json"), bytes);
        File.WriteAllText(_remote.Combine("index.json.sig"), SignatureVerifier.Sign(bytes, _keys.priv));
        var r = await Updater(appVersion: "1.5.0").UpdateAsync();
        Assert.True(r.AppTooOld);
        Assert.False(File.Exists(_local.Combine("index.json")));
    }

    [Fact]
    public void Requires_public_key_when_signature_required() =>
        Assert.Throws<DatabaseUpdateException>(() => new DatabaseUpdater(Http, new DatabaseStore(_local.Path), "https://example.com/db/", null, true, "1.0.0"));

    [Fact]
    public void Requires_https() =>
        Assert.Throws<DatabaseUpdateException>(() => new DatabaseUpdater(Http, new DatabaseStore(_local.Path), "http://example.com/db/", _keys.pub, true, "1.0.0"));

    [Fact]
    public void Seed_copies_bundled_database_only_when_newer()
    {
        Publish(null, Game("alpha"));
        var store = new DatabaseStore(_local.Path);
        Assert.True(store.SeedFrom(_remote.Path));
        Assert.Single(store.LoadGames());
        Assert.False(store.SeedFrom(_remote.Path));
    }
}

public class ValidatorAndMiscTests
{
    [Fact]
    public void Repository_database_is_valid()
    {
        var root = FindRepoFile("database");
        var games = new DatabaseStore(root).LoadGames(w => Assert.Fail(w));
        Assert.Contains(games, g => g.Id == "demogame");
    }

    [Fact]
    public void Validator_catches_common_mistakes()
    {
        var g = new GameDefinition
        {
            Id = "Bad Id", Name = "", ProcessNames = [],
            Cheats =
            [
                new CheatDefinition { Id = "a", Name = "A" },
                new CheatDefinition { Id = "a", Name = "B", Type = CheatKind.Slider, Min = 5, Max = 1, Address = new AddressSpec { Pattern = "ZZ" } },
            ],
            Mods = [new ModDefinition { Id = "m", Name = "M", DownloadUrl = "http://insecure/x.zip", Sha256 = "abc", InstallPath = "../.." }],
        };
        var errors = GameDefinitionValidator.Validate(g);
        Assert.Contains(errors, e => e.Contains("id 'Bad Id'"));
        Assert.Contains(errors, e => e.Contains("duplikat"));
        Assert.Contains(errors, e => e.Contains("max harus"));
        Assert.Contains(errors, e => e.Contains("pattern"));
        Assert.Contains(errors, e => e.Contains("HTTPS"));
        Assert.Contains(errors, e => e.Contains("sha256"));
        Assert.Contains(errors, e => e.Contains("installPath"));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1", -1)]
    [InlineData("1.10.0", "1.9.9", 1)]
    [InlineData("v2.0", "2.0.0", 0)]
    [InlineData("1.2.0-beta", "1.2.0", 0)]
    public void SemVer_compare(string a, string b, int sign) => Assert.Equal(sign, Math.Sign(SemVer.Compare(a, b)));

    [Fact]
    public void Relative_mod_url_resolves_against_database_url()
    {
        Assert.Equal("https://u.github.io/NusaTrainer/mods/a.zip",
            DatabaseUrls.ResolveModUrl("mods/a.zip", "https://u.github.io/NusaTrainer", "/tmp"));
        Assert.StartsWith("file://", DatabaseUrls.ResolveModUrl("mods/a.zip", null, Path.GetTempPath()));
    }

    internal static string FindRepoFile(string name)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, name);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new FileNotFoundException(name);
    }
}
