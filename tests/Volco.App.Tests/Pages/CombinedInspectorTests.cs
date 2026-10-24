using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.ViewModels.Hardware;
using Volco.App.Views.Hardware;
using Volco.App.Views.Inspector;
using Volco.App.Views.Stage;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Tests.Runtime;
using Xunit;
using Button = Avalonia.Controls.Button;

namespace Volco.App.Tests.Pages;

/// <summary>Controller-page spec §4.3: one inspector per tile, two fixed sections always shown
/// (Controls or Actions in the top two thirds, Hardware in the bottom third, never under about four
/// rows), each with its own scrolling; a problem marks the Hardware header, a control only the
/// draft has asks to save first, a control the controller lacks says so, and Controller settings
/// takes the side panel.</summary>
public sealed class CombinedInspectorTests : IDisposable
{
    public CombinedInspectorTests() => StageTransitions.Enabled = false;

    public void Dispose() => StageTransitions.Enabled = false;

    private static readonly ControllerPreset Diy = ControllerPresets.All.Single(p => p.Id == ControllerPresets.DiyReferenceId);
    private static readonly ControllerConfig DiyConfig = ControllerPresets.ApplyTo(new ControllerConfig(), Diy);
    private static readonly byte[] DiyBlob = ControllerConfigCodec.Encode(DiyConfig);
    private static readonly IndicatorKind[] DiyKinds = { IndicatorKind.Led, IndicatorKind.Led, IndicatorKind.LedPair };

    private sealed record Shown(StagePage Page, HardwareInspectorHost Host, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public HardwareViewModel Hardware => Vm.Hardware;

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    /// <summary>The Controller page's stage over the DIY setup (5 sliders; buttons LED, LED, pair),
    /// in sync with the controller so the draft can be edited. Volco's settings have
    /// <paramref name="sliders"/> sliders: more than 5 draws the rest as not on this controller.</summary>
    private static Shown Show(int sliders = 5, double width = 1100, double height = 900)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, sliders).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var host = new HardwareInspectorHost { DataContext = vm.Hardware };
        var page = new StagePage(vm.Controls) { Hardware = vm.Hardware, HardwareSidePanel = host };
        var window = new Window { Content = page, Width = width, Height = height };
        window.Show();

        rig.Connection!.SetStatus(TransportStatus.Open);
        rig.Connection.RaiseMessage(Hello(ControllerConfigCodec.Crc32(DiyBlob), 5));
        Until(window, () => vm.Controller.CanEdit);
        Settle(window);
        return new Shown(page, host, window, rig, vm);
    }

    private static HelloMessage Hello(uint crc, int sliders)
        => new(ProtocolVersion.Current, ChipFamily.Esp32, "dev", ControllerConfigState.Stored, crc, sliders, DiyKinds);

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Until(Window window, Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            Settle(window);
            if (condition()) return;
            if (sw.ElapsedMilliseconds > ms) throw new TimeoutException("condition not met");
            Thread.Sleep(5);
        }
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Click(Shown s, Control control)
    {
        control.BringIntoView();
        Settle(s.Window);
        var point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), s.Window)!.Value;
        s.Window.MouseDown(point, MouseButton.Left);
        s.Window.MouseUp(point, MouseButton.Left);
        Settle(s.Window);
    }

    private static void Select(Shown s, object item)
    {
        s.Vm.Controls.Select((CommunityToolkit.Mvvm.ComponentModel.ObservableObject)item);
        Settle(s.Window);
    }

    private static SliderInspector SliderInspector(Shown s) => s.Page.GetVisualDescendants().OfType<SliderInspector>().Single(i => !i.IsProbe);

    private static ButtonInspector ButtonInspector(Shown s) => s.Page.GetVisualDescendants().OfType<ButtonInspector>().Single(i => !i.IsProbe);

    private static HardwareSection Section(Control inspector) => In<HardwareSection>(inspector, "HardwareSection");

    private static string PinValue(Visual field) => In<TextBlock>(field, "ValueText").Text!;

    [AvaloniaFact]
    public void BothSectionsAlwaysShow()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[0]);

        var inspector = SliderInspector(s);
        Assert.Equal("Slider 1", In<TextBlock>(inspector, "HeadingText").Text);
        Assert.True(In<Border>(inspector, "ControlsHeader").IsEffectivelyVisible);
        Assert.True(In<Panel>(inspector, "ControlsBody").IsEffectivelyVisible);
        Assert.True(In<Button>(inspector, "ControlsRow").IsEffectivelyVisible);
        var section = Section(inspector);
        Assert.True(In<Border>(section, "HardwareHeader").IsEffectivelyVisible);
        Assert.True(In<Panel>(section, "HardwareBody").IsEffectivelyVisible);
        var field = In<PinField>(section, "PinField");
        Assert.True(field.IsEffectivelyVisible);
        Assert.Equal($"GPIO {DiyConfig.Sliders[0].Pin}", PinValue(field));
        Assert.Equal("Remove Slider 1", In<Button>(section, "HardwareRemoveButton").Content);
        // No toggles: neither section collapses (each section is always open).
        Assert.DoesNotContain(inspector.GetVisualDescendants().OfType<ToggleButton>(), t => t.Name is "ControlsHeader" or "HardwareHeader");
        Assert.DoesNotContain(inspector.GetVisualDescendants(), v => v is Control { Name: "HardwareSummaryText" });
        // Hardware sits under Controls.
        var controls = In<StackPanel>(inspector, "ControlsSection");
        double Top(Visual v) => v.TranslatePoint(new Point(0, 0), inspector)!.Value.Y;
        Assert.True(Top(section) >= Top(controls) + controls.Bounds.Height - 1);

        Select(s, s.Vm.Buttons.Items[2]);
        var button = ButtonInspector(s);
        Assert.True(In<Border>(button, "ActionsHeader").IsEffectivelyVisible);
        Assert.True(In<Panel>(button, "ActionsBody").IsEffectivelyVisible);
        Assert.True(In<ContentControl>(button, "ActionRow_Press").IsEffectivelyVisible);
        section = Section(button);
        Assert.True(In<Panel>(section, "HardwareBody").IsEffectivelyVisible);
        Assert.Equal("Remove Button 3", In<Button>(section, "HardwareRemoveButton").Content);
        Assert.DoesNotContain(button.GetVisualDescendants().OfType<ToggleButton>(), t => t.Name is "ActionsHeader" or "HardwareHeader");
    }

    /// <summary>Where the Hardware section starts, from the top of the side panel's card.</summary>
    private static double HardwareTop(Shown s, Control inspector)
        => Section(inspector).TranslatePoint(new Point(0, 0), In<Border>(s.Page, "Inspector"))!.Value.Y;

    /// <summary>The bottom of the header and the Controls or Actions section, from the top of the card.</summary>
    private static double ContentBottom(Shown s, Control inspector)
    {
        var top = In<StackPanel>(inspector, "TopContent");
        return top.TranslatePoint(new Point(0, top.Bounds.Height), In<Border>(s.Page, "Inspector"))!.Value.Y;
    }

    [AvaloniaFact]
    public void HardwareStartsAtOneHeightForEveryTile()
    {
        using var s = Show(width: 1100, height: 900);
        var items = new object[] { s.Vm.Sliders.Items[0], s.Vm.Buttons.Items[0], s.Vm.Buttons.Items[2], s.Vm.Sliders.Items[4] };
        var tops = new List<double>();
        var gaps = new List<double>();
        foreach (var item in items)
        {
            Select(s, item);
            Control inspector = item is SliderItemViewModel ? SliderInspector(s) : ButtonInspector(s);
            var top = HardwareTop(s, inspector);
            tops.Add(top);
            gaps.Add(top - ContentBottom(s, inspector));
        }

        // The same height for sliders and buttons…
        Assert.All(tops, t => Assert.InRange(t, tops[0] - 0.5, tops[0] + 0.5));
        // …under every tile's content, and no lower than the tallest one needs.
        Assert.All(gaps, g => Assert.True(g >= -0.5, $"gap {g}"));
        Assert.InRange(gaps.Min(), -0.5, 0.5);
        Assert.True(gaps.Max() > 1, "a slider's Controls section is shorter than a button's Actions");
    }

    /// <summary>The page keeps no copy of an inspector once the tiles are measured: one per tile
    /// made showing the page again several times slower (every control is styled anew on return).
    /// A change that can move a tile's height measures it again.</summary>
    [AvaloniaFact]
    public void ThePageKeepsNoProbe()
    {
        using var s = Show(width: 1100, height: 900);
        Select(s, s.Vm.Buttons.Items[0]);
        Assert.DoesNotContain(s.Page.GetVisualDescendants().OfType<TileInspector>(), i => i.IsProbe);
        Assert.Single(s.Page.GetVisualDescendants().OfType<TileInspector>());   // the real one
        var height = s.Page.InspectorTopHeight;
        Assert.True(height > 0);

        // A much longer action name wraps onto more lines: the height follows, and the probe goes again
        var slot = s.Vm.Buttons.Items[1].Press;
        slot.SelectedChoice = ActionCatalog.For(ButtonActionType.RunScript);
        slot.CommandPath = @"C:\Users\someone\Documents\Scripts\a very long script name that wraps over several lines in the inspector.ps1";
        Settle(s.Window);
        Assert.True(s.Page.InspectorTopHeight >= height - 0.5);
        Assert.DoesNotContain(s.Page.GetVisualDescendants().OfType<TileInspector>(), i => i.IsProbe);
    }

    [AvaloniaFact]
    public void TheInspectorScrollsAsOne()
    {
        using var s = Show(width: 820, height: 520);
        var item = s.Vm.Buttons.Items[0];
        Select(s, item);
        var inspector = ButtonInspector(s);

        // No section scrolls on its own: the card does.
        Assert.DoesNotContain(inspector.GetVisualDescendants().OfType<ScrollViewer>(),
                              v => v.Name is "ActionsScroll" or "ControlsScroll" or "HardwareScroll");
        Assert.Same(In<ScrollViewer>(s.Page, "InspectorScroll"), inspector.FindAncestorOfType<ScrollViewer>());
        var scroll = In<ScrollViewer>(s.Page, "InspectorScroll");
        Assert.True(scroll.Extent.Height > scroll.Viewport.Height + 1, $"extent {scroll.Extent.Height}, viewport {scroll.Viewport.Height}");
        Assert.True(In<Border>(inspector, "ActionsHeader").IsEffectivelyVisible);
        Assert.True(In<Border>(Section(inspector), "HardwareHeader").IsEffectivelyVisible);
    }

    /// <summary>The side panel is a card with all four edges, not a column cut off by the window.</summary>
    [AvaloniaFact]
    public void TheSidePanelIsACard()
    {
        using var s = Show();
        var card = In<Border>(s.Page, "Inspector");
        Assert.Equal(new Thickness(1), card.BorderThickness);
        Assert.True(card.CornerRadius.TopLeft > 0);
        Assert.True(card.Margin.Top > 0 && card.Margin.Right > 0 && card.Margin.Bottom > 0);
    }

    [AvaloniaFact]
    public void AProblemMarksTheHardwareHeader()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[1]);
        Assert.False(In<PathIcon>(Section(SliderInspector(s)), "HardwareProblemMark").IsVisible);

        s.Vm.Controller.SetSliderPin(1, DiyConfig.Sliders[0].Pin);   // Slider 1's pin
        Settle(s.Window);

        var section = Section(SliderInspector(s));
        var mark = In<PathIcon>(section, "HardwareProblemMark");
        Assert.True(mark.IsEffectivelyVisible);
        Assert.True(mark.TranslatePoint(new Point(0, 0), section)!.Value.Y
                    < In<Border>(section, "HardwareHeader").Bounds.Height);   // on the header
        Assert.True(In<Panel>(section, "HardwareBody").IsEffectivelyVisible);

        Select(s, s.Vm.Buttons.Items[0]);   // a button without a problem: no mark
        Assert.False(In<PathIcon>(Section(ButtonInspector(s)), "HardwareProblemMark").IsVisible);
    }

    [AvaloniaFact]
    public void NewSliderAsksToSaveFirst()
    {
        using var s = Show();
        s.Vm.Controller.AddSliderCommand.Execute(null);   // the draft only, without the pin dialog
        Settle(s.Window);
        Select(s, s.Hardware.Sliders[5]);

        var inspector = SliderInspector(s);
        Assert.Same(s.Hardware.Sliders[5], inspector.DataContext);
        var text = In<TextBlock>(inspector, "SaveFirstText");
        Assert.Equal("Save to set it up", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        var save = In<Button>(inspector, "SaveFirstButton");
        Assert.True(save.IsEffectivelyVisible);
        Assert.Same(s.Hardware.SaveCommand, save.Command);
        Assert.False(In<Button>(inspector, "ControlsRow").IsEffectivelyVisible);
        Assert.True(In<Border>(Section(inspector), "HardwareHeader").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AfterTheConfirmedSaveTheControlsSectionAppears()
    {
        using var s = Show();
        s.Vm.Controller.AddSliderCommand.Execute(null);   // the draft only, without the pin dialog
        s.Vm.Controller.SetSliderPin(5, 39);
        Settle(s.Window);
        Select(s, s.Hardware.Sliders[5]);
        var save = In<Button>(SliderInspector(s), "SaveFirstButton");
        Assert.True(save.IsEffectivelyEnabled);

        var blob = ControllerConfigCodec.Encode(s.Vm.Controller.Draft);
        Click(s, save);
        var conn = s.Rig.Connection!;
        Until(s.Window, () => conn.SentFrames.Any(f => f.Length > 3 && f[2] == 0x88));
        var burst = conn.SentFrames.Last(f => f.Length > 3 && f[2] == 0x88);
        conn.RaiseMessage(new ConfigResultMessage(burst[3], ConfigResultCode.Ok, 0xFF, 0xFF));
        conn.RaiseMessage(Hello(ControllerConfigCodec.Crc32(blob), 6));
        Until(s.Window, () => !s.Vm.Controller.IsSaving && s.Vm.Sliders.Items.Count == 6);
        Settle(s.Window);

        Assert.Same(s.Vm.Sliders.Items[5], s.Vm.Controls.Selected);
        var inspector = SliderInspector(s);
        Assert.Same(s.Vm.Sliders.Items[5], inspector.DataContext);
        Assert.Equal("Slider 6", In<TextBlock>(inspector, "HeadingText").Text);
        Assert.True(In<Button>(inspector, "ControlsRow").IsEffectivelyVisible);
        Assert.False(In<TextBlock>(inspector, "SaveFirstText").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void MissingSliderSaysNotOnThisController()
    {
        using var s = Show(sliders: 6);
        var missing = s.Vm.Sliders.Items[5];
        Assert.True(missing.IsMissing);
        Select(s, missing);

        var inspector = SliderInspector(s);
        // No panel over the Controls section: the Hardware section says so, with Controls' Remove
        // (the last one only).
        Assert.DoesNotContain(inspector.GetVisualDescendants(), v => v is Control { Name: "MissingPanel" });
        var section = Section(inspector);
        var text = In<TextBlock>(section, "NotOnControllerText");
        Assert.Equal("Not on this controller", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        var remove = In<Button>(section, "RemoveButton");
        Assert.True(remove.IsEffectivelyVisible);
        Assert.Single(inspector.GetVisualDescendants().OfType<Button>(), b => b.Name == "RemoveButton");
        Assert.Same(missing.RemoveLastCommand, remove.Command);
        Assert.DoesNotContain(section.GetVisualDescendants().OfType<PinField>(), f => f.IsEffectivelyVisible);
        Assert.DoesNotContain(section.GetVisualDescendants().OfType<Button>(), b => b.Name == "HardwareRemoveButton" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void ChangedLightSaysItFollowsAfterSaving()
    {
        using var s = Show();
        Select(s, s.Vm.Buttons.Items[0]);
        var inspector = ButtonInspector(s);
        Assert.NotEqual("Follows the new light after saving", In<TextBlock>(inspector, "LightText").Text);

        s.Hardware.InspectedButton!.IsScreen = true;   // the draft gives it a screen
        Settle(s.Window);

        inspector = ButtonInspector(s);
        Assert.True(In<Border>(inspector, "LightRow").IsEffectivelyVisible);
        Assert.Equal("Follows the new light after saving", In<TextBlock>(inspector, "LightText").Text);

        s.Hardware.InspectedButton!.IsLed = true;   // back to what the controller has
        Settle(s.Window);
        Assert.NotEqual("Follows the new light after saving", In<TextBlock>(ButtonInspector(s), "LightText").Text);
    }

    /// <summary>Hardware-page spec §6.5: after an unsaved removal the header and the tile's tooltip
    /// number the control by its place in the draft, as its Remove does.</summary>
    [AvaloniaFact]
    public void TheHeaderFollowsTheDraftsNumbering()
    {
        using var s = Show();
        s.Vm.Controller.RemoveSlider(1);
        Settle(s.Window);
        var moved = s.Vm.Sliders.Items[2];   // was Slider 3, now the draft's Slider 2
        Select(s, moved);

        var inspector = SliderInspector(s);
        Assert.Same(moved, inspector.DataContext);
        Assert.Equal("Slider 2", In<TextBlock>(inspector, "HeadingText").Text);
        Assert.Equal("Remove Slider 2", In<Button>(Section(inspector), "HardwareRemoveButton").Content);
        var tile = s.Page.Tiles().OfType<SliderTile>().Single(t => ReferenceEquals(t.DataContext, moved));
        Assert.Equal("Slider 2", ToolTip.GetTip(tile));

        s.Vm.Controller.RevertCommand.Execute(null);   // Discard: Volco's numbering again
        Settle(s.Window);
        Assert.Equal("Slider 3", In<TextBlock>(SliderInspector(s), "HeadingText").Text);
    }

    [AvaloniaFact]
    public void ARemovedLightSaysNoLightAfterSaving()
    {
        using var s = Show();
        Select(s, s.Vm.Buttons.Items[0]);
        s.Hardware.InspectedButton!.IsNone = true;   // the draft takes the LED away
        Settle(s.Window);

        var inspector = ButtonInspector(s);
        Assert.True(In<Border>(inspector, "LightRow").IsEffectivelyVisible);
        Assert.Equal("No light after saving", In<TextBlock>(inspector, "LightText").Text);
    }

    [AvaloniaFact]
    public void ControllerSettingsChipShowsSettings()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[0]);
        Click(s, In<Button>(s.Page, "ControllerSettingsChip"));

        Assert.IsType<ControllerSettingsViewModel>(s.Hardware.Inspected);
        var settings = s.Host.GetVisualDescendants().OfType<ControllerSettingsInspector>().Single();
        Assert.True(settings.IsEffectivelyVisible);
        Assert.False(In<ContentControl>(s.Page, "InspectorContent").IsVisible);
        Assert.DoesNotContain(s.Page.GetVisualDescendants().OfType<SliderInspector>(), i => !i.IsProbe && i.IsEffectivelyVisible);

        Select(s, s.Vm.Sliders.Items[0]);   // a tile brings its inspector back
        Assert.True(SliderInspector(s).IsEffectivelyVisible);
        Assert.Empty(s.Host.GetVisualDescendants().OfType<ControllerSettingsInspector>());
    }

    /// <summary>A new control's config is thrown away until the controller has it: a new slider
    /// can't be named, nor a new button cleared.</summary>
    [AvaloniaFact]
    public void ANewControlCantBeNamedOrCleared()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[0]);
        Assert.True(In<TextBox>(SliderInspector(s), "NameBox").IsEffectivelyEnabled);

        s.Vm.Controller.AddSliderCommand.Execute(null);   // the draft only, without the pin dialog
        Settle(s.Window);
        Select(s, s.Hardware.Sliders[5]);
        Assert.False(In<TextBox>(SliderInspector(s), "NameBox").IsEffectivelyEnabled);

        s.Vm.Controller.AddButtonCommand.Execute(null);   // the draft only, without the pin dialog
        Settle(s.Window);
        var added = s.Hardware.Buttons[3];
        Assert.True(added.IsNew);
        Select(s, added);
        Assert.False(added.CanClear);
        Assert.False(In<Button>(ButtonInspector(s), "ClearButton").IsEffectivelyEnabled);
    }

    /// <summary>"Swap with" names the others as the page does (the draft's numbering), leaves out
    /// those the drawing no longer shows, and a new control has nothing to swap with.</summary>
    [AvaloniaFact]
    public void SwapWithFollowsTheDraft()
    {
        using var s = Show();
        s.Vm.Controller.RemoveSlider(1);   // unsaved: Slider 3 is now the draft's Slider 2
        s.Vm.Controller.AddSliderCommand.Execute(null);   // the draft only, without the pin dialog
        Settle(s.Window);
        Until(s.Window, () => s.Page.Tiles().OfType<SliderTile>().Count() == 5);

        var first = s.Page.Tiles().OfType<SliderTile>().Single(t => ReferenceEquals(t.DataContext, s.Vm.Sliders.Items[0]));
        var swapWith = OpenSwapWith(s, first);
        Assert.True(swapWith.IsEnabled);
        Assert.Equal(new[] { "Slider 2", "Slider 3", "Slider 4" },
                     swapWith.ItemsSource!.Cast<MenuItem>().Select(e => (string)e.Header!));
        first.ContextMenu!.Close();
        Settle(s.Window);

        var added = s.Page.Tiles().OfType<SliderTile>().Single(t => t.DataContext is SliderItemViewModel { IsNew: true });
        swapWith = OpenSwapWith(s, added);
        Assert.False(swapWith.IsEnabled);
        Assert.Empty(swapWith.ItemsSource!.Cast<MenuItem>());
        added.ContextMenu!.Close();
    }

    private static MenuItem OpenSwapWith(Shown s, Control tile)
    {
        tile.RaiseEvent(new ContextRequestedEventArgs());
        Settle(s.Window);
        return tile.ContextMenu!.Items.OfType<MenuItem>().First();
    }

    /// <summary>The inline question shows at the bottom of the side panel, under the inspector
    /// whose Remove asked.</summary>
    [AvaloniaFact]
    public void TheRemoveQuestionIsAtTheBottomOfTheSidePanel()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[2]);
        var inspector = SliderInspector(s);
        Click(s, In<Button>(Section(inspector), "HardwareRemoveButton"));

        var panel = In<Border>(s.Page, "ConfirmPanel");
        Assert.True(panel.IsEffectivelyVisible);
        Assert.Equal("Remove Slider 3?", In<TextBlock>(panel, "ConfirmText").Text);
        Assert.DoesNotContain(s.Host.GetVisualDescendants(), v => v is Control { Name: "ConfirmPanel" });
        double Top(Visual v) => v.TranslatePoint(new Point(0, 0), s.Window)!.Value.Y;
        Assert.True(Top(panel) >= Top(inspector) + inspector.Bounds.Height - 1);
        var side = In<Border>(s.Page, "Inspector");
        Assert.True(Top(panel) + panel.Bounds.Height <= Top(side) + side.Bounds.Height + 1);
        Assert.True(Top(panel) + panel.Bounds.Height >= Top(side) + side.Bounds.Height - 40);
    }
}
