using System.Text.RegularExpressions;

namespace Volco.Core.Communication;

/// <summary>Pure text rules for a serial port's system description (used by the Windows SetupAPI reader).</summary>
public static class SerialPortText
{
    private static readonly Regex UsbIds = new(@"VID_([0-9A-F]{4})[&+]PID_([0-9A-F]{4})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The friendly name without its trailing " (COMn)" (the port's own name); the rest is kept as is.</summary>
    public static string StripComSuffix(string friendly, string port)
    {
        var suffix = $" ({port})";
        return friendly.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
            ? friendly[..^suffix.Length]
            : friendly;
    }

    /// <summary>The USB vendor and product ids in a hardware id such as <c>USB\VID_1A86&amp;PID_7523&amp;REV_0264</c>,
    /// or FTDI's <c>FTDIBUS\VID_0403+PID_6001+A50285BIA\0000</c>.</summary>
    public static bool TryParseUsbIds(string hardwareId, out int vid, out int pid)
    {
        vid = pid = 0;
        var m = UsbIds.Match(hardwareId);
        if (!m.Success) return false;
        vid = Convert.ToInt32(m.Groups[1].Value, 16);
        pid = Convert.ToInt32(m.Groups[2].Value, 16);
        return true;
    }
}
