using Volco.App.Platform;
using Volco.Core.Audio;

namespace Volco.App;

public sealed record PlatformServices(IAudioEngine Audio, bool AudioReady, IWindowEffects WindowEffects);
