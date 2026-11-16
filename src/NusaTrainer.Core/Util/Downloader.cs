namespace NusaTrainer.Core.Util;

public static class Downloader
{
    /// <summary>Unduh ke file, laporkan progres 0..1, verifikasi SHA-256. File dihapus bila hash salah.</summary>
    public static async Task DownloadVerifiedAsync(HttpClient http, string url, string destination, string expectedSha256,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback && !uri.IsFile))
            throw new InvalidOperationException("URL unduhan harus HTTPS.");

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination))!);
        var tmp = destination + ".part";
        try
        {
            if (uri.IsFile)
            {
                File.Copy(uri.LocalPath, tmp, true);
            }
            else
            {
                using var resp = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                long? total = resp.Content.Headers.ContentLength;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
                var buffer = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0) progress?.Report((double)done / total.Value);
                }
            }

            var actual = await Hashing.Sha256HexAsync(tmp, ct);
            if (!Hashing.HashEquals(actual, expectedSha256))
                throw new InvalidDataException($"SHA-256 file unduhan tidak cocok (diharapkan {expectedSha256}, didapat {actual}). File dibuang.");
            File.Move(tmp, destination, true);
            progress?.Report(1);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
