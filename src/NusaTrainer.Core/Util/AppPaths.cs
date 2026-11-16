namespace NusaTrainer.Core.Util;

public static class AppPaths
{
    public static string DataRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NusaTrainer");

    public static string DatabaseDir => Path.Combine(DataRoot, "database");
    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogsDir => Path.Combine(DataRoot, "logs");
    public static string UpdatesDir => Path.Combine(DataRoot, "updates");
    public static string AppDir => AppContext.BaseDirectory;
    public static string SeedDatabaseDir => Path.Combine(AppDir, "seed-database");
    public static string DefaultSettingsFile => Path.Combine(AppDir, "appsettings.json");
}
