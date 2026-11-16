namespace NusaTrainer.Core.Util;

public static class Log
{
    private static readonly object Lock = new();

    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARN", msg);
    public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex is null ? msg : $"{msg}: {ex}");

    private static void Write(string level, string msg)
    {
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(Path.Combine(AppPaths.LogsDir, $"{DateTime.Now:yyyy-MM-dd}.log"),
                    $"{DateTime.Now:HH:mm:ss} [{level}] {msg}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
