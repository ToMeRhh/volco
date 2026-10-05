using Volco.Core.Firmware;

namespace Volco.Windows.Firmware;

/// <summary>The controller firmware embedded in Volco.exe by CI (spec §9.3): resources named
/// "Firmware/esp32/…" (Volco.Windows.csproj). A local build has none.</summary>
public sealed class EmbeddedFirmwareSource : IFirmwareSource
{
    public Stream? Open(string path) => typeof(EmbeddedFirmwareSource).Assembly.GetManifestResourceStream($"Firmware/{path}");
}
