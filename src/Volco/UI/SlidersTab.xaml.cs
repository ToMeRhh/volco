using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Volco.Config;

namespace Volco.UI;

public class SliderViewModel
{
    public int Index { get; set; }
    public string TypeTag { get; set; } = "Device";
    public string ProcessesText { get; set; } = "";
    public bool Invert { get; set; }
}

public sealed partial class SlidersTab : Page
{
    private ConfigManager? _configManager;
    private bool _isLoading = true;

    public SlidersTab()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _configManager = e.Parameter as ConfigManager;
        if (_configManager == null) return;

        _isLoading = true;
        var viewModels = _configManager.Config.Sliders.Select(s => new SliderViewModel
        {
            Index = s.Index,
            TypeTag = s.Type.ToString(),
            ProcessesText = string.Join(", ", s.Processes),
            Invert = s.Invert
        }).ToList();

        SliderRepeater.ItemsSource = viewModels;
        _isLoading = false;
    }

    private void MappingType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is ComboBox combo && combo.Tag is int index)
        {
            if (combo.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
                sliderConfig.Type = Enum.Parse<SliderMappingType>(tag);
                _configManager.Save();
            }
        }
    }

    private void Processes_Changed(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is TextBox textBox && textBox.Tag is int index)
        {
            var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
            sliderConfig.Processes = textBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            _configManager.Save();
        }
    }

    private void Invert_Changed(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _configManager == null) return;
        if (sender is CheckBox checkBox && checkBox.Tag is int index)
        {
            var sliderConfig = _configManager.Config.Sliders.First(s => s.Index == index);
            sliderConfig.Invert = checkBox.IsChecked == true;
            _configManager.Save();
        }
    }
}
