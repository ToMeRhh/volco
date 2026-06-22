using Xunit;

namespace Volco.Tests;

public class AppLogTests
{
    private int _counter = 0;

    private string GetValue()
    {
        _counter++;
        return "value";
    }

    [Fact]
    public void Debug_InterpolatedString_SkipsEvaluationWhenDisabled()
    {
        // Arrange
        AppLog.IsDebugEnabled = false;
        _counter = 0;

        // Act
        AppLog.Debug($"Counter: {GetValue()}");

        // Assert
        Assert.Equal(0, _counter);
    }

    [Fact]
    public void Debug_InterpolatedString_EvaluatesWhenEnabled()
    {
        // Arrange
        AppLog.IsDebugEnabled = true;
        _counter = 0;

        // Act
        AppLog.Debug($"Counter: {GetValue()}");

        // Assert
        Assert.Equal(1, _counter);
    }
}
