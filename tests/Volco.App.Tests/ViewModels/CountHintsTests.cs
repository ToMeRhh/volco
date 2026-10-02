using Volco.App.ViewModels;
using Xunit;

namespace Volco.App.Tests.ViewModels;

public class CountHintsTests
{
    [Theory]
    [InlineData(null, 3, "")]
    [InlineData(3, 3, "")]
    [InlineData(4, 3, "Your controller has 4 buttons; 3 are set up.")]
    [InlineData(2, 1, "Your controller has 2 buttons; 1 is set up.")]
    [InlineData(3, 5, "Your controller has 3 buttons; cards 3–4 do nothing.")]
    [InlineData(1, 2, "Your controller has 1 button; card 1 does nothing.")]
    public void Text(int? controller, int cards, string expected)
        => Assert.Equal(expected, CountHints.Text("button", controller, cards));
}
