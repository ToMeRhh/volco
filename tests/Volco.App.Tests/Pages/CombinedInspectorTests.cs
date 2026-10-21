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

/// <summary>Controller-page spec §4.3: one inspector per tile, its Controls (or Actions) section
/// open and its Hardware section collapsed with a summary; the sections' state is remembered per
/// kind, a problem opens Hardware, a control only the draft has asks to save first, a control the
/// controller lacks says so, and Controller settings takes the side panel.</summary>
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
    private static Shown Show(int sliders = 5)
    {
        var rig = new TestRig();
        var c = rig.Config.Config;
        c.Sliders = Enumerable.Range(0, sliders).Select(i => new SliderConfig { Index = i, Type = SliderMappingType.None }).ToList();
        c.Buttons = Enumerable.Range(0, 3).Select(i => new ButtonConfig { Index = i }).ToList();
        c.ControllerConfigBlob = Convert.ToBase64String(DiyBlob);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher());
        var host = new HardwareInspectorHost { DataContext = vm.Hardware };
        var page = new StagePage(vm.Controls) { Hardware = vm.Hardware, HardwareSidePanel = host };
        var window = new Window { Content = page, Width = 1100, Height = 900 };
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

    private static SliderInspector SliderInspector(Shown s) => s.Page.GetVisualDescendants().OfType<SliderInspector>().Single();

    private static ButtonInspector ButtonInspector(Shown s) => s.Page.GetVisualDescendants().OfType<ButtonInspector>().Single();

    private static HardwareSection Section(Control inspector) => In<HardwareSection>(inspector, "HardwareSection");

    private static string PinValue(Visual field) => In<TextBlock>(field, "ValueText").Text!;

    [AvaloniaFact]
    public void SliderShowsControlsOpenAndHardwareCollapsed()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[0]);

        var inspector = SliderInspector(s);
        Assert.Equal("Slider 1", In<TextBlock>(inspector, "HeadingText").Text);
        Assert.True(In<ToggleButton>(inspector, "ControlsHeader").IsChecked);
        Assert.True(In<Button>(inspector, "ControlsRow").IsEffectivelyVisible);
        Assert.True(In<Border>(inspector, "ResponseSection").IsEffectivelyVisible);

        var section = Section(inspector);
        var header = In<ToggleButton>(section, "HardwareHeader");
        Assert.True(header.IsEffectivelyVisible);
        Assert.False(header.IsChecked);
        Assert.Equal($"GPIO {DiyConfig.Sliders[0].Pin}", In<TextBlock>(section, "HardwareSummaryText").Text);
        Assert.False(In<PathIcon>(section, "HardwareProblemMark").IsVisible);
        Assert.False(In<Panel>(section, "HardwareBody").IsVisible);
        // The section sits under the Controls section.
        Assert.True(header.TranslatePoint(new Point(0, 0), inspector)!.Value.Y
                    > In<Border>(inspector, "ResponseSection").TranslatePoint(new Point(0, 0), inspector)!.Value.Y);
    }

    [AvaloniaFact]
    public void AButtonsSummaryNamesItsPinAndLight()
    {
        using var s = Show();
        Select(s, s.Vm.Buttons.Items[0]);
        var b0 = DiyConfig.Buttons[0];
        Assert.Equal($"GPIO {b0.Pin} · LED {b0.Led.Pin}", In<TextBlock>(Section(ButtonInspector(s)), "HardwareSummaryText").Text);

        Select(s, s.Vm.Buttons.Items[2]);
        var b2 = DiyConfig.Buttons[2];
        Assert.Equal($"GPIO {b2.Pin} · LEDs {b2.LedPair.PinA}, {b2.LedPair.PinB}",
                     In<TextBlock>(Section(ButtonInspector(s)), "HardwareSummaryText").Text);
        Assert.True(In<ToggleButton>(ButtonInspector(s), "ActionsHeader").IsChecked);
        Assert.True(In<ContentControl>(ButtonInspector(s), "ActionRow_Press").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void OpeningHardwareIsRememberedForTheNextSlider()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[0]);
        Click(s, In<ToggleButton>(Section(SliderInspector(s)), "HardwareHeader"));
        Assert.True(s.Hardware.Sections.SliderHardwareOpen);
        var field = In<PinField>(Section(SliderInspector(s)), "PinField");
        Assert.True(field.IsEffectivelyVisible);
        Assert.Equal($"GPIO {DiyConfig.Sliders[0].Pin}", PinValue(field));
        Assert.Equal("Remove Slider 1", In<Button>(Section(SliderInspector(s)), "HardwareRemoveButton").Content);

        Select(s, s.Vm.Sliders.Items[1]);
        var section = Section(SliderInspector(s));
        Assert.True(In<Panel>(section, "HardwareBody").IsEffectivelyVisible);
        Assert.Equal($"GPIO {DiyConfig.Sliders[1].Pin}", PinValue(In<PinField>(section, "PinField")));

        // Per kind: a button's Hardware section is still collapsed.
        Select(s, s.Vm.Buttons.Items[0]);
        Assert.False(In<Panel>(Section(ButtonInspector(s)), "HardwareBody").IsVisible);
    }

    [AvaloniaFact]
    public void AProblemOpensTheHardwareSection()
    {
        using var s = Show();
        Select(s, s.Vm.Sliders.Items[1]);
        var section = Section(SliderInspector(s));
        Assert.False(In<Panel>(section, "HardwareBody").IsVisible);

        s.Vm.Controller.SetSliderPin(1, DiyConfig.Sliders[0].Pin);   // Slider 1's pin
        Settle(s.Window);

        section = Section(SliderInspector(s));
        Assert.True(s.Hardware.Sections.SliderHardwareOpen);
        Assert.True(In<Panel>(section, "HardwareBody").IsEffectivelyVisible);
        Assert.True(In<PathIcon>(section, "HardwareProblemMark").IsEffectivelyVisible);
        Assert.False(s.Hardware.Sections.ButtonHardwareOpen);
    }

    [AvaloniaFact]
    public void NewSliderAsksToSaveFirst()
    {
        using var s = Show();
        s.Hardware.AddSliderCommand.Execute(null);
        Settle(s.Window);
        Select(s, s.Hardware.Sliders[5]);

        var inspector = SliderInspector(s);
        Assert.Same(s.Hardware.Sliders[5], inspector.DataContext);
        var text = In<TextBlock>(inspector, "SaveFirstText");
        Assert.Equal("Save to controller to set it up", text.Text);
        Assert.True(text.IsEffectivelyVisible);
        var save = In<Button>(inspector, "SaveFirstButton");
        Assert.True(save.IsEffectivelyVisible);
        Assert.Same(s.Hardware.SaveCommand, save.Command);
        Assert.False(In<Button>(inspector, "ControlsRow").IsEffectivelyVisible);
        Assert.True(In<ToggleButton>(Section(inspector), "HardwareHeader").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AfterTheConfirmedSaveTheControlsSectionAppears()
    {
        using var s = Show();
        s.Hardware.AddSliderCommand.Execute(null);
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
        Assert.True(In<Border>(inspector, "MissingPanel").IsVisible);
        Assert.True(In<Button>(inspector, "RemoveButton").IsEffectivelyVisible);
        var section = Section(inspector);
        Click(s, In<ToggleButton>(section, "HardwareHeader"));
        var text = In<TextBlock>(section, "NotOnControllerText");
        Assert.Equal("Not on this controller", text.Text);
        Assert.True(text.IsEffectivelyVisible);
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
        Assert.EndsWith(" · Screen, bus 0, 3C", In<TextBlock>(Section(inspector), "HardwareSummaryText").Text);

        s.Hardware.InspectedButton!.IsLed = true;   // back to what the controller has
        Settle(s.Window);
        Assert.NotEqual("Follows the new light after saving", In<TextBlock>(ButtonInspector(s), "LightText").Text);
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
        Assert.Empty(s.Page.GetVisualDescendants().OfType<SliderInspector>().Where(i => i.IsEffectivelyVisible));
        Assert.True(In<Volco.App.Views.Hardware.DeviceCard>(s.Host, "Card").IsEffectivelyVisible);

        Select(s, s.Vm.Sliders.Items[0]);   // a tile brings its inspector back
        Assert.True(SliderInspector(s).IsEffectivelyVisible);
        Assert.Empty(s.Host.GetVisualDescendants().OfType<ControllerSettingsInspector>());
    }
}
