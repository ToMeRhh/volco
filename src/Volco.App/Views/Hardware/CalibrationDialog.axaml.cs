using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Volco.App.ViewModels.Hardware;

namespace Volco.App.Views.Hardware;

/// <summary>The Re-calibrate sweep over <see cref="CalibrationDialogViewModel"/>: bindings do the
/// work, except the bar's fill, which spans the learned range of the track's width.</summary>
public partial class CalibrationDialog : UserControl
{
    private readonly Border _track;
    private readonly Border _fill;
    private CalibrationDialogViewModel? _vm;

    public CalibrationDialog()
    {
        AvaloniaXamlLoader.Load(this);
        _track = this.FindControl<Border>("CalibrationTrack")!;
        _fill = this.FindControl<Border>("CalibrationFill")!;
        _track.SizeChanged += (_, _) => UpdateFill();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
        _vm = DataContext as CalibrationDialogViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        UpdateFill();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CalibrationDialogViewModel.Low) or nameof(CalibrationDialogViewModel.High)) UpdateFill();
    }

    /// <summary>The fill from the low end to the high end, as fractions of the track's inner width.</summary>
    private void UpdateFill()
    {
        var width = _track.Bounds.Width - _track.BorderThickness.Left - _track.BorderThickness.Right;
        if (_vm is null || width <= 0)
        {
            _fill.Width = 0;
            return;
        }
        var low = Math.Clamp(_vm.Low, 0, 1);
        var high = Math.Clamp(_vm.High, low, 1);
        _fill.Margin = new Thickness(low * width, 0, 0, 0);
        _fill.Width = Math.Max(0, (high - low) * width);
    }
}
