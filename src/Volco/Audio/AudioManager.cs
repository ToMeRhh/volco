using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Volco.Audio;

public class AudioManager : IAudioManager
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<uint, (string Name, DateTime CachedAt)> _processNameCache = new();
    private static readonly TimeSpan ProcessCacheTtl = TimeSpan.FromSeconds(5);

    public void SetProcessVolume(string processName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var sw = Stopwatch.StartNew();
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        AppLog.Debug($"[Audio] SetProcessVolume({processName}, {volume:F3}): GetDefaultAudioEndpoint+Sessions in {sw.ElapsedMilliseconds}ms, sessionCount={sessions.Count}");

        int matched = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                session.SimpleAudioVolume.Volume = volume;
                matched++;
            }
        }
        AppLog.Debug($"[Audio] SetProcessVolume({processName}): matched={matched}, total={sw.ElapsedMilliseconds}ms");
    }

    public void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var sw = Stopwatch.StartNew();
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;
        AppLog.Debug($"[Audio] SetProcessVolumes([{string.Join(",", processNames)}], {volume:F3}): GetDefaultAudioEndpoint+Sessions in {sw.ElapsedMilliseconds}ms, sessionCount={sessions.Count}");

        int matched = 0;
        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.IsNullOrEmpty(sessionProcessName)) continue;

            foreach (var processName in processNames)
            {
                if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
                {
                    session.SimpleAudioVolume.Volume = volume;
                    matched++;
                    break;
                }
            }
        }
        AppLog.Debug($"[Audio] SetProcessVolumes: matched={matched}, total={sw.ElapsedMilliseconds}ms");
    }

    public void SetDeviceVolume(float volume, string? deviceName = null)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var sw = Stopwatch.StartNew();
        MMDevice? device;
        if (!string.IsNullOrEmpty(deviceName))
        {
            device = FindDeviceByName(DataFlow.Render, deviceName);
        }
        else
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        AppLog.Debug($"[Audio] SetDeviceVolume({deviceName ?? "default"}, {volume:F3}): device lookup in {sw.ElapsedMilliseconds}ms, found={device != null}");

        if (device != null)
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
        }
        AppLog.Debug($"[Audio] SetDeviceVolume({deviceName ?? "default"}): total={sw.ElapsedMilliseconds}ms");
    }

    public void ToggleMuteOutput(string? deviceId = null)
    {
        MMDevice? device;
        if (deviceId != null)
        {
            device = FindDeviceByName(DataFlow.Render, deviceId);
            if (device == null) return;
        }
        else
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        if (device != null)
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void ToggleMuteInput()
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void SetDefaultOutputDevice(string deviceName)
    {
        var device = FindDeviceByName(DataFlow.Render, deviceName);
        if (device == null) return;

        var policyConfig = (IPolicyConfig)new PolicyConfigClient();
        policyConfig.SetDefaultEndpoint(device.ID, (int)Role.Multimedia);
        policyConfig.SetDefaultEndpoint(device.ID, (int)Role.Communications);
    }

    public string? GetDefaultOutputDeviceName()
    {
        try
        {
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch
        {
            return null;
        }
    }

    public List<AudioDeviceInfo> GetOutputDevices()
    {
        return _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)
            .Select(d => new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName })
            .ToList();
    }

    public List<AudioDeviceInfo> GetInputDevices()
    {
        return _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => new AudioDeviceInfo { Id = d.ID, Name = d.FriendlyName })
            .ToList();
    }

    public List<string> GetActiveProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            var name = GetProcessName(sessions[i]);
            if (!string.IsNullOrEmpty(name))
                names.Add(name);
        }

        return names.ToList();
    }

    public bool IsOutputMuted(string? deviceId = null)
    {
        try
        {
            MMDevice? device;
            if (deviceId != null)
            {
                device = FindDeviceByName(DataFlow.Render, deviceId);
                if (device == null) return false;
            }
            else
            {
                device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            return device.AudioEndpointVolume.Mute;
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
            var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return device.AudioEndpointVolume.Mute;
        }
        catch
        {
            return false;
        }
    }

    private MMDevice? FindDeviceByName(DataFlow flow, string name)
    {
        return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
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

            var sw = Stopwatch.StartNew();
            var process = System.Diagnostics.Process.GetProcessById((int)pid);
            var name = process.ProcessName + ".exe";
            _processNameCache[pid] = (name, now);
            AppLog.Debug($"[Audio] GetProcessName cache miss: pid={pid} -> {name} in {sw.ElapsedMilliseconds}ms");
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
