using System.Text.RegularExpressions;
using NusaTrainer.Core.Memory;
using NusaTrainer.Core.Models;

namespace NusaTrainer.Core.Database;

public static partial class GameDefinitionValidator
{
    [GeneratedRegex("^[a-z0-9][a-z0-9-_]{0,63}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Regex();

    public static List<string> Validate(GameDefinition g)
    {
        var errors = new List<string>();
        if (!IdRegex().IsMatch(g.Id)) errors.Add($"id '{g.Id}' tidak valid (huruf kecil, angka, '-', '_').");
        if (string.IsNullOrWhiteSpace(g.Name)) errors.Add("name kosong.");
        if (g.ProcessNames.Count == 0) errors.Add("processNames kosong.");
        if (g.OnlineMultiplayer) errors.Add("Game online/multiplayer tidak diizinkan di database.");

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in g.Cheats)
        {
            var p = $"cheat '{c.Id}'";
            if (!IdRegex().IsMatch(c.Id)) errors.Add($"{p}: id tidak valid.");
            if (!ids.Add(c.Id)) errors.Add($"{p}: id duplikat.");
            if (string.IsNullOrWhiteSpace(c.Name)) errors.Add($"{p}: name kosong.");

            if (c.Patch is not null)
            {
                if (c.Type != CheatKind.Toggle) errors.Add($"{p}: patch hanya untuk type toggle.");
                ValidateAddress(c.Patch.Address, p, errors);
                TryParse(() => AobPattern.ParseBytes(c.Patch.Bytes), $"{p}: patch.bytes", errors);
                if (c.Patch.ExpectedOriginal is { } eo)
                    TryParse(() => AobPattern.Parse(eo), $"{p}: patch.expectedOriginal", errors);
            }
            else if (c.Address is null)
            {
                errors.Add($"{p}: butuh 'address' atau 'patch'.");
            }
            else
            {
                ValidateAddress(c.Address, p, errors);
            }

            if (c.Type == CheatKind.Slider)
            {
                if (c.Max <= c.Min) errors.Add($"{p}: max harus > min.");
                if (c.Step <= 0) errors.Add($"{p}: step harus > 0.");
                if (c.Default < c.Min || c.Default > c.Max) errors.Add($"{p}: default di luar rentang.");
            }
            if (c.Type == CheatKind.Button && c.Operation == CheatOperation.Freeze)
                errors.Add($"{p}: button tidak bisa memakai operation freeze.");
            if (c.Type == CheatKind.Slider && c.Operation == CheatOperation.Add)
                errors.Add($"{p}: slider tidak bisa memakai operation add.");
            if (c.Type == CheatKind.Number && c.Patch is not null)
                errors.Add($"{p}: number tidak bisa memakai patch.");
        }

        foreach (var t in g.ExternalTrainers)
        {
            if (string.IsNullOrWhiteSpace(t.Name)) errors.Add("externalTrainers: name kosong.");
            if (!Uri.TryCreate(t.Homepage, UriKind.Absolute, out var hp) || hp.Scheme != Uri.UriSchemeHttps)
                errors.Add($"externalTrainers '{t.Name}': homepage harus HTTPS.");
        }

        var modIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in g.Mods)
        {
            var p = $"mod '{m.Id}'";
            if (!IdRegex().IsMatch(m.Id)) errors.Add($"{p}: id tidak valid.");
            if (!modIds.Add(m.Id)) errors.Add($"{p}: id duplikat.");
            if (Uri.TryCreate(m.DownloadUrl, UriKind.Absolute, out var uri))
            {
                if (uri.Scheme != Uri.UriSchemeHttps) errors.Add($"{p}: downloadUrl harus HTTPS.");
            }
            else if (string.IsNullOrWhiteSpace(m.DownloadUrl) || m.DownloadUrl.StartsWith('/') || m.DownloadUrl.Contains('\\')
                     || m.DownloadUrl.Split('/').Contains(".."))
            {
                errors.Add($"{p}: downloadUrl harus URL HTTPS atau path relatif (mis. 'mods/x.zip') tanpa '..'.");
            }
            if (!Sha256Regex().IsMatch(m.Sha256)) errors.Add($"{p}: sha256 wajib (64 hex).");
            if (Path.IsPathRooted(m.InstallPath) || m.InstallPath.Split('/', '\\').Contains(".."))
                errors.Add($"{p}: installPath harus relatif dan tanpa '..'.");
        }
        return errors;
    }

    private static void ValidateAddress(AddressSpec a, string p, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(a.Pattern) && string.IsNullOrWhiteSpace(a.Module))
            errors.Add($"{p}: address butuh 'pattern' atau 'module'.");
        if (a.Pattern is { } pat) TryParse(() => AobPattern.Parse(pat), $"{p}: pattern", errors);
        if (a.Offset is { } off) TryParse(() => Trainer.AddressResolver.ParseHex(off), $"{p}: offset", errors);
        foreach (var ptr in a.Pointers ?? []) TryParse(() => Trainer.AddressResolver.ParseHex(ptr), $"{p}: pointers", errors);
        if (a.Region is not ("any" or "code" or "data")) errors.Add($"{p}: region harus any/code/data.");
    }

    private static void TryParse(Func<object> parse, string what, List<string> errors)
    {
        try { parse(); }
        catch (Exception ex) { errors.Add($"{what}: {ex.Message}"); }
    }
}
