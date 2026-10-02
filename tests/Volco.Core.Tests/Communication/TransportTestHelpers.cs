using System.Net;
using System.Net.Sockets;

namespace Volco.Core.Tests.Communication;

internal static class TransportTestHelpers
{
    /// <summary>Asks the OS for a free TCP port (bind to 0, read it back, release it).</summary>
    public static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    /// <summary>Asks the OS for a free UDP port (bind to 0, read it back, release it).</summary>
    public static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    public static byte[] Frame(params byte[] payload)
    {
        byte checksum = 0;
        foreach (var b in payload) checksum ^= b;
        var frame = new byte[payload.Length + 3];
        frame[0] = 0xAA;
        frame[1] = (byte)payload.Length;
        Array.Copy(payload, 0, frame, 2, payload.Length);
        frame[^1] = checksum;
        return frame;
    }

    /// <summary>Bounded polling of an async probe until it returns <paramref name="expected"/>.</summary>
    public static async Task<bool> WaitUntilAsync(Func<Task<bool>> probe, bool expected, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (await probe() == expected) return true;
            await Task.Delay(20);
        }
        return await probe() == expected;
    }

    /// <summary>Bounded polling; returns whether the condition became true within the timeout.</summary>
    public static async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}
