using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Audio;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Devices;

namespace Volco.App.ViewModels;

/// <summary>One device's nickname and icon, as the device dialog edits them.</summary>
public sealed partial class DeviceItemViewModel : ObservableObject
{
    private readonly DevicesViewModel _owner;
    private readonly ScreenIcon _automaticIcon;
    private bool _loading;

    public string Id { get; }
    public AudioFlow Flow { get; }
    public string WindowsName { get; }
    public string AutoName { get; }
    public bool IsConnected { get; }
    public bool IsDefault { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName), nameof(IsAutoName), nameof(Preview), nameof(NicknameFlowDirection))]
    private string _nickname = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    private ScreenIcon _icon;

    [ObservableProperty]
    private bool _isExpanded;

    public IReadOnlyList<IconChoiceViewModel> IconChoices { get; }

    internal DeviceItemViewModel(DevicesViewModel owner, string id, AudioFlow flow, string windowsName,
                                 ScreenIcon automaticIcon, bool isConnected, bool isDefault)
    {
        _owner = owner;
        Id = id;
        Flow = flow;
        WindowsName = windowsName;
        AutoName = DeviceDisplay.AutoShortName(windowsName);
        IsConnected = isConnected;
        IsDefault = isDefault;
        _automaticIcon = automaticIcon;

        var settings = owner.Config.FindDevice(id);
        _loading = true;
        Nickname = settings?.Nickname ?? "";
        Icon = settings?.Icon ?? automaticIcon;
        _loading = false;

        IconChoices = ScreenIconSets.OfferedOnDevicesPage
            .Select(i => new IconChoiceViewModel(i, choice => Icon = choice)).ToList();
        UpdateIconSelection();
    }

    public string DisplayName => Nickname.Trim().Length > 0 ? Nickname.Trim() : AutoName;
    public bool IsAutoName => Nickname.Trim().Length == 0;
    public string NicknameHint => $"Leave empty to use “{AutoName}”.";

    /// <summary>A Hebrew (or other right-to-left) nickname is typed right to left (spec §6.1).</summary>
    public FlowDirection NicknameFlowDirection
        => Nickname.FirstOrDefault(char.IsLetter) is var c && c >= '֐' && c <= 'ࣿ'
            ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public string StatusText => IsDefault ? (Flow == AudioFlow.Render ? "Default output" : "Default input")
                              : !IsConnected ? "Unplugged" : "";
    public bool HasStatus => StatusText.Length > 0;

    /// <summary>The device as a "Switch output" button's screen would show it.</summary>
    public ScreenContent Preview
        => ScreenContent.Text(IsDefault && Flow == AudioFlow.Render ? ControllerDisplay.Active : "", Icon, false, DisplayName);

    public string UsedBy => _owner.UsedBy(Id);
    public bool CanForget => !IsConnected && UsedBy.Length == 0;
    public bool ShowUsedBy => !IsConnected && UsedBy.Length > 0;
    /// <summary>Re-reads "Used by" (a slider or button action changed on another page).</summary>
    internal void RefreshUsage()
    {
        OnPropertyChanged(nameof(UsedBy));
        OnPropertyChanged(nameof(CanForget));
        OnPropertyChanged(nameof(ShowUsedBy));
    }

    public string FooterText => $"Windows name: {WindowsName} · {(Flow == AudioFlow.Render ? "Output" : "Input")}";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Forget() => _owner.Forget(this);

    /// <summary>Back to the automatic icon.</summary>
    [RelayCommand]
    private void ResetIcon()
    {
        _loading = true;
        Icon = _automaticIcon;
        _loading = false;
        Store(iconChosen: false);
    }

    partial void OnNicknameChanged(string value)
    {
        if (!_loading) Store(iconChosen: _owner.Config.FindDevice(Id)?.Icon is not null);
    }

    partial void OnIconChanged(ScreenIcon value)
    {
        UpdateIconSelection();
        if (!_loading) Store(iconChosen: true);
    }

    private void UpdateIconSelection()
    {
        if (IconChoices is null) return;
        foreach (var c in IconChoices) c.IsSelected = c.Icon == Icon;
    }

    /// <summary>Writes this device's entry; removes it once it holds nothing (spec §5.1).</summary>
    private void Store(bool iconChosen)
    {
        var devices = _owner.Config.Devices;
        var entry = _owner.Config.FindDevice(Id);
        if (entry is null)
        {
            entry = new DeviceSettingsConfig { Id = Id, Flow = Flow, FriendlyName = WindowsName };
            devices.Add(entry);
        }
        entry.Nickname = Nickname.Trim();
        entry.Icon = iconChosen ? Icon : null;
        if (entry.IsEmpty) devices.Remove(entry);
        _owner.Save();
    }
}

/// <summary>One icon in a device's icon picker.</summary>
public sealed partial class IconChoiceViewModel : ObservableObject
{
    private readonly Action<ScreenIcon> _select;

    public IconChoiceViewModel(ScreenIcon icon, Action<ScreenIcon> select)
    {
        Icon = icon;
        _select = select;
    }

    public ScreenIcon Icon { get; }
    public string Label => Icon switch
    {
        ScreenIcon.Speakers => "Speaker pair",
        ScreenIcon.Display => "Monitor / TV",
        ScreenIcon.Mic => "Microphone",
        ScreenIcon.StudioMic => "Studio mic",
        _ => Icon.ToString(),
    };

    [ObservableProperty]
    private bool _isSelected;

    [RelayCommand]
    private void Select() => _select(Icon);
}
