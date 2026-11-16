using System.Net.Http.Headers;
using System.Reflection;
using System.Diagnostics;
using NusaTrainer.Core.Database;
using NusaTrainer.Core.Library;
using NusaTrainer.Core.Mods;
using NusaTrainer.Core.Settings;
using NusaTrainer.Core.Update;
using NusaTrainer.Core.Util;

namespace NusaTrainer.App.Services;

/// <summary>Kumpulan layanan bersama (pengganti DI container sederhana).</summary>
public sealed class AppServices : IDisposable
{
    public string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public HttpClient Http { get; }
    public AppSettings Settings { get; }
    public DatabaseStore Store { get; }
    public ModInstaller Mods { get; }
    public AppUpdater AppUpdater { get; }
    public IHotkeyService Hotkeys { get; }
    public UserLibrary Library { get; }
    public TrainerInstaller Trainers { get; }

    /// <summary>Diisi oleh jendela utama: membuka dialog pemilihan folder.</summary>
    public Func<string, Task<string?>> PickFolderAsync { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>Diisi oleh jendela utama: dialog pilih file (judul, nama filter, pola mis. "*.exe").</summary>
    public Func<string, string, string[], Task<string?>> PickFileAsync { get; set; } = (_, _, _) => Task.FromResult<string?>(null);

    public AppServices()
    {
        Directory.CreateDirectory(AppPaths.DataRoot);
        Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        Http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NusaTrainer", AppVersion));
        Settings = AppSettings.Load(AppPaths.SettingsFile, AppPaths.DefaultSettingsFile);
        Store = new DatabaseStore(AppPaths.DatabaseDir);
        Mods = new ModInstaller(Http, AppPaths.DataRoot);
        AppUpdater = new AppUpdater(Http);
        Hotkeys = OperatingSystem.IsWindows() ? new WindowsHotkeyService() : new NullHotkeyService();
        Library = UserLibrary.Load();
        Trainers = new TrainerInstaller(Http, Path.Combine(AppPaths.DataRoot, "trainers"));
    }

    public void SaveLibrary()
    {
        try { Library.Save(); }
        catch (Exception ex) { Log.Error("Gagal menyimpan library.json", ex); }
    }

    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("https" or "http")) return;
        try { Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
    }

    public void SaveSettings()
    {
        try { Settings.Save(AppPaths.SettingsFile); }
        catch (Exception ex) { Log.Error("Gagal menyimpan pengaturan", ex); }
    }

    public DatabaseUpdater CreateUpdater() =>
        new(Http, Store, Settings.DatabaseUrl, Settings.PublicKeyPem, Settings.RequireSignature, AppVersion);

    public string ResolveModUrl(string downloadUrl) =>
        DatabaseUrls.ResolveModUrl(downloadUrl, Settings.IsDatabaseUrlConfigured ? Settings.DatabaseUrl : null,
            Directory.Exists(AppPaths.SeedDatabaseDir) ? AppPaths.SeedDatabaseDir : AppPaths.DatabaseDir);

    public void Dispose()
    {
        Hotkeys.Dispose();
        Http.Dispose();
    }
}
