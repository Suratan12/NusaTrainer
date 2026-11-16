namespace NusaTrainer.Core.Util;

public static class AtomicFile
{
    /// <summary>Tulis ke file sementara lalu ganti — file tidak pernah setengah tertulis.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(data);
            fs.Flush(true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
