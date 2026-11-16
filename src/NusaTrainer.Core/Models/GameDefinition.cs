namespace NusaTrainer.Core.Models;

/// <summary>
/// Satu file definisi game di database (database/games/&lt;id&gt;.json).
/// </summary>
public sealed class GameDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Nomor revisi definisi ini (hanya untuk ditampilkan; update dideteksi lewat SHA-256).</summary>
    public int Revision { get; set; } = 1;
    public string? Description { get; set; }
    public string? Author { get; set; }
    /// <summary>Versi/build game yang didukung, mis. "1.04 (Steam)".</summary>
    public string? SupportedGameVersion { get; set; }
    /// <summary>Nama proses tanpa atau dengan ".exe", mis. ["DemoGame", "DemoGame.exe"].</summary>
    public List<string> ProcessNames { get; set; } = new();
    public int? SteamAppId { get; set; }
    /// <summary>Folder exe relatif terhadap root game (mis. "Binaries/Win64"), untuk menebak root folder dari proses yang berjalan.</summary>
    public string? ExeRelativeDir { get; set; }
    /// <summary>Warna aksen header (hex), mis. "#7C3AED".</summary>
    public string? AccentColor { get; set; }
    /// <summary>Game online/multiplayer kompetitif DITOLAK oleh aplikasi.</summary>
    public bool OnlineMultiplayer { get; set; }
    /// <summary>true = proses 64-bit (default). Ukuran pointer otomatis dideteksi saat attach bila memungkinkan.</summary>
    public bool Is64Bit { get; set; } = true;
    public List<CheatDefinition> Cheats { get; set; } = new();
    public List<ModDefinition> Mods { get; set; } = new();
}

public enum CheatKind
{
    Toggle,
    Slider,
    Button,
}

public enum CheatOperation
{
    /// <summary>Tulis nilai terus-menerus (dikunci) selama aktif.</summary>
    Freeze,
    /// <summary>Tulis nilai sekali.</summary>
    Set,
    /// <summary>Tambahkan nilai ke nilai sekarang (sekali).</summary>
    Add,
}

public enum DataType
{
    Byte,
    Int16,
    Int32,
    Int64,
    Float,
    Double,
}

public sealed class CheatDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Umum";
    public string? Description { get; set; }
    public CheatKind Type { get; set; } = CheatKind.Toggle;

    /// <summary>Hotkey utama (Toggle/Button), mis. "Num1", "Ctrl+F1".</summary>
    public string? Hotkey { get; set; }
    /// <summary>Hotkey naik/turun untuk Slider.</summary>
    public string? HotkeyIncrease { get; set; }
    public string? HotkeyDecrease { get; set; }

    /// <summary>Lokasi nilai di memori (untuk cheat nilai).</summary>
    public AddressSpec? Address { get; set; }
    public DataType ValueType { get; set; } = DataType.Int32;
    public CheatOperation Operation { get; set; } = CheatOperation.Freeze;
    /// <summary>Nilai untuk Toggle/Button.</summary>
    public double Value { get; set; }
    /// <summary>Untuk Toggle+Set: kembalikan nilai asli saat dimatikan.</summary>
    public bool RestoreOnDisable { get; set; }

    // Slider
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public double Step { get; set; } = 1;
    public double Default { get; set; }

    /// <summary>Patch byte kode (alternatif dari cheat nilai). Hanya untuk Toggle.</summary>
    public PatchSpec? Patch { get; set; }
}

/// <summary>
/// Cara menemukan alamat. Dua mode:
/// 1) Pointer statis: Module + Offset, lalu ikuti rantai Pointers (semantik Cheat Engine).
/// 2) AOB scan: cari Pattern (dengan wildcard ??) lalu + PatternOffset; opsional Rip untuk membaca alamat relatif RIP;
///    lalu ikuti rantai Pointers bila ada.
/// </summary>
public sealed class AddressSpec
{
    public string? Module { get; set; }
    /// <summary>Offset hex dari base module, mis. "0x1A2B3C".</summary>
    public string? Offset { get; set; }
    /// <summary>Rantai offset pointer hex, mis. ["0x10", "0x4C8"].</summary>
    public List<string>? Pointers { get; set; }

    /// <summary>Pola byte, mis. "48 8B 05 ?? ?? ?? ?? 89 88".</summary>
    public string? Pattern { get; set; }
    public int PatternOffset { get; set; }
    /// <summary>Wilayah yang di-scan: "code" (executable), "data" (writable), "any".</summary>
    public string Region { get; set; } = "any";
    public RipRelativeSpec? Rip { get; set; }
}

/// <summary>Membaca alamat target dari instruksi x64 yang memakai alamat relatif RIP.</summary>
public sealed class RipRelativeSpec
{
    /// <summary>Offset awal instruksi dari hasil scan.</summary>
    public int InstructionOffset { get; set; }
    /// <summary>Offset field displacement (int32) dari awal instruksi.</summary>
    public int DisplacementOffset { get; set; } = 3;
    /// <summary>Panjang total instruksi.</summary>
    public int InstructionLength { get; set; } = 7;
}

public sealed class PatchSpec
{
    public AddressSpec Address { get; set; } = new();
    /// <summary>Byte pengganti, mis. "90 90 90".</summary>
    public string Bytes { get; set; } = "";
    /// <summary>Opsional: byte asli yang diharapkan (verifikasi sebelum patch).</summary>
    public string? ExpectedOriginal { get; set; }
}

public sealed class ModDefinition
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public string? Author { get; set; }
    public string Version { get; set; } = "1.0";
    /// <summary>URL file .zip mod (HTTPS).</summary>
    public string DownloadUrl { get; set; } = "";
    /// <summary>SHA-256 (hex) dari file .zip — wajib, diverifikasi sebelum dipasang.</summary>
    public string Sha256 { get; set; } = "";
    public long? SizeBytes { get; set; }
    /// <summary>Folder tujuan relatif terhadap root game, mis. "BepInEx/plugins". Kosong = root game.</summary>
    public string InstallPath { get; set; } = "";
    public string? Homepage { get; set; }
    /// <summary>Catatan prasyarat, mis. "Butuh BepInEx 5".</summary>
    public string? Requirements { get; set; }
}
