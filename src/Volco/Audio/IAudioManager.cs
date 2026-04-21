namespace Volco.Audio;

public class AudioDeviceInfo
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
}

public interface IAudioManager
{
    void SetProcessVolume(string processName, float volume);
    void SetProcessVolumes(IReadOnlyCollection<string> processNames, float volume);
    void SetDeviceVolume(float volume, string? deviceName = null);
    void ToggleMuteOutput(string? deviceId = null);
    void ToggleMuteInput();
    void SetDefaultOutputDevice(string deviceName);
    string? GetDefaultOutputDeviceName();
    List<AudioDeviceInfo> GetOutputDevices();
    List<AudioDeviceInfo> GetInputDevices();
    List<string> GetActiveProcessNames();
    bool IsOutputMuted(string? deviceId = null);
    bool IsInputMuted();
}
