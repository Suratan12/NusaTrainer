// DemoGame — "game" kecil untuk menguji NusaTrainer tanpa menyentuh game sungguhan.
// Status pemain disimpan di blok memori unmanaged yang diawali tanda tangan 16 byte "NUSA-DEMO-GAME!!",
// sehingga bisa ditemukan oleh cheat berbasis AOB scan (lihat database/games/demogame.json).
//
// Layout blok:  +0  magic (16 byte)
//               +16 int32  nyawa (health)
//               +20 int32  amunisi
//               +24 int32  emas
//               +28 float  kecepatan

using System.Runtime.InteropServices;

unsafe
{
    // Magic dibangun saat runtime (di-XOR) agar tidak ada salinan lain di memori yang ikut cocok dengan pola.
    byte[] obf = [0x14, 0x0F, 0x09, 0x1B, 0x77, 0x1E, 0x1F, 0x17, 0x15, 0x77, 0x1D, 0x1B, 0x17, 0x1F, 0x7B, 0x7B];
    byte* block = (byte*)NativeMemory.AllocZeroed(64);
    for (int i = 0; i < 16; i++) block[i] = (byte)(obf[i] ^ 0x5A);

    ref int health = ref *(int*)(block + 16);
    ref int ammo = ref *(int*)(block + 20);
    ref int gold = ref *(int*)(block + 24);
    ref float speed = ref *(float*)(block + 28);
    health = 100; ammo = 30; gold = 50; speed = 1.0f;

    bool quiet = args.Contains("--quiet");

    // "Mod loader" sederhana: tampilkan isi semua mods/*.txt di folder game.
    var modsDir = Path.Combine(AppContext.BaseDirectory, "mods");
    if (Directory.Exists(modsDir))
    {
        foreach (var f in Directory.GetFiles(modsDir, "*.txt"))
            Console.WriteLine($"[MOD] {Path.GetFileName(f)}: {File.ReadAllText(f).Trim()}");
    }
    var rng = new Random();
    Console.WriteLine($"DemoGame berjalan (PID {Environment.ProcessId}). Tekan Ctrl+C untuk keluar.");
    int tick = 0;
    while (true)
    {
        Thread.Sleep(500);
        tick++;
        // "Musuh" menyerang, pemain menembak, kadang dapat emas.
        health -= rng.Next(1, 6);
        if (ammo > 0) ammo--;
        if (tick % 4 == 0) gold += 5;
        if (health <= 0)
        {
            if (!quiet) Console.WriteLine("💀 Pemain mati! Respawn…");
            health = 100; ammo = 30;
        }
        if (!quiet)
            Console.WriteLine($"Nyawa: {health,4}   Amunisi: {ammo,3}   Emas: {gold,7}   Kecepatan: {speed:0.0}x");
    }
}
