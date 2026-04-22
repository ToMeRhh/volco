using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Volco.Audio;

public class AudioManager : IAudioManager
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<uint, (string Name, DateTime CachedAt)> _processNameCache = new();
    private readonly Dictionary<(DataFlow, string), (string Id, DateTime CachedAt)> _deviceIdCache = new();
    private static readonly TimeSpan ProcessCacheTtl = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DeviceCacheTtl = TimeSpan.FromSeconds(30);

    public void SetProcessVolume(string processName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        // MMDevice and SessionCollection must be disposed to avoid COM resource leaks
        using var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                session.SimpleAudioVolume.Volume = volume;
            }
        }
    }

    public void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        // MMDevice and SessionCollection must be disposed to avoid COM resource leaks
        using var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.IsNullOrEmpty(sessionProcessName)) continue;

            foreach (var processName in processNames)
            {
                if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    session.SimpleAudioVolume.Volume = volume;
                    break;
                }
            }
        }
    }

    public void SetDeviceVolume(float volume, string? deviceName = null)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        using var device = !string.IsNullOrEmpty(deviceName)
            ? FindDeviceByName(DataFlow.Render, deviceName)
            : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        if (device != null)
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
        }
    }

    public void ToggleMuteOutput(string? deviceId = null)
    {
        using var device = deviceId != null
            ? FindDeviceByName(DataFlow.Render, deviceId)
            : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

        if (device != null)
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void ToggleMuteInput()
    {
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void SetDefaultOutputDevice(string deviceName)
    {
        using var device = FindDeviceByName(DataFlow.Render, deviceName);
        if (device == null) return;

        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
        policyConfig.SetDefaultEndpoint(device.ID, (int)Role.Multimedia);
        policyConfig.SetDefaultEndpoint(device.ID, (int)Role.Communications);
    }

    public string? GetDefaultOutputDeviceName()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();
        foreach (var d in devices)
        {
            result.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName });
            d.Dispose();
        }
        return result;
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        var devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        var result = new List<AudioDeviceInfo>();
        foreach (var d in devices)
        {
            result.Add(new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName });
            d.Dispose();
        }
        return result;
    }

    public List<string> GetActiveProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        // MMDevice and SessionCollection must be disposed to avoid COM resource leaks
        using var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            using var session = sessions[i];
            var name = GetProcessName(session);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return names.ToList();
    }

    public bool IsOutputMuted(string? deviceId = null)
    {
        try
        {
            using var device = deviceId != null
                ? FindDeviceByName(DataFlow.Render, deviceId)
                : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);

            return device?.AudioEndpointVolume.Mute ?? false;
        }
        catch
        {
            return false;
        }
    }

    public bool IsInputMuted()
    {
        try
        {
            using var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    private MMDevice? FindDeviceByName(DataFlow flow, string name)
    {
        var now = DateTime.UtcNow;
        // Optimization: Use cached device ID to avoid expensive COM enumeration
        if (_deviceIdCache.TryGetValue((flow, name), out var cached) && now - cached.CachedAt < DeviceCacheTtl)
        {
            try
            {
                return _enumerator.GetDevice(cached.Id);
            }
            catch
            {
                _deviceIdCache.Remove((flow, name));
            }
        }

        // COM Enumeration is O(N) and expensive; manually dispose non-matches to avoid leaks
        var devices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        MMDevice? foundDevice = null;
        foreach (var device in devices)
        {
            if (foundDevice == null && device.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                foundDevice = device;
                _deviceIdCache[(flow, name)] = (device.ID, now);
            }
            else
            {
                device.Dispose();
            }
        }

        return foundDevice;
    }

    private string GetProcessName(AudioSessionControl session)
    {
        try
        {
            var pid = (uint)session.GetProcessID;
            if (pid == 0) return "";

            var now = DateTime.UtcNow;
            if (_processNameCache.TryGetValue(pid, out var cached) && now - cached.CachedAt < ProcessCacheTtl)
                return cached.Name;

            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName + ".exe";
            _processNameCache[pid] = (name, now);
            return name;
        }
        catch
        {
            return "";
        }
    }

    // COM interop for setting default audio device (IPolicyConfig)
    [ComImport]
    [Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient { }

    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, IntPtr format);
        [PreserveSig] int GetDeviceFormat(string deviceId, int flow, IntPtr format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, IntPtr endpointFormat, IntPtr mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, int flow, out long defaultPeriod, out long minPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, long period);
        [PreserveSig] int GetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int SetShareMode(string deviceId, IntPtr mode);
        [PreserveSig] int GetPropertyValue(string deviceId, int storeType, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue(string deviceId, int storeType, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint(string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(string deviceId, int visible);
    }
}
