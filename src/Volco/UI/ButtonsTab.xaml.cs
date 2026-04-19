using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public sealed partial class ButtonsTab : Page
{
    private ConfigManager? _configManager;
    private IAudioManager? _audioManager;
    private bool _isLoading = true;

    public ButtonsTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        if (e.Parameter is ValueTuple<ConfigManager, IAudioManager> tuple)
        {
            _configManager = tuple.Item1;
            _audioManager = tuple.Item2;
        }
        if (_configManager == null) return;

        _isLoading = true;
        BuildButtonPanels();
        _isLoading = false;
    }

    private void BuildButtonPanels()
    {
        ButtonsPanel.Children.Clear();
        var devices = _audioManager?.GetOutputDevices() ?? new();

        foreach (var button in _configManager!.Config.Buttons)
        {
            var card = new Border
            {
                Padding = new Thickness(12),
                Margin = new Thickness(0, 0, 0, 8),
                CornerRadius = new CornerRadius(8),
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1)
            };

            var stack = new StackPanel { Spacing = 8 };
            stack.Children.Add(new TextBlock
            {
                Text = $"Button {button.Index}",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"]
            });

            stack.Children.Add(CreateActionRow("Single Press", button.Index, "single", button.SinglePress, devices));
            stack.Children.Add(CreateActionRow("Double Press", button.Index, "double", button.DoublePress, devices));
            stack.Children.Add(CreateActionRow("Long Press", button.Index, "long", button.LongPress, devices));

            card.Child = stack;
            ButtonsPanel.Children.Add(card);
        }
    }

    private StackPanel CreateActionRow(string label, int buttonIndex, string pressTag,
        ButtonActionConfig actionConfig, List<AudioDeviceInfo> devices)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };

        row.Children.Add(new TextBlock { Text = label, Width = 100, VerticalAlignment = VerticalAlignment.Center });

        var actionCombo = new ComboBox { Width = 200, Tag = $"{buttonIndex}:{pressTag}" };
        foreach (ButtonActionType actionType in Enum.GetValues<ButtonActionType>())
        {
            var item = new ComboBoxItem { Content = FormatActionName(actionType), Tag = actionType.ToString() };
            actionCombo.Items.Add(item);
            if (actionType == actionConfig.Action)
                actionCombo.SelectedItem = item;
        }
        actionCombo.SelectionChanged += Action_Changed;
        row.Children.Add(actionCombo);

        var deviceCombo = new ComboBox { Width = 200, Tag = $"{buttonIndex}:{pressTag}:device", PlaceholderText = "Select device" };
        foreach (var device in devices)
        {
            var item = new ComboBoxItem { Content = device.Name, Tag = device.Name };
            deviceCombo.Items.Add(item);
            if (device.Name == actionConfig.DeviceId)
                deviceCombo.SelectedItem = item;
        }
        deviceCombo.SelectionChanged += Device_Changed;
        var needsDevice = actionConfig.Action is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
        deviceCombo.Visibility = needsDevice ? Visibility.Visible : Visibility.Collapsed;
        row.Children.Add(deviceCombo);

        return row;
    }

    private void Action_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is not ComboBox combo || combo.Tag is not string tag) return;
        if (combo.SelectedItem is not ComboBoxItem selected || selected.Tag is not string actionTag) return;

        var parts = tag.Split(':');
        var buttonIndex = int.Parse(parts[0]);
        var pressType = parts[1];
        var actionType = Enum.Parse<ButtonActionType>(actionTag);

        var buttonConfig = _configManager.Config.Buttons.First(b => b.Index == buttonIndex);
        var actionConfig = pressType switch
        {
            "single" => buttonConfig.SinglePress,
            "double" => buttonConfig.DoublePress,
            "long" => buttonConfig.LongPress,
            _ => throw new ArgumentException($"Unknown press type: {pressType}")
        };
        actionConfig.Action = actionType;

        // Show/hide device picker
        var parent = combo.Parent as StackPanel;
        var deviceCombo = parent?.Children.OfType<ComboBox>().LastOrDefault();
        if (deviceCombo != null)
        {
            var needsDevice = actionType is ButtonActionType.MuteSpecificDevice or ButtonActionType.SetOutputDevice;
            deviceCombo.Visibility = needsDevice ? Visibility.Visible : Visibility.Collapsed;
        }

        _configManager.Save();
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is not ComboBox combo || combo.Tag is not string tag) return;
        if (combo.SelectedItem is not ComboBoxItem selected || selected.Tag is not string deviceName) return;

        var parts = tag.Split(':');
        var buttonIndex = int.Parse(parts[0]);
        var pressType = parts[1];

        var buttonConfig = _configManager.Config.Buttons.First(b => b.Index == buttonIndex);
        var actionConfig = pressType switch
        {
            "single" => buttonConfig.SinglePress,
            "double" => buttonConfig.DoublePress,
            "long" => buttonConfig.LongPress,
            _ => throw new ArgumentException($"Unknown press type: {pressType}")
        };
        actionConfig.DeviceId = deviceName;
        _configManager.Save();
    }

    private static string FormatActionName(ButtonActionType type) => type switch
    {
        ButtonActionType.None => "None",
        ButtonActionType.MuteSpecificDevice => "Mute Specific Device",
        ButtonActionType.MuteCurrentOutput => "Mute Current Output",
        ButtonActionType.MuteCurrentInput => "Mute Current Input",
        ButtonActionType.ToggleOutputDevice => "Toggle Output Device",
        ButtonActionType.SetOutputDevice => "Set Output Device",
        _ => type.ToString()
    };
}
