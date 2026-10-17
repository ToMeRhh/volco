using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.ViewModels;
using Volco.App.Views.Pages;
using Volco.Core.Config;
using Volco.Core.Input;
using Volco.Core.Media;
using Volco.Core.Runtime;
using Volco.Core.Tests.Audio;
using Volco.Core.Tests.Fakes;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>The media, Open a program and Run a script editors on a button card (custom-actions spec §2.3).</summary>
public class ButtonsPageCustomActionTests
{
    private sealed class Fixture : IDisposable
    {
        public FakeMediaSessions Media { get; } = new();
        public FakeProgramLauncher Launcher { get; } = new();
        public FakeProcessStarter Starter { get; } = new();
        public TestRig Rig { get; }
        public SettingsViewModel Vm { get; }
        public ButtonsPage Page { get; }

        public Fixture(Action<AppConfig>? configure = null)
        {
            Rig = new TestRig(new FakeAudioEngine(), custom: new CustomActionServices(Media, Launcher, Starter));
            configure?.Invoke(Rig.Config.Config);
            Vm = Rig.CreateSettings(new AvaloniaUiDispatcher());
            Page = new ButtonsPage(Vm.Buttons);
            new Window { Content = Page, Width = 900, Height = 1200 }.Show();
            Vm.Buttons.Items[0].IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
        }

        public ActionSlotViewModel Press => Vm.Buttons.Items[0].Press;
        public ButtonActionConfig Saved => Rig.Config.Config.Buttons[0].Default().SinglePress;

        public T InPressRow<T>(string name) where T : Control
        {
            var card = Page.GetVisualDescendants().OfType<Border>().First(b => b.Name == "Card");
            var row = card.GetVisualDescendants().OfType<ActionRow>().First(r => r.Name == "PressRow");
            return row.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        }

        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    private static ActionChoice Choice(ActionSlotViewModel slot, ButtonActionType type)
        => slot.Choices.Single(c => c.Type == type && !c.IsHeader);

    [AvaloniaFact]
    public void WithServices_TheNewActionsCanBeChosen()
    {
        using var f = new Fixture();
        Assert.True(Choice(f.Press, ButtonActionType.RunScript).IsSelectable);
        Assert.True(Choice(f.Press, ButtonActionType.OpenProgram).IsSelectable);
        Assert.True(Choice(f.Press, ButtonActionType.MediaPlayPause).IsSelectable);
    }

    [AvaloniaFact]
    public void RunScript_ShowsTheCommandFields_AndSavesThem()
    {
        using var f = new Fixture();
        f.Press.SelectedChoice = Choice(f.Press, ButtonActionType.RunScript);
        Dispatcher.UIThread.RunJobs();

        Assert.True(f.InPressRow<Border>("CommandParams").IsVisible);
        Assert.True(f.InPressRow<CheckBox>("StartAnotherCheck").IsVisible);
        Assert.False(f.InPressRow<Border>("MediaParams").IsVisible);

        f.Press.CommandPath = @"C:\Scripts\lights.bat";
        f.Press.CommandArguments = "on";
        f.Press.CommandName = "Lights";
        f.Press.StartAnother = true;

        Assert.Equal(ButtonActionType.RunScript, f.Saved.Action);
        Assert.Equal(@"C:\Scripts\lights.bat", f.Saved.Command!.Path);
        Assert.Equal("on", f.Saved.Command.Arguments);
        Assert.Equal("Lights", f.Saved.Command.Name);
        Assert.Equal(WhileRunningPolicy.StartAnother, f.Saved.WhileRunning);
        Assert.Equal("Run Lights", f.Vm.Buttons.Items[0].Summary);
    }

    [AvaloniaFact]
    public void OpenProgram_HidesTheRepeatChoice_AndMarksAMissingFile()
    {
        using var f = new Fixture();
        f.Press.SelectedChoice = Choice(f.Press, ButtonActionType.OpenProgram);
        f.Press.CommandPath = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid()}.exe");
        Dispatcher.UIThread.RunJobs();

        Assert.True(f.InPressRow<Border>("CommandParams").IsVisible);
        Assert.False(f.InPressRow<CheckBox>("StartAnotherCheck").IsVisible);
        Assert.True(f.Press.PathMissing);
        Assert.True(f.InPressRow<TextBlock>("PathMissingText").IsVisible);

        f.Press.CommandPath = "steam://rungameid/570";
        Assert.False(f.Press.PathMissing);
    }

    [AvaloniaFact]
    public void UsbOnlyNote_ShowsForNetworkTransports()
    {
        using var f = new Fixture(c =>
        {
            c.Connection.Type = ConnectionType.Udp;
            c.Buttons[0].Default().SinglePress = new ButtonActionConfig { Action = ButtonActionType.RunScript };
        });
        Assert.True(f.Press.ShowUsbOnly);
        Assert.True(f.InPressRow<TextBlock>("UsbOnlyText").IsVisible);

        f.Rig.Config.Config.Connection.Type = ConnectionType.Serial;
        f.Vm.Buttons.RefreshTransport();
        Assert.False(f.Press.ShowUsbOnly);
    }

    [AvaloniaFact]
    public void MediaPicker_ListsCurrent_LiveApps_AndTheSavedMissingOne()
    {
        using var f = new Fixture(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
        {
            Action = ButtonActionType.MediaPlayPause, MediaApp = new MediaAppConfig { Id = "Spotify.exe", Name = "Spotify" },
        });
        f.Media.SetApps(new[] { new MediaApp("chrome", "Google Chrome", MediaPlayback.Playing) });
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "Current app (whatever plays)", "Google Chrome", "Spotify (not running)" },
                     f.Press.MediaAppChoices.Select(c => c.Label));
        Assert.Equal("Spotify.exe", f.Press.SelectedMediaApp!.Id);
        Assert.True(f.InPressRow<Border>("MediaParams").IsVisible);

        f.Press.SelectedMediaApp = f.Press.MediaAppChoices[1];
        Assert.Equal("chrome", f.Saved.MediaApp!.Id);
        Assert.Equal("Google Chrome", f.Saved.MediaApp.Name);
        Assert.Equal("Play/pause Google Chrome", f.Vm.Buttons.Items[0].Summary);

        f.Press.SelectedMediaApp = f.Press.MediaAppChoices[0];
        Assert.Null(f.Saved.MediaApp);
    }

    [AvaloniaFact]
    public void Test_RunsTheScriptWithTheButtonAndGesture()
    {
        var script = $@"C:\volco-test-{Guid.NewGuid()}.bat";   // a Windows full path; on Linux a file by that name in the working directory
        File.WriteAllText(script, "");
        try
        {
            using var f = new Fixture(c => c.Buttons[0].Default().LongPress = new ButtonActionConfig
            {
                Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = script },
            });
            f.Vm.Buttons.Items[0].Hold.TestCommand.Execute(null);

            var (spec, _) = Assert.Single(f.Starter.Runs);
            Assert.Equal("0", spec.Environment["VOLCO_BUTTON"]);
            Assert.Equal("long", spec.Environment["VOLCO_GESTURE"]);
            Assert.Equal("Default", spec.Environment["VOLCO_PRESET"]);
        }
        finally { File.Delete(script); }
    }

    [AvaloniaFact]
    public void ScriptPreview_FollowsTheRun()
    {
        var script = $@"C:\volco-test-{Guid.NewGuid()}.bat";   // a Windows full path; on Linux a file by that name in the working directory
        File.WriteAllText(script, "");
        try
        {
            using var f = new Fixture(c => c.Buttons[0].Default().SinglePress = new ButtonActionConfig
            {
                Action = ButtonActionType.RunScript, Command = new CommandConfig { Path = script, Name = "Lights" },
            });
            f.Press.TestCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("RUNNING", f.Vm.Buttons.Items[0].Screen.TopText);

            f.Starter.Runs[0].Run.End(0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", f.Vm.Buttons.Items[0].Screen.TopText);
        }
        finally { File.Delete(script); }
    }
}
