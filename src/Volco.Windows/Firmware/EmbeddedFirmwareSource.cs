using Volco.Core.Firmware;

namespace Volco.Windows.Firmware;

/// <summary>The controller firmware embedded in Volco.exe by CI (spec §9.3): resources named
/// "Firmware/esp32/…" (Volco.Windows.csproj). A local build has none.</summary>
public sealed class EmbeddedFirmwareSource : IFirmwareSource
{
    public Stream? Open(string path)
    {
        var assembly = typeof(EmbeddedFirmwareSource).Assembly;
        var name = $"Firmware/{path}";
        // The csproj names them with '/'; tolerate a '\' from a build that used RecursiveDir.
        return assembly.GetManifestResourceStream(name)
               ?? assembly.GetManifestResourceNames().Where(n => n.Replace('\\', '/') == name)
                          .Select(assembly.GetManifestResourceStream).FirstOrDefault();
    }
}
