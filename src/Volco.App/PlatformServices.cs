using Volco.App.Platform;
using Volco.Core.Audio;
using Volco.Core.Firmware;
using Volco.Core.Media;
using Volco.Core.Programs;

namespace Volco.App;

/// <param name="Flasher">The USB firmware flasher (Windows); null where there is none.</param>
/// <param name="Firmware">Where the bundled controller firmware comes from; null = none.</param>
/// <param name="Media">The system's media sessions (media button actions); null = none.</param>
/// <param name="Launcher">Starts and finds programs (Open a program); null = none.</param>
/// <param name="StartWithSystem">Starts Volco at sign-in (General page); null = none, the toggle is hidden.</param>
public sealed record PlatformServices(IAudioEngine Audio, bool AudioReady, IWindowEffects WindowEffects,
                                      IFirmwareFlasher? Flasher = null, IFirmwareSource? Firmware = null,
                                      IMediaSessions? Media = null, IProgramLauncher? Launcher = null,
                                      IStartWithSystem? StartWithSystem = null);
