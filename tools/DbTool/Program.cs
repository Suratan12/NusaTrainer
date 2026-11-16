// nusa-db — alat pengelola database cheat/mod NusaTrainer.
//
//   nusa-db keygen   --out <folder>                       Buat pasangan kunci tanda tangan (sekali saja)
//   nusa-db validate --db <folder>                        Validasi semua games/*.json
//   nusa-db build    --db <folder> [--key <private.pem>]  Validasi, hitung SHA-256, tulis index.json (+ .sig)
//                    [--out <folder>] [--app-version X --app-url URL --app-sha256 H --app-notes TXT]
//                    (atau tulis info rilis aplikasi di <db>/app-release.json)
//   nusa-db hash     <file>                               Cetak SHA-256 sebuah file (untuk mod .zip)
//   nusa-db import-ct --ct <tabel.CT> --db <folder> --id <id> --name "Nama Game" --process <Game.exe>
//                    [--steam <appid>] [--author <nama>]   Buat/perbarui games/<id>.json dari tabel Cheat Engine
//
// Kunci privat juga bisa diberikan lewat env NUSA_DB_PRIVATE_KEY (isi PEM) — dipakai di GitHub Actions.

using NusaTrainer.Core.CheatTables;
using NusaTrainer.Core.Database;
using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

static string? Opt(string[] args, string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static int Fail(string msg)
{
    Console.Error.WriteLine("ERROR: " + msg);
    return 1;
}

if (args.Length == 0)
{
    Console.WriteLine("Perintah: keygen | validate | build | hash | import-ct  (lihat komentar di Program.cs / docs/DATABASE.md)");
    return 1;
}

switch (args[0])
{
    case "keygen":
    {
        var outDir = Opt(args, "--out") ?? "keys";
        Directory.CreateDirectory(outDir);
        var priv = Path.Combine(outDir, "private.pem");
        if (File.Exists(priv)) return Fail($"{priv} sudah ada — tidak ditimpa.");
        var (privatePem, publicPem) = SignatureVerifier.GenerateKeyPair();
        File.WriteAllText(priv, privatePem);
        File.WriteAllText(Path.Combine(outDir, "public.pem"), publicPem);
        Console.WriteLine($"Kunci dibuat di {outDir}/");
        Console.WriteLine("  private.pem → RAHASIA. Jangan di-commit. Simpan sebagai secret NUSA_DB_PRIVATE_KEY di GitHub.");
        Console.WriteLine("  public.pem  → salin isinya ke src/NusaTrainer.App/appsettings.json (publicKeyPem).");
        Console.WriteLine();
        Console.WriteLine(publicPem);
        return 0;
    }

    case "hash":
    {
        if (args.Length < 2) return Fail("Pakai: nusa-db hash <file>");
        Console.WriteLine(await Hashing.Sha256HexAsync(args[1]));
        return 0;
    }

    case "import-ct":
    {
        var ctPath = Opt(args, "--ct") ?? throw new ArgumentException("--ct wajib");
        var db = Opt(args, "--db") ?? "database";
        var id = Opt(args, "--id") ?? throw new ArgumentException("--id wajib");
        var target = Path.Combine(db, "games", id + ".json");
        var result = CheatTableImporter.Import(await File.ReadAllTextAsync(ctPath));

        GameDefinition game;
        if (File.Exists(target))
        {
            game = Json.Deserialize<GameDefinition>(await File.ReadAllBytesAsync(target));
            game.Cheats.RemoveAll(c => c.Id.StartsWith("ct-", StringComparison.Ordinal)); // ganti hasil impor sebelumnya
            game.Revision++;
        }
        else
        {
            game = new GameDefinition
            {
                Id = id,
                Name = Opt(args, "--name") ?? throw new ArgumentException("--name wajib untuk game baru"),
                ProcessNames = [Opt(args, "--process") ?? throw new ArgumentException("--process wajib untuk game baru")],
                SteamAppId = Opt(args, "--steam") is { } st ? int.Parse(st) : null,
                Author = Opt(args, "--author"),
            };
        }
        game.Cheats.AddRange(result.Cheats);
        var errors = GameDefinitionValidator.Validate(game);
        if (errors.Count > 0) return Fail(string.Join("\n  ", errors));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, Json.SerializeToUtf8(game));
        Console.WriteLine($"✓ {target}: {result.Cheats.Count} cheat diimpor, {result.ScriptEntries} skrip AA dilewati, {result.UnsupportedEntries} entri tidak didukung.");
        Console.WriteLine("  Uji di game, lalu jalankan 'nusa-db build --db " + db + "'. Pastikan Anda punya izin pembuat tabel untuk mendistribusikannya.");
        return 0;
    }

    case "validate":
    case "build":
    {
        var db = Opt(args, "--db") ?? "database";
        var gamesDir = Path.Combine(db, "games");
        if (!Directory.Exists(gamesDir)) return Fail($"Folder {gamesDir} tidak ada.");

        var entries = new List<IndexEntry>();
        int errorCount = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(gamesDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var rel = "games/" + Path.GetFileName(file);
            var bytes = await File.ReadAllBytesAsync(file);
            GameDefinition g;
            try { g = Json.Deserialize<GameDefinition>(bytes); }
            catch (Exception ex) { Console.Error.WriteLine($"✗ {rel}: JSON tidak valid — {ex.Message}"); errorCount++; continue; }

            var errors = GameDefinitionValidator.Validate(g);
            if (Path.GetFileNameWithoutExtension(file) != g.Id) errors.Add($"nama file harus '{g.Id}.json'.");
            if (!seen.Add(g.Id)) errors.Add("id game duplikat.");
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"✗ {rel}");
                foreach (var e in errors) Console.Error.WriteLine("    - " + e);
                errorCount++;
                continue;
            }
            foreach (var m in g.Mods.Where(m => !Uri.IsWellFormedUriString(m.DownloadUrl, UriKind.Absolute)))
            {
                var local = Path.Combine(db, m.DownloadUrl);
                if (!File.Exists(local)) errors.Add($"mod '{m.Id}': file {m.DownloadUrl} tidak ada di database.");
                else if (!Hashing.HashEquals(await Hashing.Sha256HexAsync(local), m.Sha256))
                    errors.Add($"mod '{m.Id}': sha256 tidak cocok — jalankan 'nusa-db hash {m.DownloadUrl}' lalu perbarui.");
            }
            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"✗ {rel}");
                foreach (var e in errors) Console.Error.WriteLine("    - " + e);
                errorCount++;
                continue;
            }
            Console.WriteLine($"✓ {rel}  ({g.Cheats.Count} cheat, {g.Mods.Count} mod)");
            entries.Add(new IndexEntry
            {
                Id = g.Id, Name = g.Name, Revision = g.Revision, File = rel, Sha256 = Hashing.Sha256Hex(bytes),
                CheatCount = g.Cheats.Count, ModCount = g.Mods.Count,
            });
        }
        if (errorCount > 0) return Fail($"{errorCount} file tidak valid.");
        if (args[0] == "validate") { Console.WriteLine("Semua valid."); return 0; }

        var outDir = Opt(args, "--out") ?? db;
        var indexPath = Path.Combine(outDir, "index.json");
        var srcIndexPath = Path.Combine(db, "index.json");

        // Pertahankan info rilis aplikasi & minAppVersion dari index lama, bisa ditimpa lewat opsi.
        DatabaseIndex? previous = null;
        if (File.Exists(srcIndexPath))
        {
            try { previous = Json.Deserialize<DatabaseIndex>(await File.ReadAllBytesAsync(srcIndexPath)); } catch { }
        }
        var index = new DatabaseIndex
        {
            Updated = DateTimeOffset.UtcNow,
            MinAppVersion = Opt(args, "--min-app-version") ?? previous?.MinAppVersion,
            Games = entries,
            App = previous?.App,
        };
        // Info rilis aplikasi bisa ditulis di <db>/app-release.json (lebih mudah daripada opsi CLI).
        var appReleasePath = Path.Combine(db, "app-release.json");
        if (File.Exists(appReleasePath))
            index.App = Json.Deserialize<AppReleaseInfo>(await File.ReadAllBytesAsync(appReleasePath));
        if (Opt(args, "--app-version") is { } av)
        {
            index.App = new AppReleaseInfo
            {
                Version = av,
                DownloadUrl = Opt(args, "--app-url") ?? throw new ArgumentException("--app-url wajib"),
                Sha256 = Opt(args, "--app-sha256") ?? throw new ArgumentException("--app-sha256 wajib"),
                Notes = Opt(args, "--app-notes"),
            };
        }

        // Jangan ubah timestamp bila isi tidak berubah (hindari commit/deploy yang tidak perlu).
        if (previous is not null && previous.MinAppVersion == index.MinAppVersion
            && Json.Serialize(previous.Games) == Json.Serialize(index.Games)
            && Json.Serialize(previous.App) == Json.Serialize(index.App))
            index.Updated = previous.Updated;

        Directory.CreateDirectory(outDir);
        if (Path.GetFullPath(outDir) != Path.GetFullPath(db))
        {
            Directory.CreateDirectory(Path.Combine(outDir, "games"));
            foreach (var e in entries)
                File.Copy(Path.Combine(db, e.File), Path.Combine(outDir, e.File), true);
            // File mod yang di-host bersama database (downloadUrl relatif)
            var modsSrc = Path.Combine(db, "mods");
            if (Directory.Exists(modsSrc))
            {
                foreach (var f in Directory.GetFiles(modsSrc, "*", SearchOption.AllDirectories))
                {
                    var dest = Path.Combine(outDir, Path.GetRelativePath(db, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.Copy(f, dest, true);
                }
            }
        }
        var indexBytes = Json.SerializeToUtf8(index);
        await File.WriteAllBytesAsync(indexPath, indexBytes);
        Console.WriteLine($"→ {indexPath} ({entries.Count} game)");

        var keyPem = Environment.GetEnvironmentVariable("NUSA_DB_PRIVATE_KEY");
        if (Opt(args, "--key") is { } keyPath) keyPem = await File.ReadAllTextAsync(keyPath);
        if (!string.IsNullOrWhiteSpace(keyPem))
        {
            await File.WriteAllTextAsync(indexPath + ".sig", SignatureVerifier.Sign(indexBytes, keyPem));
            Console.WriteLine($"→ {indexPath}.sig (ditandatangani)");
        }
        else
        {
            Console.WriteLine("! Tidak ada kunci privat — index TIDAK ditandatangani.");
        }
        return 0;
    }

    default:
        return Fail($"Perintah tidak dikenal: {args[0]}");
}
