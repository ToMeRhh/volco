using Volco.App.Platform;
using Volco.Core.Audio;
using Volco.Core.Firmware;

namespace Volco.App;

/// <param name="Flasher">The USB firmware flasher (Windows); null where there is none.</param>
/// <param name="Firmware">Where the bundled controller firmware comes from; null = none.</param>
public sealed record PlatformServices(IAudioEngine Audio, bool AudioReady, IWindowEffects WindowEffects,
                                      IFirmwareFlasher? Flasher = null, IFirmwareSource? Firmware = null);
