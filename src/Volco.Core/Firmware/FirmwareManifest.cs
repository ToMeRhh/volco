using System.Security.Cryptography;
using System.Text.Json;
using Volco.Core.Diagnostics;

namespace Volco.Core.Firmware;

public sealed class FirmwareManifestException(string message) : Exception(message);

/// <summary>One flash part as the manifest describes it (spec §9.1).</summary>
public sealed record FirmwarePartInfo(string Name, string File, uint Offset, uint Size, string Md5);

/// <summary>
/// <c>firmware-manifest.json</c> (spec §9.1), written by <c>firmware/scripts/manifest.py</c>:
/// the chip, the build id the controller reports in Hello, the parts and the NVS range. Parsing
/// rejects anything an install must not do: a part overlapping NVS, overlapping parts, no app.
/// </summary>
public sealed record FirmwareManifest(string Chip, string Version, string DisplayVersion,
                                      IReadOnlyList<FirmwarePartInfo> Parts, uint NvsOffset, uint NvsSize)
{
    public FirmwarePartInfo App => Parts.Single(p => p.Name == "app");

    public static FirmwareManifest Parse(string json)
    {
        JsonElement root;
        try { root = JsonDocument.Parse(json).RootElement; }
        catch (JsonException e) { throw new FirmwareManifestException($"Not JSON: {e.Message}"); }

        string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s
                ? s : throw new FirmwareManifestException($"Missing \"{name}\".");
        uint Num(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n)
                ? n : throw new FirmwareManifestException($"Missing or invalid \"{name}\".");

        if (!root.TryGetProperty("parts", out var partsJson) || partsJson.ValueKind != JsonValueKind.Array)
            throw new FirmwareManifestException("Missing \"parts\".");
        if (!root.TryGetProperty("nvs", out var nvs) || nvs.ValueKind != JsonValueKind.Object)
            throw new FirmwareManifestException("Missing \"nvs\".");

        var parts = partsJson.EnumerateArray()
            .Select(p => new FirmwarePartInfo(Str(p, "name"), Str(p, "file"), Num(p, "offset"), Num(p, "size"), Str(p, "md5").ToLowerInvariant()))
            .OrderBy(p => p.Offset).ToList();
        var version = Str(root, "version");
        var manifest = new FirmwareManifest(Str(root, "chip"), version,
            root.TryGetProperty("displayVersion", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : version,
            parts, Num(nvs, "offset"), Num(nvs, "size"));
        manifest.Validate();
        return manifest;
    }

    private void Validate()
    {
        if (Parts.Count(p => p.Name == "app") != 1) throw new FirmwareManifestException("Exactly one \"app\" part is required.");
        // Erases are whole 4 KB sectors: with NVS sector-aligned, a part outside NVS can't erase into it.
        if (NvsOffset % 0x1000 != 0 || NvsSize % 0x1000 != 0 || NvsSize == 0)
            throw new FirmwareManifestException("The NVS range must be whole 4 KB sectors.");
        ulong nvsEnd = (ulong)NvsOffset + NvsSize;
        for (int i = 0; i < Parts.Count; i++)
        {
            var p = Parts[i];
            if (p.Size == 0) throw new FirmwareManifestException($"Part {p.Name} is empty.");
            if (p.File.Contains('/') || p.File.Contains('\\') || p.File.Contains(".."))
                throw new FirmwareManifestException($"Part {p.Name} names a path: {p.File}.");
            ulong end = (ulong)p.Offset + p.Size;
            // Spec §8.6: Update must never write NVS, so no part may reach into it.
            if (p.Offset < nvsEnd && NvsOffset < end)
                throw new FirmwareManifestException($"Part {p.Name} overlaps NVS.");
            if (i > 0 && (ulong)Parts[i - 1].Offset + Parts[i - 1].Size > p.Offset)
                throw new FirmwareManifestException($"Parts {Parts[i - 1].Name} and {p.Name} overlap.");
        }
    }
}

/// <summary>A part with its bytes.</summary>
public sealed record FirmwarePart(FirmwarePartInfo Info, byte[] Data);

/// <summary>Where bundled firmware files come from: the exe's embedded resources (Windows), or
/// nothing (tests, local builds). Paths look like "esp32/firmware-manifest.json".</summary>
public interface IFirmwareSource
{
    Stream? Open(string path);
}

public sealed class EmptyFirmwareSource : IFirmwareSource
{
    public static EmptyFirmwareSource Instance { get; } = new();
    public Stream? Open(string path) => null;
}

/// <summary>A manifest with every part loaded and checked against its size and MD5.</summary>
public sealed class FirmwareImage
{
    private FirmwareImage(FirmwareManifest manifest, IReadOnlyList<FirmwarePart> parts)
        => (Manifest, Parts) = (manifest, parts);

    public FirmwareManifest Manifest { get; }
    public IReadOnlyList<FirmwarePart> Parts { get; }
    public FirmwarePart App => Parts.Single(p => p.Info.Name == "app");

    /// <summary>The image for <paramref name="chip"/> ("esp32"), or null when the source has none.</summary>
    public static FirmwareImage? Load(IFirmwareSource source, string chip)
    {
        using var manifestStream = source.Open($"{chip}/firmware-manifest.json");
        if (manifestStream is null) return null;
        var manifest = FirmwareManifest.Parse(new StreamReader(manifestStream).ReadToEnd());
        if (manifest.Chip != chip) throw new FirmwareManifestException($"The manifest is for {manifest.Chip}, not {chip}.");
        var parts = new List<FirmwarePart>();
        foreach (var info in manifest.Parts)
        {
            using var s = source.Open($"{chip}/{info.File}") ?? throw new FirmwareManifestException($"{info.File} is missing.");
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var data = ms.ToArray();
            if (data.Length != info.Size) throw new FirmwareManifestException($"{info.File} is {data.Length} bytes, not {info.Size}.");
            if (Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant() != info.Md5)
                throw new FirmwareManifestException($"{info.File} doesn't match its MD5.");
            parts.Add(new FirmwarePart(info, data));
        }
        return new FirmwareImage(manifest, parts);
    }
}

/// <summary>The firmware this build of Volco carries (spec §8.6). Only ESP32 today.</summary>
public sealed class FirmwareCatalog
{
    private FirmwareCatalog(FirmwareImage? esp32) => Esp32 = esp32;

    public static FirmwareCatalog Empty { get; } = new(null);

    public FirmwareImage? Esp32 { get; }
    public bool HasFirmware => Esp32 is not null;

    /// <summary>The bundled build id (Hello's firmware field when the bundled build runs).</summary>
    public string? BundledVersion => Esp32?.Manifest.Version;

    /// <summary>Loads the bundle; a broken one is logged and treated as none.</summary>
    public static FirmwareCatalog Load(IFirmwareSource source)
    {
        try
        {
            var image = FirmwareImage.Load(source, "esp32");
            if (image is not null) AppLog.Info($"Bundled controller firmware: {image.Manifest.Version} ({image.Manifest.DisplayVersion})");
            return new FirmwareCatalog(image);
        }
        catch (Exception e)
        {
            AppLog.Error($"Bundled controller firmware unusable: {e.Message}");
            return Empty;
        }
    }

    /// <summary>Tests.</summary>
    public static FirmwareCatalog From(FirmwareImage image) => new(image);
}
