using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.CheatTables;

public sealed class CheatTableImportResult
{
    public List<CheatDefinition> Cheats { get; } = new();
    /// <summary>Entri Auto Assembler (skrip) — hanya bisa dijalankan lewat Cheat Engine.</summary>
    public int ScriptEntries { get; set; }
    /// <summary>Entri lain yang tidak didukung (string, array of byte, simbol dari skrip, dll.).</summary>
    public int UnsupportedEntries { get; set; }
    public List<string> Notes { get; } = new();
}

/// <summary>
/// Mengimpor tabel Cheat Engine (.CT). Entri alamat/pointer menjadi cheat "Number" bawaan NusaTrainer
/// (lihat nilai, ubah, kunci). Skrip Auto Assembler dihitung agar pengguna bisa membuka tabel di Cheat Engine.
/// </summary>
public static partial class CheatTableImporter
{
    // "Game.exe"+1234 | Game.exe+1234 | game.exe | 7FF6A0001000 | Game.exe+1234-10
    [GeneratedRegex("^\\s*(?:\"(?<mod>[^\"]+)\"|(?<mod>[^\"+\\[\\]\\s]+?\\.(?:exe|dll)))\\s*(?<sign>[+-])\\s*(?:0x)?(?<off>[0-9A-Fa-f]+)\\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleOffset();

    [GeneratedRegex("^\\s*(?:0x)?(?<abs>[0-9A-Fa-f]{6,16})\\s*$")]
    private static partial Regex Absolute();

    public static CheatTableImportResult Import(string ctXml)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(ctXml, LoadOptions.None);
        }
        catch (XmlException ex)
        {
            throw new FormatException("File bukan tabel Cheat Engine yang valid (.CT XML). Tabel terenkripsi/.CETRAINER tidak didukung.", ex);
        }
        if (doc.Root?.Name.LocalName != "CheatTable")
            throw new FormatException("File bukan tabel Cheat Engine (elemen <CheatTable> tidak ada).");

        var result = new CheatTableImportResult();
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = doc.Root.Element("CheatEntries");
        if (entries is not null) Walk(entries, "Tabel Cheat Engine", result, usedIds);

        if (result.Cheats.Count == 0 && result.ScriptEntries == 0)
            result.Notes.Add("Tidak ada entri yang bisa diimpor.");
        return result;
    }

    private static void Walk(XElement entries, string category, CheatTableImportResult result, HashSet<string> usedIds)
    {
        foreach (var e in entries.Elements("CheatEntry"))
        {
            var desc = Unquote((string?)e.Element("Description") ?? "Tanpa nama");
            var type = ((string?)e.Element("VariableType"))?.Trim() ?? "";
            bool isGroup = (string?)e.Element("GroupHeader") == "1";
            var children = e.Element("CheatEntries");

            if (type.Equals("Auto Assembler Script", StringComparison.OrdinalIgnoreCase) || e.Element("AssemblerScript") is not null)
            {
                result.ScriptEntries++;
            }
            else if (!isGroup && e.Element("Address") is { } addrEl)
            {
                var cheat = TryConvert(desc, type, (string)addrEl, e, category, usedIds);
                if (cheat is null) result.UnsupportedEntries++;
                else result.Cheats.Add(cheat);
            }

            // Anak dari sebuah grup/skrip memakai nama induk sebagai kategori.
            if (children is not null) Walk(children, desc, result, usedIds);
        }
    }

    private static CheatDefinition? TryConvert(string desc, string varType, string address, XElement entry, string category,
        HashSet<string> usedIds)
    {
        DataType? dt = varType switch
        {
            "Byte" => DataType.Byte,
            "2 Bytes" => DataType.Int16,
            "4 Bytes" => DataType.Int32,
            "8 Bytes" => DataType.Int64,
            "Float" => DataType.Float,
            "Double" => DataType.Double,
            _ => null,
        };
        if (dt is null) return null;

        var spec = new AddressSpec();
        var m = ModuleOffset().Match(address);
        if (m.Success)
        {
            spec.Module = m.Groups["mod"].Value.Trim();
            spec.Offset = (m.Groups["sign"].Value == "-" ? "-0x" : "0x") + m.Groups["off"].Value.ToUpperInvariant();
        }
        else if (Absolute().Match(address) is { Success: true } a && entry.Element("Offsets") is not null)
        {
            // Alamat absolut hanya berguna sebagai basis pointer statis; tanpa module ia berubah tiap game dibuka.
            return null;
        }
        else
        {
            return null; // simbol dari skrip (mis. "pPlayer") atau ekspresi kompleks
        }

        // Cheat Engine menyimpan offset dari yang TERAKHIR diterapkan ke yang pertama.
        var offsets = entry.Element("Offsets")?.Elements("Offset").Select(o => ((string)o).Trim()).ToList();
        if (offsets is { Count: > 0 })
        {
            if (offsets.Any(o => !long.TryParse(o.TrimStart('-').Replace("0x", "", StringComparison.OrdinalIgnoreCase),
                    NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
                return null; // offset berupa simbol/ekspresi
            offsets.Reverse();
            spec.Pointers = offsets.Select(o => o.StartsWith('-') ? "-0x" + o[1..] : "0x" + o).ToList();
        }

        var id = MakeId(desc, usedIds);
        return new CheatDefinition
        {
            Id = id,
            Name = desc,
            Category = category,
            Type = CheatKind.Number,
            Address = spec,
            ValueType = dt.Value,
            Hotkey = ReadHotkey(entry),
            Description = $"Diimpor dari tabel Cheat Engine ({varType}).",
        };
    }

    private static string? ReadHotkey(XElement entry)
    {
        foreach (var hk in entry.Element("Hotkeys")?.Elements("Hotkey") ?? [])
        {
            var action = ((string?)hk.Element("Action"))?.Trim();
            if (action is not ("Toggle Activation" or "Activate")) continue;
            var keys = hk.Element("Keys")?.Elements("Key").Select(k => int.TryParse((string)k, out var v) ? v : -1).ToList();
            if (keys is null || keys.Count == 0 || keys.Contains(-1)) continue;
            var parts = new List<string>();
            string? main = null;
            foreach (var vk in keys)
            {
                switch (vk)
                {
                    case 16: case 160: case 161: parts.Add("Shift"); break;
                    case 17: case 162: case 163: parts.Add("Ctrl"); break;
                    case 18: case 164: case 165: parts.Add("Alt"); break;
                    default: main = VkName(vk); break;
                }
            }
            if (main is null) continue;
            parts.Add(main);
            return string.Join("+", parts.Distinct());
        }
        return null;
    }

    private static string? VkName(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 => ((char)vk).ToString(),
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x6A => "NumMultiply", 0x6B => "NumPlus", 0x6D => "NumMinus", 0x6F => "NumDivide",
        0x2D => "Insert", 0x2E => "Delete", 0x24 => "Home", 0x23 => "End", 0x21 => "PageUp", 0x22 => "PageDown",
        0x26 => "Up", 0x28 => "Down", 0x25 => "Left", 0x27 => "Right",
        _ => null,
    };

    private static string Unquote(string s)
    {
        s = s.Trim();
        return s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1] : s;
    }

    private static string MakeId(string desc, HashSet<string> used)
    {
        var baseId = Regex.Replace(desc.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (baseId.Length == 0) baseId = "entri";
        if (baseId.Length > 40) baseId = baseId[..40].Trim('-');
        baseId = "ct-" + baseId;
        var id = baseId;
        for (int i = 2; !used.Add(id); i++) id = $"{baseId}-{i}";
        return id;
    }
}
