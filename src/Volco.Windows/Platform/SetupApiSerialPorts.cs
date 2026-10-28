using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Foundation;
using Volco.Core.Communication;
using Volco.Core.Diagnostics;

namespace Volco.Windows.Platform;

/// <summary>
/// <see cref="ISerialPortInfo"/> over SetupAPI: each present device of the Ports class with its
/// port name (the device's registry key), friendly name (without " (COMn)") and the USB vendor and
/// product ids from its first hardware id. A failure is logged once and the ports read so far are returned.
/// </summary>
internal sealed class SetupApiSerialPorts : ISerialPortInfo
{
    private bool _logged;

    public IReadOnlyList<SerialPortDescription> List()
    {
        var ports = new List<SerialPortDescription>();
        try { Read(ports); }
        catch (Exception e)
        {
            if (!_logged) { _logged = true; AppLog.Error($"Serial port descriptions unavailable: {e.Message}"); }
        }
        return ports;
    }

    private static unsafe void Read(List<SerialPortDescription> ports)
    {
        var portsClass = PInvoke.GUID_DEVCLASS_PORTS;
        using var set = PInvoke.SetupDiGetClassDevs(portsClass, null, HWND.Null, SETUP_DI_GET_CLASS_DEVS_FLAGS.DIGCF_PRESENT);
        if (set.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());

        for (uint i = 0; ; i++)
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)sizeof(SP_DEVINFO_DATA) };
            if (!PInvoke.SetupDiEnumDeviceInfo(set, i, ref data)) break;

            var name = PortName(set, data);
            if (string.IsNullOrEmpty(name)) continue;
            var friendly = StringProperty(set, data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_FRIENDLYNAME, multi: false);
            var hardware = StringProperty(set, data, SETUP_DI_REGISTRY_PROPERTY.SPDRP_HARDWAREID, multi: true);

            int? vid = null, pid = null;
            if (hardware is not null && SerialPortText.TryParseUsbIds(hardware, out var v, out var p)) (vid, pid) = (v, p);
            ports.Add(new SerialPortDescription(name,
                string.IsNullOrWhiteSpace(friendly) ? null : SerialPortText.StripComSuffix(friendly, name), vid, pid));
        }
    }

    private static string? PortName(SetupDiDestroyDeviceInfoListSafeHandle set, SP_DEVINFO_DATA data)
    {
        using var key = PInvoke.SetupDiOpenDevRegKey(set, data, 1 /* DICS_FLAG_GLOBAL */, 0, 1 /* DIREG_DEV */, (uint)0x20019 /* KEY_READ */);
        if (key.IsInvalid) return null;
        using var reg = RegistryKey.FromHandle(new SafeRegistryHandle(key.DangerousGetHandle(), ownsHandle: false));
        return reg.GetValue("PortName") as string;
    }

    /// <summary>A string property; for a multi-string (the hardware ids) the first matching entry's text is
    /// returned joined, so the caller can search it.</summary>
    private static unsafe string? StringProperty(SetupDiDestroyDeviceInfoListSafeHandle set, SP_DEVINFO_DATA data, SETUP_DI_REGISTRY_PROPERTY property, bool multi)
    {
        var buffer = new byte[1024];
        uint needed;
        fixed (byte* p = buffer)
        {
            if (!PInvoke.SetupDiGetDeviceRegistryProperty((HDEVINFO)set.DangerousGetHandle(), &data, property, null, p, (uint)buffer.Length, &needed)) return null;
        }
        var text = System.Text.Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(needed, (uint)buffer.Length));
        var parts = text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        return multi ? string.Join('\n', parts) : parts[0];
    }
}
