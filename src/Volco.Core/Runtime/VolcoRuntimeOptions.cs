using Volco.Core.Communication;
using Volco.Core.Firmware;

namespace Volco.Core.Runtime;

/// <summary>Tunables for <see cref="VolcoRuntime"/>. Exists so tests can shorten delays; the app
/// always uses <see cref="Default"/>.</summary>
public sealed record VolcoRuntimeOptions
{
    public static VolcoRuntimeOptions Default { get; } = new();

    /// <summary>How long a device-switch press waits for its DefaultChanged before the pending
    /// OSD is discarded (a switch that never lands must not claim a later, unrelated change).</summary>
    public TimeSpan PendingSwitchTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Wrap a mode change in Begin/Commit so every screen slides on the same loop pass
    /// (spec §3.4). Off: the same frames go out unwrapped, so each screen slides as its message
    /// arrives; a test switch for judging the delay.</summary>
    public bool SynchronizedModeTransitions { get; init; } = true;

    /// <summary>The clock the config session's timeouts use (spec §8.2); tests pass a fake.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>The firmware this build carries (spec §8.6): the banner's update notice compares
    /// Hello's build id with it. Empty for tests and local builds.</summary>
    public FirmwareCatalog Firmware { get; init; } = FirmwareCatalog.Empty;

    /// <summary>How long an open USB port may go without a Hello before the controller counts as
    /// silent (the Controller page then suggests checking its firmware).</summary>
    public TimeSpan SilentAfter { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a transport Volco just started counts as an expected wait ("Connecting…")
    /// before what it finds is reported (no red tray for the first moments of a launch, a
    /// Reconnect or a port change). Zero: none.</summary>
    public TimeSpan ConnectGrace { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>How often a pin watch is sent again while it runs (firmware helpers, Detect); the
    /// controller stops watching 30 s after the last one.</summary>
    public TimeSpan PinWatchRenewal { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long <see cref="VolcoRuntime.ScanI2cAsync"/> waits for the controller's result
    /// (firmware helpers, Find screens).</summary>
    public TimeSpan I2cScanTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Opens one USB port by name (a manual port, or each port Automatic tries). Null: a
    /// <see cref="SerialConnection"/>. Tests pass fakes.</summary>
    public Func<string, IControllerConnection>? SerialFactory { get; init; }

    /// <summary>How long Automatic waits on each port, and how often it reads the port list.</summary>
    public AutoSerialOptions AutoSerial { get; init; } = AutoSerialOptions.Default;

    /// <summary>HelloRequest retry, heartbeat and tick intervals.</summary>
    public ControllerLinkOptions Link { get; init; } = ControllerLinkOptions.Default;
}
