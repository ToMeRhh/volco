using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// ViewModel for the Buttons settings tab.
/// Owns a collection of <see cref="ButtonItemViewModel"/>, one per configured button.
/// </summary>
public partial class ButtonsViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IAudioEngine _audio;

    public ObservableCollection<ButtonItemViewModel> Items { get; } = new();

    public ButtonsViewModel(ConfigManager config, IAudioEngine audio)
    {
        _config = config;
        _audio = audio;
        BuildItems();
    }

    private void BuildItems()
    {
        Items.Clear();
        var devices = _audio.GetDevices(AudioFlow.Render);
        foreach (var buttonCfg in _config.Config.Buttons)
        {
            Items.Add(new ButtonItemViewModel(buttonCfg, _audio, devices, _config, () => _config.RequestSave()));
        }
    }

    /// <summary>
    /// Re-seeds every button row's device picker from a fresh device snapshot (Bug 3). Callers
    /// must already be on the UI thread — see SettingsViewModel.RefreshDevices, which marshals
    /// the single <see cref="IAudioEngine.DevicesChanged"/> subscription (audio thread) before
    /// calling this.
    /// </summary>
    public void RefreshDevices(IReadOnlyList<AudioDevice> devices)
    {
        foreach (var item in Items)
            item.RefreshDevices(devices);
    }

    /// <summary>Human-readable label for a button action type.</summary>
    public static string FormatActionName(ButtonActionType type) => type switch
    {
        ButtonActionType.None               => "None",
        ButtonActionType.MuteSpecificDevice => "Mute Specific Device",
        ButtonActionType.MuteCurrentOutput  => "Mute Current Output",
        ButtonActionType.MuteCurrentInput   => "Mute Current Input",
        ButtonActionType.ToggleOutputDevice => "Toggle Output Device",
        ButtonActionType.SetOutputDevice    => "Set Output Device",
        _                                   => type.ToString()
    };
}
