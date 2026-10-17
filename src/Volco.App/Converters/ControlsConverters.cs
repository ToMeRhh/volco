using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Volco.Core.Config;
using Volco.Core.Input;

namespace Volco.App.Converters;

/// <summary>Icons on the Controls page, from the vector art in Styles/Icons.axaml.</summary>
public static class IconConverters
{
    /// <summary>A resource key ("Icon.Speaker") → its geometry.</summary>
    public static readonly IValueConverter Resource = new FuncValueConverter<string?, Geometry?>(Find);

    /// <summary>A <see cref="ButtonActionType"/> name → its action's icon.</summary>
    public static readonly IValueConverter Action = new FuncValueConverter<string?, Geometry?>(name
        => Enum.TryParse<ButtonActionType>(name, out var type) ? Find(KeyOf(type)) : null);

    public static string KeyOf(ButtonActionType type) => type switch
    {
        ButtonActionType.MuteCurrentOutput or ButtonActionType.MuteSpecificDevice => "Icon.Muted",
        ButtonActionType.MuteCurrentInput => "Icon.Device.Mic",
        ButtonActionType.SetOutputDevice => "Icon.Speaker",
        ButtonActionType.ToggleOutputDevice => "Icon.Refresh",
        ButtonActionType.NextPreset or ButtonActionType.GoToPreset => "Icon.Preset",
        ButtonActionType.MediaPlayPause => "Icon.Device.Play",
        ButtonActionType.MediaNext => "Icon.Device.Next",
        ButtonActionType.MediaPrevious => "Icon.Device.Previous",
        ButtonActionType.MediaStop => "Icon.Stop",
        ButtonActionType.OpenProgram => "Icon.Device.App",
        ButtonActionType.RunScript => "Icon.Device.Script",
        _ => "Icon.Plus",
    };

    private static Geometry? Find(string? key)
        => key is not null && Application.Current is { } app && app.TryFindResource(key, out var value) ? value as Geometry : null;
}

/// <summary>The gesture mark before a button tile's other actions: •• double press, ▬ hold.</summary>
public static class GestureMarkConverter
{
    public static readonly IValueConverter Instance = new FuncValueConverter<PressType, string>(g => g switch
    {
        PressType.Double => "••",
        PressType.Long => "▬",
        _ => "•",
    });
}

/// <summary>The settings window's compact layout, below 1000 px wide (spec §3.1).</summary>
public static class LayoutConverters
{
    public const double CompactBelow = 1000;

    public static readonly IValueConverter IsCompact = new FuncValueConverter<double, bool>(w => w > 0 && w < CompactBelow);
}

/// <summary>A slider tile's fader: values are the live level (0..1) and the track's height.
/// The parameter is what the moving part leaves free: the knob's height, or the fill's inset.</summary>
public sealed class FaderConverter : IMultiValueConverter
{
    /// <summary>The knob's bottom margin.</summary>
    public static readonly FaderConverter Knob = new(15, asMargin: true);

    /// <summary>The level fill's height.</summary>
    public static readonly FaderConverter Fill = new(6, asMargin: false);

    private readonly double _reserve;
    private readonly bool _asMargin;

    private FaderConverter(double reserve, bool asMargin)
    {
        _reserve = reserve;
        _asMargin = asMargin;
    }

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = values.Count > 0 && values[0] is double l ? Math.Clamp(l, 0, 1) : 0;
        var height = values.Count > 1 && values[1] is double h ? h : 0;
        var travel = Math.Max(0, height - _reserve) * level;
        return _asMargin ? new Thickness(0, 0, 0, travel) : travel;
    }
}
