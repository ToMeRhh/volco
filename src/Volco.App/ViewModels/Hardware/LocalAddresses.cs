using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Volco.Core.Diagnostics;

namespace Volco.App.ViewModels.Hardware;

/// <summary>One of this PC's IPv4 addresses and the adapter that has it (the Wi-Fi card's "This
/// PC's address", hardware-page spec §10.3).</summary>
public sealed record LocalAddress(IPAddress Address, string Adapter)
{
    /// <summary>"192.168.1.20 · Ethernet".</summary>
    public string Label => $"{Address} · {Adapter}";

    /// <summary>The address as the controller config's pc_ip: a.b.c.d = a&lt;&lt;24|b&lt;&lt;16|c&lt;&lt;8|d.</summary>
    public uint Value
    {
        get
        {
            var b = Address.GetAddressBytes();
            return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
        }
    }
}

/// <summary>This PC's addresses, read from the network adapters.</summary>
public static class LocalAddresses
{
    /// <summary>The IPv4 unicast addresses of every adapter that is up, loopback left out, in the
    /// system's adapter order. Empty when the adapters can't be read.</summary>
    public static IReadOnlyList<LocalAddress> Read()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses
                    .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
                    .Select(a => new LocalAddress(a.Address, n.Name)))
                .ToList();
        }
        catch (NetworkInformationException e)
        {
            AppLog.Info($"Couldn't read this PC's addresses: {e.Message}");
            return Array.Empty<LocalAddress>();
        }
    }
}
