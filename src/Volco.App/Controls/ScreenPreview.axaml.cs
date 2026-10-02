using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Volco.Core.Communication;

namespace Volco.App.Controls;

/// <summary>Fills a preset dot: lit for the selected preset.</summary>
public static class ScreenDotFill
{
    public static readonly IValueConverter Instance = new FuncValueConverter<bool, IBrush>(
        on => on ? new SolidColorBrush(Color.Parse("#E8F0FF")) : Brushes.Transparent);
}

/// <summary>What one controller screen shows (spec §6.3): a preview of layout 1.</summary>
public partial class ScreenPreview : UserControl
{
    public static readonly StyledProperty<ScreenContent?> ScreenProperty =
        AvaloniaProperty.Register<ScreenPreview, ScreenContent?>(nameof(Screen));

    public static readonly StyledProperty<double> ScaleProperty =
        AvaloniaProperty.Register<ScreenPreview, double>(nameof(Scale), 1.0);

    private readonly TextBlock _top, _main;
    private readonly ItemsControl _dots;
    private readonly Image _icon;
    private readonly Panel _slash;
    private readonly LayoutTransformControl _scaler;

    public ScreenPreview()
    {
        AvaloniaXamlLoader.Load(this);
        _top = this.FindControl<TextBlock>("TopText")!;
        _main = this.FindControl<TextBlock>("MainText")!;
        _dots = this.FindControl<ItemsControl>("TopDots")!;
        _icon = this.FindControl<Image>("IconImage")!;
        _slash = this.FindControl<Panel>("Slash")!;
        _scaler = this.FindControl<LayoutTransformControl>("Scaler")!;
        _top.FontFamily = _main.FontFamily = ScreenIcons.Font;
        Apply();
    }

    public ScreenContent? Screen
    {
        get => GetValue(ScreenProperty);
        set => SetValue(ScreenProperty, value);
    }

    public double Scale
    {
        get => GetValue(ScaleProperty);
        set => SetValue(ScaleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ScreenProperty || change.Property == ScaleProperty) Apply();
    }

    private void Apply()
    {
        if (_scaler is null) return;   // during XAML load
        _scaler.LayoutTransform = new ScaleTransform(Scale, Scale);
        var c = Screen ?? ScreenContent.Blank;
        _top.Text = c.TopText;
        _top.IsVisible = c.TopKind == TopKind.Text;
        _dots.IsVisible = c.TopKind == TopKind.Dots;
        _dots.ItemsSource = Enumerable.Range(0, c.DotsCount).Select(i => i == c.DotsSelected).ToList();
        _icon.IsVisible = c.Icon is not null;
        _icon.Source = c.Icon is { } icon ? ScreenIcons.Bitmap(icon) : null;
        _slash.IsVisible = c.Icon is not null && c.Muted;
        _main.Text = c.Main;
    }
}
