using Volco.Core.Config;
using Volco.Core.Controller;
using Xunit;

namespace Volco.Core.Tests.Controller;

public class ControlLayoutTests
{
    private static AppConfig Config(int sliders, int buttons = 0)
    {
        var c = new AppConfig();
        c.Sliders = Enumerable.Range(0, sliders)
            .Select(i => new SliderConfig { Index = i, Name = ((char)('a' + i)).ToString() }).ToList();
        c.Buttons = Enumerable.Range(0, buttons).Select(i => new ButtonConfig { Index = i }).ToList();
        return c;
    }

    private static string Names(AppConfig c) => string.Join(",", c.Sliders.Select(s => s.Name));

    [Fact]
    public void IdentityAppliesAsNoChange()
    {
        var c = Config(5);
        var layout = ControlLayout.Identity(5, 0);
        layout.ApplyTo(c);
        Assert.Equal("a,b,c,d,e", Names(c));
        Assert.True(layout.IsIdentity);
    }

    [Fact]
    public void RemovingAMiddleSliderShiftsTheRestDown()
    {
        var c = Config(5);
        var layout = ControlLayout.Identity(5, 0);
        layout.RemoveSlider(2);
        layout.ApplyTo(c);
        Assert.Equal("a,b,d,e", Names(c));
        Assert.Equal(new[] { 0, 1, 2, 3 }, c.Sliders.Select(s => s.Index));
        Assert.False(layout.IsIdentity);
    }

    [Fact]
    public void RemovingAMiddleButtonMovesItsActionsInEveryMode()
    {
        var c = Config(0, 3);
        foreach (var b in c.Buttons)
        {
            b.Actions["default"] = new ButtonBindingConfig();
            b.Actions["mode-2"] = new ButtonBindingConfig();
        }
        var old2 = c.Buttons[2];
        var layout = ControlLayout.Identity(0, 3);
        layout.RemoveButton(1);
        layout.ApplyTo(c);
        Assert.Equal(2, c.Buttons.Count);
        Assert.Same(old2, c.Buttons[1]);
        Assert.Equal(new[] { "default", "mode-2" }, c.Buttons[1].Actions.Keys.OrderBy(k => k));
        Assert.Equal(1, c.Buttons[1].Index);
    }

    [Fact]
    public void AddedControlsGetEmptyEntries()
    {
        var c = Config(3);
        var layout = ControlLayout.Identity(3, 0);
        layout.AddSlider();
        layout.ApplyTo(c);
        Assert.Equal(4, c.Sliders.Count);
        Assert.Equal(SliderMappingType.None, c.Sliders[3].Type);
        Assert.Equal(3, c.Sliders[3].Index);
    }

    [Fact]
    public void RemoveThenAddIsNotIdentity()
    {
        var c = Config(5);
        var layout = ControlLayout.Identity(5, 0);
        layout.RemoveSlider(4);
        layout.AddSlider();
        Assert.Equal(new int?[] { 0, 1, 2, 3, null }, layout.Sliders);
        Assert.False(layout.IsIdentity);
        layout.ApplyTo(c);
        Assert.Equal("a,b,c,d,", Names(c));
        Assert.Equal(SliderMappingType.None, c.Sliders[4].Type);
    }

    [Fact]
    public void SavedEntriesBeyondTheDraftAreDropped()
    {
        var c = Config(6);
        ControlLayout.Identity(5, 0).ApplyTo(c);
        Assert.Equal(5, c.Sliders.Count);
    }

    [Fact]
    public void ACloneIsIndependent()
    {
        var layout = ControlLayout.Identity(5, 2);
        layout.RemoveSlider(2);
        var copy = layout.Clone();
        layout.RemoveSlider(0);
        layout.AddButton();
        Assert.Equal(new int?[] { 0, 1, 3, 4 }, copy.Sliders);
        Assert.Equal(new int?[] { 0, 1 }, copy.Buttons);
        Assert.False(copy.IsIdentity);
        Assert.True(ControlLayout.Identity(3, 1).Clone().IsIdentity);
    }
}
