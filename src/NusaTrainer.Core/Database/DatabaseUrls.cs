namespace NusaTrainer.Core.Database;

public static class DatabaseUrls
{
    /// <summary>
    /// URL unduhan mod boleh relatif terhadap database (mis. "mods/hello.zip") sehingga file mod
    /// bisa di-host bersama database. Bila database online belum diatur, pakai salinan lokal (seed).
    /// </summary>
    public static string ResolveModUrl(string downloadUrl, string? databaseBaseUrl, string localDatabaseRoot)
    {
        if (Uri.TryCreate(downloadUrl, UriKind.Absolute, out var abs) && abs.Scheme is "https" or "http" or "file")
            return abs.AbsoluteUri;

        if (!string.IsNullOrWhiteSpace(databaseBaseUrl) && Uri.TryCreate(EnsureSlash(databaseBaseUrl), UriKind.Absolute, out var b))
            return new Uri(b, downloadUrl).AbsoluteUri;

        return new Uri(DatabaseStore.SafeCombine(localDatabaseRoot, downloadUrl)).AbsoluteUri;
    }

    private static string EnsureSlash(string s) => s.EndsWith('/') ? s : s + "/";
}
