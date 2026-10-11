using Volco.App.Converters;
using Xunit;

namespace Volco.App.Tests.Controls;

public class NumberWithUnitConverterTests
{
    private readonly NumberWithUnitConverter _seconds = new() { Unit = "s" };

    [Theory]
    [InlineData("30", 30)]
    [InlineData("30s", 30)]
    [InlineData("30 s", 30)]
    [InlineData(" 30 seconds ", 30)]
    [InlineData("5 ms", 5)]
    [InlineData("-3", -3)]
    public void Parse_ReadsTheNumber_WithOrWithoutAUnit(string text, int expected)
        => Assert.Equal((decimal)expected, _seconds.Parse(text));

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(null)]
    public void Parse_NoText_IsNoValue(string? text) => Assert.Null(_seconds.Parse(text));

    [Fact]
    public void Parse_TextWithNoNumber_Throws() => Assert.Throws<FormatException>(() => _seconds.Parse("s"));

    [Fact]
    public void Format_AddsTheUnit()
    {
        Assert.Equal("120 s", _seconds.Format(120m));
        Assert.Equal("", _seconds.Format(null));
        Assert.Equal("7", new NumberWithUnitConverter().Format(7m));
    }
}
