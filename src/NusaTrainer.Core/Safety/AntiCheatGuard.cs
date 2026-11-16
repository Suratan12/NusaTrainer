using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Safety;

/// <summary>
/// NusaTrainer hanya untuk game single-player/offline. Penjaga ini menolak attach ke game yang
/// ditandai online, atau yang memuat modul anti-cheat (memodifikasi game seperti itu bisa membuat akun di-ban
/// dan merugikan pemain lain).
/// </summary>
public static class AntiCheatGuard
{
    private static readonly string[] AntiCheatModulePrefixes =
    [
        "easyanticheat", "eac_", "beclient", "bedaisy", "battleye", "vgk", "vanguard", "xigncode", "x3.xem",
        "npggnt", "gameguard", "faceit", "esportal", "ricochet", "mhyprot", "ace-base", "acebase", "nprotect",
    ];

    public static string? CheckDefinition(GameDefinition game) =>
        game.OnlineMultiplayer
            ? "Game ini ditandai sebagai online/multiplayer. NusaTrainer hanya mendukung permainan single-player."
            : null;

    /// <summary>Return pesan penolakan bila ditemukan anti-cheat, null bila aman.</summary>
    public static string? CheckProcess(IProcessMemory memory)
    {
        foreach (var m in memory.GetModules())
        {
            var name = m.Name.ToLowerInvariant();
            if (AntiCheatModulePrefixes.Any(name.StartsWith))
                return $"Anti-cheat terdeteksi ({m.Name}). Demi keamanan akun dan keadilan bagi pemain lain, NusaTrainer tidak akan terhubung. " +
                       "Jika game punya mode offline tanpa anti-cheat, jalankan mode tersebut.";
        }
        return null;
    }
}
