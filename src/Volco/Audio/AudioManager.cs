using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace Volco.Audio;

public class AudioManager : IAudioManager
{
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly Dictionary<uint, (string Name, DateTime CachedAt)> _processNameCache = new();
    private readonly Dictionary<string, MMDevice> _deviceCache = new(StringComparer.OrdinalIgnoreCase);
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

        if (device == null) return;

        try
        {
            device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            InvalidateDeviceCache();
            device = !string.IsNullOrEmpty(deviceName)
                ? FindDeviceByName(DataFlow.Render, deviceName)
                : _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (device != null)
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

        if (device == null) return;

        try
        {
            device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            InvalidateDeviceCache();
        }
    }

    public void ToggleMuteInput()
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void SetDefaultOutputDevice(string deviceName)
    {
        AppLog.Info($"[Audio] SetDefaultOutputDevice called: deviceName='{deviceName}'");
        var device = FindDeviceByName(DataFlow.Render, deviceName);
        if (device == null)
        {
            AppLog.Error($"[Audio] SetDefaultOutputDevice: device not found for '{deviceName}'");
            return;
        }

        // PolicyConfigClient COM object requires STA. Button press callbacks arrive
        // on thread pool threads (MTA) via Task.Delay continuations, so marshal to
        // a dedicated STA thread.
        var deviceId = device.ID;
        var deviceFriendlyName = device.FriendlyName;
        AppLog.Info($"[Audio] SetDefaultOutputDevice: resolved to '{deviceFriendlyName}', id={deviceId}");

        Exception? error = null;
        int hrMultimedia = 0, hrCommunications = 0;
        var thread = new Thread(() =>
        {
            try
            {
                var policyConfig = (IPolicyConfig)new PolicyConfigClient();
                hrMultimedia = policyConfig.SetDefaultEndpoint(deviceId, (int)Role.Multimedia);
                hrCommunications = policyConfig.SetDefaultEndpoint(deviceId, (int)Role.Communications);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (error != null)
        {
            AppLog.Error($"[Audio] SetDefaultOutputDevice exception: {error.GetType().Name}: {error.Message}");
        }
        else if (hrMultimedia != 0 || hrCommunications != 0)
        {
            AppLog.Error($"[Audio] SetDefaultOutputDevice HRESULT failure: multimedia=0x{hrMultimedia:X8}, communications=0x{hrCommunications:X8}");
        }
        else
        {
            AppLog.Info($"[Audio] SetDefaultOutputDevice succeeded: '{deviceFriendlyName}'");
        }
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
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            InvalidateDeviceCache();
            return false;
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
        var cacheKey = $"{flow}:{name}";
        if (_deviceCache.TryGetValue(cacheKey, out var cached))
        {
            AppLog.Debug($"[Audio] FindDeviceByName: cache hit for '{name}' -> '{cached.FriendlyName}' (id={cached.ID})");
            return cached;
        }

        var allDevices = _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active);
        var deviceNames = allDevices.Select(d => d.FriendlyName).ToList();
        AppLog.Debug($"[Audio] FindDeviceByName: searching for '{name}' in [{string.Join(", ", deviceNames)}]");

        var device = allDevices
            .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (device != null)
        {
            _deviceCache[cacheKey] = device;
            AppLog.Debug($"[Audio] FindDeviceByName: found '{device.FriendlyName}' (id={device.ID})");
        }
        else
        {
            AppLog.Error($"[Audio] FindDeviceByName: no device matched '{name}'");
        }

        return device;
    }

    private void InvalidateDeviceCache()
    {
        _deviceCache.Clear();
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
