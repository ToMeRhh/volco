using NAudio.CoreAudioApi;

namespace Volco.Audio;

public class AudioManager : IAudioManager
{
    private readonly MMDeviceEnumerator _enumerator = new();

    public void SetProcessVolume(string processName, float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var sessions = device.AudioSessionManager.Sessions;

        for (int i = 0; i < sessions.Count; i++)
        {
            var session = sessions[i];
            var sessionProcessName = GetProcessName(session);
            if (string.Equals(sessionProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                session.SimpleAudioVolume.Volume = volume;
            }
        }
    }

    public void SetDeviceVolume(float volume)
    {
        volume = Math.Clamp(volume, 0f, 1f);
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        device.AudioEndpointVolume.MasterVolumeLevelScalar = volume;
    }

    public void ToggleMuteOutput(string? deviceId = null)
    {
        MMDevice device;
        if (deviceId != null)
        {
            device = FindDeviceByName(DataFlow.Render, deviceId);
            if (device == null) return;
        }
        else
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void ToggleMuteInput()
    {
        var device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
        device.AudioEndpointVolume.Mute = !device.AudioEndpointVolume.Mute;
    }

    public void SetDefaultOutputDevice(string deviceName)
    {
        // NAudio doesn't directly support changing the default device.
        // Use the AudioSwitcher library for this.
        var controller = new AudioSwitcher.AudioApi.CoreAudio.CoreAudioController();
        var devices = controller.GetPlaybackDevices();
        var target = devices.FirstOrDefault(d =>
            d.FullName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));
        target?.SetAsDefault();
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

    private MMDevice? FindDeviceByName(DataFlow flow, string name)
    {
        return _enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .FirstOrDefault(d => d.FriendlyName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetProcessName(AudioSessionControl session)
    {
        try
        {
            var pid = (int)session.GetProcessID;
            if (pid == 0) return "";
            var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }
}
