using NusaTrainer.Core.Models;
using NusaTrainer.Core.Util;

namespace NusaTrainer.Core.Database;

public sealed record UpdateResult(
    int Added,
    int Updated,
    int Removed,
    DatabaseIndex Index,
    bool AppUpdateAvailable,
    bool AppTooOld)
{
    public bool Changed => Added + Updated + Removed > 0;
}

public sealed class DatabaseUpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Mengunduh index.json (+ .sig) dari server, memverifikasi tanda tangan, lalu mengunduh
/// hanya file game yang berubah (dibandingkan via SHA-256) dan memverifikasi hash tiap file.
/// </summary>
public sealed class DatabaseUpdater
{
    private readonly HttpClient _http;
    private readonly DatabaseStore _store;
    private readonly Uri _baseUri;
    private readonly string? _publicKeyPem;
    private readonly bool _requireSignature;
    private readonly string _appVersion;

    public DatabaseUpdater(HttpClient http, DatabaseStore store, string baseUrl, string? publicKeyPem,
        bool requireSignature, string appVersion)
    {
        _http = http;
        _store = store;
        _publicKeyPem = string.IsNullOrWhiteSpace(publicKeyPem) ? null : publicKeyPem;
        _requireSignature = requireSignature;
        _appVersion = appVersion;

        if (!baseUrl.EndsWith('/')) baseUrl += "/";
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new DatabaseUpdateException($"URL database tidak valid: {baseUrl}");
        bool isLocal = uri.IsLoopback;
        if (uri.Scheme != Uri.UriSchemeHttps && !(isLocal && uri.Scheme == Uri.UriSchemeHttp) && !uri.IsFile)
            throw new DatabaseUpdateException("URL database harus HTTPS.");
        if (_requireSignature && _publicKeyPem is null)
            throw new DatabaseUpdateException("Public key database belum diatur (Pengaturan → Public key), atau matikan 'Wajib tanda tangan'.");
        _baseUri = uri;
    }

    public async Task<UpdateResult> UpdateAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        progress?.Report("Memeriksa pembaruan database…");
        var indexBytes = await FetchAsync("index.json", ct);

        string? sig = null;
        try { sig = System.Text.Encoding.UTF8.GetString(await FetchAsync("index.json.sig", ct)); }
        catch (DatabaseUpdateException) when (!_requireSignature) { }

        if (_publicKeyPem is not null && (sig is not null || _requireSignature))
        {
            if (sig is null || !SignatureVerifier.Verify(indexBytes, sig, _publicKeyPem))
                throw new DatabaseUpdateException("Tanda tangan database TIDAK VALID. Pembaruan dibatalkan demi keamanan.");
        }

        DatabaseIndex remote;
        try { remote = Json.Deserialize<DatabaseIndex>(indexBytes); }
        catch (Exception ex) { throw new DatabaseUpdateException("index.json rusak.", ex); }

        if (remote.Schema > DatabaseIndex.CurrentSchema)
            return new UpdateResult(0, 0, 0, remote, remote.App is not null, AppTooOld: true);
        bool appTooOld = remote.MinAppVersion is not null && SemVer.Compare(_appVersion, remote.MinAppVersion) < 0;
        if (appTooOld)
            return new UpdateResult(0, 0, 0, remote, remote.App is not null, AppTooOld: true);

        var local = _store.LoadIndex();
        var localById = (local?.Games ?? []).ToDictionary(g => g.Id, StringComparer.OrdinalIgnoreCase);

        // Unduh & verifikasi semua file yang berubah ke memori dulu; tulis ke disk hanya bila semua sukses.
        var pending = new List<(IndexEntry entry, byte[] bytes)>();
        int added = 0, updated = 0;
        foreach (var entry in remote.Games)
        {
            ct.ThrowIfCancellationRequested();
            var localPath = _store.GamePath(entry); // juga memvalidasi path
            bool exists = localById.TryGetValue(entry.Id, out var localEntry) && File.Exists(localPath);
            if (exists && Hashing.HashEquals(localEntry!.Sha256, entry.Sha256)
                && Hashing.HashEquals(await Hashing.Sha256HexAsync(localPath, ct), entry.Sha256))
                continue;

            progress?.Report($"Mengunduh {entry.Name}…");
            var bytes = await FetchAsync(entry.File, ct);
            if (!Hashing.HashEquals(Hashing.Sha256Hex(bytes), entry.Sha256))
                throw new DatabaseUpdateException($"Hash file {entry.File} tidak cocok dengan index.");

            GameDefinition def;
            try { def = Json.Deserialize<GameDefinition>(bytes); }
            catch (Exception ex) { throw new DatabaseUpdateException($"{entry.File} bukan JSON valid.", ex); }
            if (!string.Equals(def.Id, entry.Id, StringComparison.OrdinalIgnoreCase))
                throw new DatabaseUpdateException($"{entry.File}: id '{def.Id}' tidak sama dengan index '{entry.Id}'.");
            var errors = GameDefinitionValidator.Validate(def);
            if (errors.Count > 0)
                throw new DatabaseUpdateException($"{entry.File} tidak valid: {string.Join("; ", errors)}");

            pending.Add((entry, bytes));
            if (exists) updated++; else added++;
        }

        foreach (var (entry, bytes) in pending)
            AtomicFile.WriteAllBytes(_store.GamePath(entry), bytes);

        int removed = 0;
        var remoteIds = new HashSet<string>(remote.Games.Select(g => g.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var old in localById.Values.Where(g => !remoteIds.Contains(g.Id)))
        {
            try
            {
                var p = _store.GamePath(old);
                if (File.Exists(p)) File.Delete(p);
                removed++;
            }
            catch { /* abaikan path aneh di index lokal lama */ }
        }

        AtomicFile.WriteAllBytes(_store.IndexPath, indexBytes);
        if (sig is not null) AtomicFile.WriteAllBytes(_store.IndexPath + ".sig", System.Text.Encoding.UTF8.GetBytes(sig));

        bool appUpdate = remote.App is not null && SemVer.Compare(remote.App.Version, _appVersion) > 0;
        progress?.Report(added + updated + removed > 0
            ? $"Database diperbarui: {added} baru, {updated} diperbarui, {removed} dihapus."
            : "Database sudah versi terbaru.");
        return new UpdateResult(added, updated, removed, remote, appUpdate, false);
    }

    private async Task<byte[]> FetchAsync(string relative, CancellationToken ct)
    {
        var uri = new Uri(_baseUri, relative);
        if (!uri.AbsoluteUri.StartsWith(_baseUri.AbsoluteUri, StringComparison.Ordinal))
            throw new DatabaseUpdateException($"Path '{relative}' keluar dari URL database.");
        try
        {
            if (uri.IsFile) return await File.ReadAllBytesAsync(uri.LocalPath, ct);
            using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
                throw new DatabaseUpdateException($"Server mengembalikan {(int)resp.StatusCode} untuk {relative}.");
            var len = resp.Content.Headers.ContentLength;
            if (len > 20 * 1024 * 1024) throw new DatabaseUpdateException($"{relative} terlalu besar.");
            return await resp.Content.ReadAsByteArrayAsync(ct);
        }
        catch (DatabaseUpdateException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            throw new DatabaseUpdateException($"Gagal mengunduh {relative}: {ex.Message}", ex);
        }
    }
}
