namespace Volco.Core.Firmware;

public enum FlashMode
{
    /// <summary>Writes the parts and wipes NVS (the controller's config).</summary>
    Install,
    /// <summary>Writes the parts only: the config survives.</summary>
    Update,
}

public enum ProbeKind { NoDevice, UnsupportedChip, VolcoMatches, DifferentFirmware }

/// <param name="ChipName">For UnsupportedChip, e.g. "ESP32-S3".</param>
/// <param name="Message">For NoDevice, the flasher's own reason (e.g. the port is in use).</param>
public sealed record ProbeResult(ProbeKind Kind, string? ChipName = null, string? Message = null);

public enum FlashStage { Connecting, Erasing, Writing, Verifying, Restarting }

/// <param name="Fraction">0–1 within the stage.</param>
public sealed record FlashProgress(FlashStage Stage, double Fraction);

/// <summary>The board isn't the image's chip family; nothing was written.</summary>
public sealed class UnsupportedChipException(string chipName)
    : Exception($"This board is an {chipName}; this version of Volco only includes firmware for ESP32.")
{
    public string ChipName { get; } = chipName;
}

/// <summary>No ESP32 answered on the port; nothing was written.</summary>
public sealed class NoDeviceException(string message) : Exception(message);

/// <summary>The flash failed. <see cref="Written"/>: whether anything had been written.</summary>
public sealed class FirmwareFlashException(string reason, bool written) : Exception(reason)
{
    public bool Written { get; } = written;
}

/// <summary>
/// The seam to the USB flasher (spec §8.6). The implementation lives in Volco.Windows (EspDotNet)
/// and is handed over through PlatformServices; tests use a fake.
/// </summary>
public interface IFirmwareFlasher
{
    /// <summary>Resets the board into its bootloader, reads the chip and the app region's MD5, resets it.</summary>
    Task<ProbeResult> ProbeAsync(string port, FirmwareImage bundled, IProgress<FlashProgress> progress, CancellationToken ct);

    /// <summary>Writes <paramref name="image"/> (and for Install wipes NVS first), verifies, resets.
    /// <paramref name="ct"/> is honoured only until the first write. Throws
    /// <see cref="NoDeviceException"/>, <see cref="UnsupportedChipException"/> or
    /// <see cref="FirmwareFlashException"/>.</summary>
    Task FlashAsync(string port, FirmwareImage image, FlashMode mode, IProgress<FlashProgress> progress, CancellationToken ct);
}
