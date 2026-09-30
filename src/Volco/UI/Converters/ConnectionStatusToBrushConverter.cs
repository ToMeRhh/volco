using System;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Volco.Core.Communication;

namespace Volco.UI.Converters;

/// <summary>
/// Converts a <see cref="ConnectionStatus"/> value to a <see cref="SolidColorBrush"/>
/// for the live status indicator dot.
///   Connected    → green  (#FF22B14C)
///   Error        → red    (#FFE81123)
///   Disconnected → gray   (#FF888888)
/// </summary>
public sealed class ConnectionStatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is ConnectionStatus status)
        {
            return status switch
            {
                ConnectionStatus.Connected    => new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x22, 0xB1, 0x4C)),
                ConnectionStatus.Error        => new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0xE8, 0x11, 0x23)),
                ConnectionStatus.Disconnected => new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88)),
                _                             => new SolidColorBrush(global::Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88)),
            };
        }

        return new SolidColorBrush(Colors.Gray);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
