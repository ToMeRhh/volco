using Volco.Core.Communication;
using Volco.Core.Config;

namespace Volco.Core.Controller;

/// <summary>Which row of the spec §8.3 table the Controller page is in. The page maps each kind to
/// its text; the spec's wording is quoted here.</summary>
public enum BannerKind
{
    /// <summary>No banner: in sync, no Hello yet, or an automatic read about to adopt the setup.</summary>
    None,
    /// <summary>"This controller's firmware is incompatible. Update it to use it."</summary>
    Incompatible,
    /// <summary>A save is running: the page shows its progress.</summary>
    Saving,
    /// <summary>A read is running.</summary>
    Reading,
    /// <summary>"This controller has a different setup."</summary>
    DifferentSetup,
    /// <summary>"Couldn't read the controller's setup."</summary>
    ReadFailed,
    /// <summary>"This controller isn't set up." (Volco has a saved setup.)</summary>
    NotSetUpHasMirror,
    /// <summary>"Set up your controller."</summary>
    NotSetUp,
    /// <summary>"The controller's saved setup can't be used."</summary>
    Unusable,
    /// <summary>"Your saved setup doesn't work with this firmware."</summary>
    BrokenWithFirmware,
    /// <summary>The port is open but no Hello came within a few seconds: "No Volco controller
    /// answered on COM3. It may not have Volco's firmware yet." (USB only.)</summary>
    NoAnswer,
}

[Flags]
public enum BannerActions
{
    None = 0,
    UseControllers = 1,
    ApplyMine = 2,
    Retry = 4,
    SetUpFromScratch = 8,
    ApplySaved = 16,
    SetUp = 32,
    FixIt = 64,
    Update = 128,
    /// <summary>Check what's on the port (the firmware probe).</summary>
    CheckFirmware = 256,
}

/// <param name="FirmwareUpdateAvailable">The firmware notice, or an incompatible firmware that an
/// update would fix: drives the page's banner, the sidebar dot and the tray (spec §8.3).</param>
/// <param name="DevelopmentFirmware">The controller runs a "dev" build (spec §9.4).</param>
public sealed record ControllerBanner(BannerKind Kind, BannerActions Actions, bool FirmwareUpdateAvailable, bool DevelopmentFirmware)
{
    public static ControllerBanner Empty { get; } = new(BannerKind.None, BannerActions.None, false, false);

    /// <summary>The controller needs the user: the sidebar dot and the tray say so.</summary>
    public bool NeedsAttention => FirmwareUpdateAvailable || Kind is BannerKind.NoAnswer or BannerKind.Incompatible
        or BannerKind.DifferentSetup or BannerKind.ReadFailed or BannerKind.NotSetUpHasMirror or BannerKind.NotSetUp
        or BannerKind.Unusable or BannerKind.BrokenWithFirmware;
}

/// <param name="Hello">The last Hello on the current connection, if any.</param>
/// <param name="Mirror">The mirror's bytes, or null when there is none.</param>
/// <param name="ReadFailed">The last read under this Hello failed.</param>
/// <param name="BundledFirmwareVersion">The build id Volco carries; null when it carries no firmware.</param>
/// <param name="Silent">The port has been open for a while with no Hello (USB only).</param>
public sealed record BannerInputs(HelloMessage? Hello, byte[]? Mirror, ConfigOperation Operation, bool ReadFailed,
                                  ConnectionType ConnectionType, string? BundledFirmwareVersion, bool Silent = false);

/// <summary>Spec §8.3: the banner as a pure function of the last Hello, the mirror and the operation.</summary>
public static class ControllerBannerLogic
{
    public const string DevFirmware = "dev";

    public static ControllerBanner Compute(BannerInputs i)
    {
        var h = i.Hello;
        if (h is null)
            return i.Silent && i.ConnectionType == ConnectionType.Serial
                ? new(BannerKind.NoAnswer, BannerActions.CheckFirmware, false, false)
                : ControllerBanner.Empty;

        bool canUpdate = i.ConnectionType == ConnectionType.Serial && i.BundledFirmwareVersion is not null;

        // The version check comes first: a version-only Hello has the default configState.
        if (h.Version != ProtocolVersion.Current)
            return new(BannerKind.Incompatible, canUpdate ? BannerActions.Update : BannerActions.None, canUpdate, false);

        bool dev = h.FirmwareVersion == DevFirmware;
        bool notice = canUpdate && h.Chip == ChipFamily.Esp32 && !dev && h.FirmwareVersion != i.BundledFirmwareVersion;

        if (i.Operation == ConfigOperation.Writing) return new(BannerKind.Saving, BannerActions.None, notice, dev);
        if (i.Operation == ConfigOperation.Reading) return new(BannerKind.Reading, BannerActions.None, notice, dev);

        // A mirror that fails Decode counts as absent.
        uint? mirrorCrc = Decodes(i.Mirror) ? ControllerConfigCodec.Crc32(i.Mirror!) : null;

        var (kind, actions) = h.ConfigState switch
        {
            ControllerConfigState.Stored when mirrorCrc is { } crc => h.ConfigCrc == crc
                ? (BannerKind.None, BannerActions.None)
                : i.ReadFailed
                    ? (BannerKind.ReadFailed, BannerActions.UseControllers | BannerActions.ApplyMine | BannerActions.Retry)
                    : (BannerKind.DifferentSetup, BannerActions.UseControllers | BannerActions.ApplyMine),
            ControllerConfigState.Stored => i.ReadFailed
                ? (BannerKind.ReadFailed, BannerActions.Retry | BannerActions.SetUpFromScratch)
                : (BannerKind.None, BannerActions.None),   // the automatic read adopts it
            ControllerConfigState.None => mirrorCrc is not null
                ? (BannerKind.NotSetUpHasMirror, BannerActions.ApplySaved | BannerActions.SetUpFromScratch)
                : (BannerKind.NotSetUp, BannerActions.SetUp),
            _ when mirrorCrc is { } crc && h.ConfigCrc == crc => (BannerKind.BrokenWithFirmware, BannerActions.FixIt),
            _ => (BannerKind.Unusable, (mirrorCrc is not null ? BannerActions.ApplySaved : BannerActions.None) | BannerActions.SetUpFromScratch),
        };
        return new(kind, actions, notice, dev);
    }

    /// <summary>Whether <paramref name="mirror"/> is present and decodes with this app's codec.</summary>
    public static bool Decodes(byte[]? mirror)
    {
        if (mirror is null || mirror.Length == 0) return false;
        try
        {
            ControllerConfigCodec.Decode(mirror);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
