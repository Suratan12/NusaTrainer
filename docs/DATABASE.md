# Panduan Database Cheat & Mod

Setiap game = satu file `database/games/<id>.json`. Nama file harus sama dengan `id`.

Setelah mengedit, jalankan:

```bash
dotnet run --project tools/DbTool -- build --db database
```

Perintah ini memvalidasi semua file, menghitung SHA-256, dan memperbarui `database/index.json` (database offline yang ikut dalam aplikasi). Commit hasilnya lalu push — workflow **Publish Database** akan menandatangani dan mempublikasikannya, dan semua aplikasi mengambilnya otomatis.

---

## 1. Struktur file game

```jsonc
{
  "id": "contoh-rpg",                         // huruf kecil, angka, '-' atau '_'
  "name": "Contoh RPG",
  "revision": 1,                              // naikkan tiap kali diubah (untuk ditampilkan)
  "description": "Teks singkat di header.",
  "author": "nama-anda",
  "supportedGameVersion": "1.2.3 (Steam)",    // versi game tempat cheat diuji
  "processNames": ["ContohRPG-Win64-Shipping"],// nama proses (dengan/tanpa .exe)
  "steamAppId": 123456,                       // opsional: tombol "Mainkan" + deteksi folder game
  "exeRelativeDir": "ContohRPG/Binaries/Win64",// opsional: lokasi exe relatif terhadap root game
  "accentColor": "#7C3AED",
  "is64Bit": true,
  "onlineMultiplayer": false,                 // true = DITOLAK (khusus single-player)
  "cheats": [ ... ],
  "mods": [ ... ]
}
```

## 2. Jenis cheat

| `type` | Tampilan | `operation` yang valid |
|---|---|---|
| `toggle` | Saklar on/off | `freeze` (kunci terus), `set` (tulis sekali; `restoreOnDisable: true` untuk mengembalikan nilai asli) |
| `slider` | Penggeser + nilai | `set` (tulis saat digeser), `freeze` (muncul saklar kunci) |
| `button` | Tombol "Terapkan" | `set`, `add` (tambah ke nilai sekarang) |

Field umum:

```jsonc
{
  "id": "inf-health",
  "name": "Nyawa Tak Terbatas",
  "category": "Pemain",                // pengelompokan di UI
  "description": "opsional",
  "type": "toggle",
  "hotkey": "Num1",                    // toggle/button
  "hotkeyIncrease": "Num6",            // slider
  "hotkeyDecrease": "Num4",            // slider
  "address": { ... },                  // lihat bagian 3
  "valueType": "int32",                // byte | int16 | int32 | int64 | float | double
  "operation": "freeze",
  "value": 100,                        // toggle/button
  "min": 0, "max": 100, "step": 1, "default": 50   // slider
}
```

Hotkey: `F1`–`F24`, `Num0`–`Num9`, `NumPlus`, `NumMinus`, `NumMultiply`, `NumDivide`, `A`–`Z`, `0`–`9`, `Insert`, `Delete`, `Home`, `End`, `PageUp`, `PageDown`, panah (`Up`…), dengan pengubah `Ctrl+`, `Shift+`, `Alt+`.

## 3. Menentukan alamat (`address`)

### a) Pointer statis (hasil *Pointer scan* Cheat Engine)

Cheat Engine menampilkan pointer seperti `"Game.exe"+01A2B3C0 → 10 → 4C8`. Tuliskan:

```json
"address": { "module": "Game.exe", "offset": "0x1A2B3C0", "pointers": ["0x10", "0x4C8"] }
```

Semantiknya sama dengan Cheat Engine: `addr = base(Game.exe) + offset`, lalu untuk tiap offset: `addr = [addr] + offset`.

Kelemahan: offset biasanya berubah setiap game di-update.

### b) AOB scan (disarankan — lebih tahan update)

Cari pola byte unik, opsional dengan wildcard `??`:

```json
"address": { "pattern": "48 8B 05 ?? ?? ?? ?? 48 85 C0 74 ?? 8B 88", "module": "Game.exe", "region": "code", "patternOffset": 0 }
```

- `region`: `code` (instruksi), `data` (memori yang bisa ditulis), atau `any`.
- `module`: batasi pencarian ke satu modul (lebih cepat & aman).
- `patternOffset`: geser dari awal kecocokan.

**Alamat relatif-RIP.** Pola sering menunjuk ke instruksi seperti `mov rax, [rip+XXXXXXXX]` yang memuat pointer global. Tambahkan:

```json
"rip": { "instructionOffset": 0, "displacementOffset": 3, "instructionLength": 7 }
```

Hasilnya alamat global tersebut; lanjutkan dengan `"pointers": [...]` bila perlu. Kombinasi *AOB + RIP + pointer* adalah cara paling tahan update.

### c) Patch kode

Untuk efek seperti "tidak menerima damage", ganti instruksi pengurang nyawa dengan NOP:

```json
{
  "id": "no-damage",
  "name": "Kebal Damage",
  "type": "toggle",
  "patch": {
    "address": { "pattern": "29 87 ?? ?? 00 00 8B 87", "module": "Game.exe", "region": "code" },
    "bytes": "90 90 90 90 90 90",
    "expectedOriginal": "29 87 ?? ?? 00 00"
  }
}
```

`expectedOriginal` sangat disarankan: bila byte asli berbeda (versi game lain), patch **dibatalkan** alih-alih membuat game crash. Patch selalu dikembalikan saat cheat dimatikan, game terputus, atau aplikasi ditutup.

## 4. Mencari alamat dengan Cheat Engine (ringkas)

1. Buka game (mode single-player), lampirkan Cheat Engine ke prosesnya.
2. **Cari nilai**: mis. nyawa 100 → *First Scan*; terkena damage → *Next Scan* dengan nilai baru. Ulangi sampai tersisa sedikit alamat.
3. **Pointer**: klik kanan alamat → *Pointer scan for this address*. Restart game, *Rescan* untuk menyaring pointer yang tetap valid. Ambil rantai terpendek.
4. **AOB**: klik kanan alamat → *Find out what writes to this address* → pilih instruksi → *Show disassembler*. Salin ±12–20 byte di sekitar instruksi, ganti byte yang berubah (offset/alamat) dengan `??`. Pastikan pola **unik** (Cheat Engine → *Memory View → Search → Find memory (AOB)*).
5. Uji di NusaTrainer: tambah ke file game, `nusa-db build --db database`, jalankan aplikasi dari source.

> Simpan game sebelum menguji. Nilai di luar batas wajar (mis. emas negatif) dapat merusak save.

## 5. Mod

```jsonc
"mods": [
  {
    "id": "hd-textures",
    "name": "Tekstur HD",
    "description": "Tekstur 4K untuk lingkungan.",
    "author": "pembuat-asli",
    "version": "2.1",
    "downloadUrl": "https://example.com/hd-textures-2.1.zip",  // HTTPS, atau path relatif: "mods/file.zip"
    "sha256": "<64 hex>",                                       // wajib: nusa-db hash <file.zip>
    "sizeBytes": 734003200,
    "installPath": "Content/Paks/~mods",                        // relatif terhadap root game
    "requirements": "Butuh game versi 1.2 ke atas"
  }
]
```

- Isi `.zip` diekstrak ke `<folder game>/<installPath>/`, struktur folder di dalam zip dipertahankan.
- File asli yang tertimpa dicadangkan ke `%AppData%\NusaTrainer\backups\` dan dikembalikan saat mod dicopot.
- Path relatif seperti `"mods/x.zip"` di-host bersama database (letakkan di `database/mods/`) — cocok untuk mod kecil. Untuk file besar gunakan GitHub Releases atau hosting lain (HTTPS).
- **Hanya distribusikan mod yang diizinkan oleh pembuatnya.**

## 6. Rilis aplikasi & auto-update aplikasi

Tulis `database/app-release.json`:

```json
{
  "version": "1.1.0",
  "downloadUrl": "https://github.com/<user>/<repo>/releases/download/v1.1.0/NusaTrainer-1.1.0-win-x64.zip",
  "sha256": "<sha256 zip>",
  "notes": "Perbaikan bug & dukungan game baru."
}
```

Workflow *Build & Release* (saat push tag `v*`) mencetak cuplikan ini di catatan rilis. Opsional `"minAppVersion"` dapat di-set lewat `nusa-db build --min-app-version 1.1.0` agar aplikasi lama tidak memakai database yang butuh fitur baru.

## 7. Keamanan rantai update

```
editor → push database/ → GitHub Actions (kunci privat di Secrets)
       → index.json + index.json.sig (ECDSA P-256) → GitHub Pages
aplikasi → unduh index.json + .sig → verifikasi dengan public key bawaan
         → unduh file berubah → verifikasi SHA-256 tiap file → validasi isi → simpan atomik
```

Jika tanda tangan, hash, atau isi tidak valid, **tidak ada satu file pun yang ditulis** dan database lama tetap dipakai.
