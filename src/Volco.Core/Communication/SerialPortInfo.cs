using System.IO.Ports;

namespace Volco.Core.Communication;

/// <summary>A serial port as the system lists it: its name and, where the platform knows it,
/// the friendly name and USB ids.</summary>
public sealed record SerialPortDescription(string Name, string? Description = null, int? VendorId = null, int? ProductId = null)
{
    /// <summary>Whether the port is a USB device (its vendor id is known).</summary>
    public bool IsUsb => VendorId is not null;

    /// <summary>The DIY controller's USB serial chip, 1a86:7523.</summary>
    public bool IsCh340 => VendorId == 0x1A86 && ProductId == 0x7523;
}

/// <summary>Seam: the ports with their descriptions and USB ids (SetupAPI on Windows).</summary>
public interface ISerialPortInfo
{
    IReadOnlyList<SerialPortDescription> List();
}

/// <summary>The serial ports of this machine: the seam's list, merged with
/// <see cref="SerialPort.GetPortNames"/> so a port the seam misses is still listed by name.
/// Without the seam, names only.</summary>
public sealed class SystemSerialPorts(ISerialPortInfo? info) : ISerialPortInfo
{
    public IReadOnlyList<SerialPortDescription> List()
    {
        IReadOnlyList<SerialPortDescription> described = [];
        try { described = info?.List() ?? []; }
        catch { /* the names alone still work */ }
        string[] names;
        try { names = SerialPort.GetPortNames(); }
        catch { names = []; }
        return Merge(described, names);
    }

    internal static IReadOnlyList<SerialPortDescription> Merge(
        IReadOnlyList<SerialPortDescription> described, IEnumerable<string> names)
    {
        var all = described.ToList();
        foreach (var name in names)
            if (!all.Any(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)))
                all.Add(new SerialPortDescription(name));
        return all.OrderBy(p => p, SerialPortOrder.ByName).ToList();
    }
}

/// <summary>The order Automatic tries ports in (spec §10): the CH340, then the last port that
/// worked, then the rest by name.</summary>
public static class SerialPortOrder
{
    /// <summary>"COM3" before "COM10" (the number), then ordinal.</summary>
    internal static readonly IComparer<SerialPortDescription> ByName =
        Comparer<SerialPortDescription>.Create((a, b) =>
        {
            var (an, bn) = (Number(a.Name), Number(b.Name));
            if (an != bn) return an.CompareTo(bn);
            return string.CompareOrdinal(a.Name, b.Name);
        });

    private static int Number(string name)
    {
        var i = name.Length;
        while (i > 0 && char.IsDigit(name[i - 1])) i--;
        return i < name.Length && int.TryParse(name.AsSpan(i), out var n) ? n : int.MaxValue;
    }

    /// <summary>The ports worth trying, in order. With the system's descriptions only USB ports
    /// count (a Bluetooth port never holds the controller); without them every port does.</summary>
    public static IReadOnlyList<SerialPortDescription> Candidates(
        IReadOnlyList<SerialPortDescription> ports, string? lastPort, bool haveInfo)
    {
        var pool = haveInfo ? ports.Where(p => p.IsUsb) : ports;
        return pool
            .OrderBy(p => p.IsCh340 ? 0 : string.Equals(p.Name, lastPort, StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .ThenBy(p => p, ByName)
            .ToList();
    }

    /// <summary>The port to name in "Automatic (COM3)": the CH340, else the only candidate, else none.</summary>
    public static SerialPortDescription? BestCandidate(IReadOnlyList<SerialPortDescription> candidates)
        => candidates.FirstOrDefault(p => p.IsCh340) ?? (candidates.Count == 1 ? candidates[0] : null);
}
