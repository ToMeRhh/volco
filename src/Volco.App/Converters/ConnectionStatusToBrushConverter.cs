using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Volco.Core.Communication;

namespace Volco.App.Converters;

/// <summary>
/// Maps a <see cref="ConnectionStatus"/> to the status-dot brush: Connected green (#22B14C),
/// Error red (#E81123), Disconnected (and anything else) gray (#888888). Same colours as the
/// WinUI converter.
/// </summary>
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    private static readonly IBrush Green = new SolidColorBrush(Color.FromArgb(0xFF, 0x22, 0xB1, 0x4C)).ToImmutable();
    private static readonly IBrush Red = new SolidColorBrush(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23)).ToImmutable();
    private static readonly IBrush Gray = new SolidColorBrush(Color.FromArgb(0xFF, 0x88, 0x88, 0x88)).ToImmutable();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is ConnectionStatus status
            ? status switch
            {
                ConnectionStatus.Connected => Green,
                ConnectionStatus.Error => Red,
                _ => Gray,
            }
            : Gray;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
