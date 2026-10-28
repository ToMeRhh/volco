using Volco.Core.Communication;
using Xunit;

namespace Volco.Core.Tests.Communication;

public class SerialPortTextTests
{
    [Theory]
    [InlineData("USB-SERIAL CH340 (COM3)", "COM3", "USB-SERIAL CH340")]
    [InlineData("Standard Serial over Bluetooth link (COM5)", "COM5", "Standard Serial over Bluetooth link")]
    [InlineData("USB-SERIAL CH340 (COM3)", "COM4", "USB-SERIAL CH340 (COM3)")]
    [InlineData("Plain name", "COM1", "Plain name")]
    public void StripComSuffix_removes_only_the_trailing_port(string friendly, string port, string expected)
        => Assert.Equal(expected, SerialPortText.StripComSuffix(friendly, port));

    [Theory]
    [InlineData(@"USB\VID_1A86&PID_7523&REV_0264", 0x1A86, 0x7523)]
    [InlineData(@"usb\vid_10c4&pid_ea60", 0x10C4, 0xEA60)]
    [InlineData(@"FTDIBUS\VID_0403+PID_6001+A50285BIA\0000", 0x0403, 0x6001)]
    public void TryParseUsbIds_reads_hex_ids(string id, int vid, int pid)
    {
        Assert.True(SerialPortText.TryParseUsbIds(id, out var v, out var p));
        Assert.Equal((vid, pid), (v, p));
    }

    [Theory]
    [InlineData(@"BTHENUM\{00001101-0000-1000-8000-00805F9B34FB}_LOCALMFG&0000")]
    [InlineData(@"ACPI\PNP0501")]
    [InlineData("")]
    public void TryParseUsbIds_rejects_non_usb(string id)
        => Assert.False(SerialPortText.TryParseUsbIds(id, out _, out _));
}
