using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Audio;
using Volco.Config;

namespace Volco.UI;

public class SliderViewModel : INotifyPropertyChanged
{
    private string _typeTag = "Device";
    private string _processesText = "";
    private string _deviceName = "";
    private bool _isUnmapped;
    private bool _invert;

    public int Index { get; set; }

    public string TypeTag
    {
        get => _typeTag;
        set
        {
            if (_typeTag != value)
            {
                _typeTag = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsProcessesVisible));
                OnPropertyChanged(nameof(IsDeviceVisible));
                OnPropertyChanged(nameof(IsProcessesSelected));
                OnPropertyChanged(nameof(IsDeviceSelected));
            }
        }
    }

    public string ProcessesText
    {
        get => _processesText;
        set
        {
            if (_processesText != value)
            {
                _processesText = value;
                OnPropertyChanged();
            }
        }
    }

    public string DeviceName
    {
        get => _deviceName;
        set
        {
            if (_deviceName != value)
            {
                _deviceName = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsUnmapped
    {
        get => _isUnmapped;
        set
        {
            if (_isUnmapped != value)
            {
                _isUnmapped = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsProcessesInputEnabled));
            }
        }
    }

    public bool Invert
    {
        get => _invert;
        set
        {
            if (_invert != value)
            {
                _invert = value;
                OnPropertyChanged();
            }
        }
    }

    public Visibility IsProcessesVisible => TypeTag == "Processes" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsDeviceVisible => TypeTag == "Device" ? Visibility.Visible : Visibility.Collapsed;
    public bool IsProcessesInputEnabled => !IsUnmapped;

    public bool IsProcessesSelected => TypeTag == "Processes";
    public bool IsDeviceSelected => TypeTag == "Device";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class SlidersTab : Page
{
    private ConfigManager? _configManager;
    private IAudioManager? _audioManager;
    private bool _isLoading = true;
    private ObservableCollection<SliderViewModel> _viewModels = new();

    public SlidersTab()
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

        if (_configManager == null || _audioManager == null) return;

        _isLoading = true;

        _viewModels.Clear();
        foreach (var s in _configManager.Config.Sliders)
        {
            var vm = new SliderViewModel
            {
                Index = s.Index,
                TypeTag = s.Type == SliderMappingType.Unmapped ? "Processes" : s.Type.ToString(),
                IsUnmapped = s.Type == SliderMappingType.Unmapped,
                ProcessesText = string.Join(", ", s.Processes),
                DeviceName = s.DeviceName,
                Invert = s.Invert
            };
            _viewModels.Add(vm);
        }

        SliderRepeater.ItemsSource = _viewModels;
        _isLoading = false;
    }

    private void MappingType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is ComboBox combo && combo.DataContext is SliderViewModel vm)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                vm.TypeTag = tag;
                SaveSliderConfig(vm);
            }
        }
    }

    private void Processes_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_isLoading || _configManager == null || _audioManager == null) return;
        if (sender.DataContext is SliderViewModel vm)
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            {
                var input = sender.Text;
                var parts = input.Split(',').Select(p => p.Trim()).ToList();
                var lastPart = parts.LastOrDefault() ?? "";

                if (string.IsNullOrEmpty(lastPart))
                {
                    sender.ItemsSource = null;
                }
                else
                {
                    var suggestions = _audioManager.GetActiveProcessNames()
                        .Where(p => p.Contains(lastPart, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    sender.ItemsSource = suggestions;
                }
            }

            vm.ProcessesText = sender.Text;
            SaveSliderConfig(vm);
        }
    }

    private void Processes_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        var suggestion = args.SelectedItem.ToString();
        var parts = sender.Text.Split(',').Select(p => p.Trim()).ToList();
        if (parts.Count > 0)
        {
            parts[parts.Count - 1] = suggestion!;
        }
        else
        {
            parts.Add(suggestion!);
        }
        sender.Text = string.Join(", ", parts.Where(p => !string.IsNullOrEmpty(p)));
    }

    private void Unmapped_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is CheckBox cb && cb.DataContext is SliderViewModel vm)
        {
            vm.IsUnmapped = cb.IsChecked == true;
            SaveSliderConfig(vm);
        }
    }

    private void Device_Loaded(object sender, RoutedEventArgs e)
    {
        if (_audioManager == null) return;
        if (sender is ComboBox combo && combo.DataContext is SliderViewModel vm)
        {
            var devices = _audioManager.GetOutputDevices();
            combo.Items.Clear();
            foreach (var d in devices)
            {
                var item = new ComboBoxItem { Content = d.Name, Tag = d.Name };
                combo.Items.Add(item);
                if (d.Name == vm.DeviceName)
                    combo.SelectedItem = item;
            }
        }
    }

    private void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is ComboBox combo && combo.DataContext is SliderViewModel vm)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string deviceName)
            {
                vm.DeviceName = deviceName;
                SaveSliderConfig(vm);
            }
        }
    }

    private void Invert_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is CheckBox checkBox && checkBox.DataContext is SliderViewModel vm)
        {
            vm.Invert = checkBox.IsChecked == true;
            SaveSliderConfig(vm);
        }
    }

    private void SaveSliderConfig(SliderViewModel vm)
    {
        var sliderConfig = _configManager!.Config.Sliders.First(s => s.Index == vm.Index);

        if (vm.TypeTag == "Processes")
        {
            sliderConfig.Type = vm.IsUnmapped ? SliderMappingType.Unmapped : SliderMappingType.Processes;
        }
        else
        {
            sliderConfig.Type = Enum.Parse<SliderMappingType>(vm.TypeTag);
        }

        sliderConfig.Processes = vm.ProcessesText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        sliderConfig.DeviceName = vm.DeviceName;
        sliderConfig.Invert = vm.Invert;
        _configManager.Save();
    }
}
