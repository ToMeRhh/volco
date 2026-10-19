using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.Controls;
using Volco.App.Platform;
using Volco.App.ViewModels;
using Volco.App.Views.Dialogs;
using Volco.App.Views.Pages;
using Volco.Core.Audio;
using Volco.Core.Config;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Pages;

/// <summary>Spec docs/design/controls-page.md §6.2, §6.3, §6.4 and §11: the slider target dialog.</summary>
public class TargetDialogTests
{
    private sealed record Shown(ControlsPage Page, Window Window, TestRig Rig, SettingsViewModel Vm) : IDisposable
    {
        public ControlsViewModel Controls => Vm.Controls;
        public AppConfig Config => Rig.Config.Config;
        public TargetDialogViewModel Dialog => Assert.IsType<TargetDialogViewModel>(Controls.Dialog);
        public TargetDialog View => Page.GetVisualDescendants().OfType<TargetDialog>().Single();

        public void Dispose()
        {
            Vm.Dispose();
            Rig.Dispose();
        }
    }

    private static AudioSessionInfo Session(string image, uint pid)
        => new($"s-{pid}", pid, image, "dev-1", false);

    /// <summary>Speakers (the default) and nicknamed headphones; Spotify (twice), chrome and Discord
    /// playing. Slider 1 is the main output, slider 2 "Chat" has Discord, slider 3 is everything
    /// else, slider 4 has game.exe, which isn't running.</summary>
    private static Shown Show(Action<AppConfig>? configure = null, IAppIcons? icons = null,
                              Action<FakeAudioEngine>? setup = null)
    {
        var speakers = new AudioDevice("dev-1", "Speakers (Realtek Audio)", AudioFlow.Render, true);
        var engine = new FakeAudioEngine
        {
            Devices = { speakers, new AudioDevice("dev-2", "Headphones (USB Audio)", AudioFlow.Render, false) },
            DefaultRender = speakers,
            Sessions = { Session("Spotify.exe", 1), Session("chrome.exe", 2), Session("Spotify.exe", 3), Session("Discord.exe", 4) },
        };
        setup?.Invoke(engine);
        var rig = new TestRig(engine);
        var c = rig.Config.Config;
        c.Devices.Add(new DeviceSettingsConfig { Id = "dev-2", FriendlyName = "Headphones (USB Audio)", Nickname = "Cans" });
        c.Sliders = new List<SliderConfig>
        {
            new() { Type = SliderMappingType.Device },
            new() { Name = "Chat", Type = SliderMappingType.Processes, Processes = { "Discord.exe" } },
            new() { Type = SliderMappingType.Unmapped },
            new() { Type = SliderMappingType.Processes, Processes = { "game.exe" } },
        };
        configure?.Invoke(c);
        var vm = rig.CreateSettings(new AvaloniaUiDispatcher(), icons);
        var page = new ControlsPage(vm.Controls);
        var window = new Window { Content = page, Width = 932, Height = 680 };
        window.Show();
        Settle(window);
        return new Shown(page, window, rig, vm);
    }

    /// <summary>Selects the slider and opens its dialog from the inspector's Controls row, as a click would.</summary>
    private static void Open(Shown s, int slider = 0)
    {
        s.Controls.Select(s.Vm.Sliders.Items[slider]);
        Settle(s.Window);
        var row = ControlsRow(s);
        row.Focus(NavigationMethod.Pointer);
        row.Command!.Execute(row.CommandParameter);
        Settle(s.Window);
    }

    private static Button ControlsRow(Shown s) => In<Button>(s.Page.FindControl<Border>("Inspector")!, "ControlsRow");

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T In<T>(Visual root, string name) where T : Control
        => root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static List<Button> Rows(Shown s)
        => s.View.GetVisualDescendants().OfType<Button>().Where(b => b.Name == "TargetRowButton" && b.IsEffectivelyVisible).ToList();

    private static Button Row(Shown s, string name) => Rows(s).First(b => ((TargetItem)b.DataContext!).Name == name);

    private static TargetItem Item(Shown s, string name) => s.Dialog.VisibleItems.First(i => i.Name == name);

    private static void Click(Shown s, string name)
    {
        Row(s, name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
    }

    private static void Done(Shown s)
    {
        In<Button>(s.View, "DoneButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle(s.Window);
    }

    private static TextBox SearchBox(Shown s) => In<TextBox>(s.View, "SearchBox");

    private static IEnumerable<string> On(Shown s) => s.Dialog.Sections.SelectMany(x => x.Items).Where(i => i.IsOn).Select(i => i.Name);

    // ── Opening ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Open_SearchFocused()
    {
        using var s = Show();
        Open(s);

        Assert.True(SearchBox(s).IsFocused);
        Assert.Equal("Search devices and apps", SearchBox(s).PlaceholderText);
        Assert.Equal("Slider 1 controls", In<TextBlock>(s.View, "TitleText").Text);
        Assert.Contains("Narrow", s.Page.FindControl<Border>("DialogCard")!.Classes);
        Assert.Equal(new[] { "Devices", "Apps", "Other" }, s.Dialog.Sections.Select(x => x.Name));

        // Devices: the main output (on, as saved) and every output, by display name over the Windows name.
        var devices = s.Dialog.Sections[0].Items;
        Assert.Equal(new[] { "Main output", "Speakers", "Cans" }, devices.Select(i => i.Name));
        Assert.Equal(new[] { "Now Speakers", "Speakers (Realtek Audio)", "Headphones (USB Audio)" }, devices.Select(i => i.Secondary));
        Assert.All(devices, i => Assert.True(i.IsRadio));
        Assert.Equal(new[] { "Main output" }, On(s));

        // Apps: the live sessions once each, then saved apps that aren't running, then Other app….
        var apps = s.Dialog.Sections[1].Items;
        Assert.Equal(new[] { "Spotify", "chrome", "Discord", "game", "Other app…" }, apps.Select(i => i.Name));
        Assert.Equal(new[] { "Spotify.exe", "chrome.exe", "Discord.exe", "Not running", "" }, apps.Select(i => i.Secondary));
        Assert.Equal(new[] { TargetKind.App, TargetKind.App, TargetKind.App, TargetKind.App, TargetKind.OtherApp }, apps.Select(i => i.Kind));
        Assert.Equal("Everything else", Assert.Single(s.Dialog.Sections[2].Items).Name);
        Assert.Equal(9, Rows(s).Count);
        Assert.Null(s.Dialog.Error);
        Assert.False(In<TextBlock>(s.View, "ErrorText").IsVisible);

        // A collapsed section is remembered for the next dialog.
        In<Button>(s.View, "SectionHeader").Command!.Execute("Apps");
        Settle(s.Window);
        Assert.True(s.Controls.CollapsedSections["target:Apps"]);
        Assert.DoesNotContain(Rows(s), b => ((TargetItem)b.DataContext!).Kind == TargetKind.App);

        // Escape closes it and gives the focus back to the Controls row.
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.True(ControlsRow(s).IsFocused);

        Open(s);
        Assert.True(s.Dialog.Sections[1].IsCollapsed);
    }

    // ── Choices ───────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void PickDevice_SingleChoice()
    {
        using var s = Show();
        Open(s);

        Click(s, "Cans");
        Assert.Equal(new[] { "Cans" }, On(s));
        Click(s, "Speakers");
        Assert.Equal(new[] { "Speakers" }, On(s));
        Click(s, "Main output");
        Assert.Equal(new[] { "Main output" }, On(s));

        // The Windows name is searched too.
        s.Window.KeyTextInput("usb");
        Settle(s.Window);
        Assert.Equal("Cans", s.Dialog.VisibleItems[0].Name);
        Assert.Empty(s.Dialog.VisibleItems[0].Highlight);
        PressEnter(s);
        Assert.Equal(new[] { "Cans" }, On(s));

        Done(s);
        Assert.False(s.Controls.HasDialog);
        var slider = s.Config.Sliders[0];
        Assert.Equal(SliderMappingType.Device, slider.Type);
        Assert.Equal("dev-2", slider.Device!.Id);
        Assert.Equal("Cans", s.Vm.Sliders.Items[0].Label);
    }

    private static void AddMic(FakeAudioEngine e)
        => e.Devices.Add(new AudioDevice("mic-1", "Microphone (Blue Yeti)", AudioFlow.Capture, true, DeviceFormFactor.Microphone));

    [AvaloniaFact]
    public void Devices_ListsInputsAfterOutputs_WithInputSecondary()
    {
        using var s = Show(setup: AddMic);
        Open(s);

        var devices = s.Dialog.Sections[0].Items;
        Assert.Equal(new[] { "Main output", "Speakers", "Cans", "Microphone" }, devices.Select(i => i.Name));
        var mic = devices[^1];
        Assert.Equal("device:mic-1", mic.Key);
        Assert.Equal("Input · Microphone (Blue Yeti)", mic.Secondary);
        Assert.Equal("Icon.Device.Mic", mic.IconKey);
        Assert.True(mic.IsRadio);
    }

    [AvaloniaFact]
    public void PickInput_SavesDevice_LabelIsItsName()
    {
        using var s = Show(setup: AddMic);
        Open(s);

        Click(s, "Microphone");
        Assert.Equal(new[] { "Microphone" }, On(s));
        Done(s);

        var slider = s.Config.Sliders[0];
        Assert.Equal(SliderMappingType.Device, slider.Type);
        Assert.Equal("mic-1", slider.Device!.Id);
        Assert.Equal("Microphone", s.Vm.Sliders.Items[0].Label);
    }

    private static void PressEnter(Shown s)
    {
        s.Window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        s.Window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Settle(s.Window);
    }

    [AvaloniaFact]
    public void Apps_MultiChoice()
    {
        using var s = Show();
        Open(s);

        Click(s, "Spotify");
        Click(s, "chrome");
        Assert.Equal(new[] { "Spotify", "chrome" }, On(s));
        Assert.All(s.Dialog.Sections[1].Items.Where(i => i.Kind == TargetKind.App), i => Assert.True(i.IsCheck));
        Click(s, "chrome");
        Assert.Equal(new[] { "Spotify" }, On(s));

        // Other app… picks an .exe; its file name is ticked.
        s.Dialog.PickExe = () => Task.FromResult<string?>(@"C:\Games\Quest\quest.exe");
        Click(s, "Other app…");
        Assert.Equal(new[] { "Spotify", "quest" }, On(s));
        Assert.Equal("Not running", Item(s, "quest").Secondary);

        Done(s);
        Assert.False(s.Controls.HasDialog);
        var slider = s.Config.Sliders[0];
        Assert.Equal(SliderMappingType.Processes, slider.Type);
        Assert.Equal(new[] { "Spotify.exe", "quest.exe" }, slider.Processes);
        Assert.Null(slider.Device);
        // The tile and the Controls row name the apps as the dialog does.
        Assert.Equal("Spotify, quest", s.Vm.Sliders.Items[0].Label);
        Assert.Equal("Spotify, quest", In<TextBlock>(s.Page.GetVisualDescendants().OfType<SliderTile>().First(), "Label").Text);
        Assert.Equal("Spotify, quest", In<TextBlock>(s.Page.FindControl<Border>("Inspector")!, "TargetText").Text);
        // The Everything else slider leaves them to this one.
        Assert.Contains("quest.exe", s.Vm.Sliders.Items[2].OtherAppsHint);
    }

    [AvaloniaFact]
    public void AppOnOtherSlider_ShowsOwner_DoneMovesIt()
    {
        using var s = Show();
        Open(s);

        Assert.Equal("Chat", Item(s, "Discord").OwnedBy);
        Assert.True(In<Border>(Row(s, "Discord"), "OwnerPill").IsVisible);
        Assert.Null(Item(s, "Spotify").OwnedBy);
        Assert.Equal(s.Vm.Sliders.Items[3].Label, Item(s, "game").OwnedBy);

        Click(s, "Discord");
        Assert.True(Item(s, "Discord").IsOn);
        Assert.Null(Item(s, "Discord").OwnedBy);
        Assert.Equal(new[] { "Discord.exe" }, s.Config.Sliders[1].Processes);   // not yet

        Done(s);
        Assert.Equal(new[] { "Discord.exe" }, s.Config.Sliders[0].Processes);
        Assert.Empty(s.Config.Sliders[1].Processes);
        Assert.Empty(s.Vm.Sliders.Items[1].AppNames);
        Assert.Equal(new[] { "game.exe" }, s.Config.Sliders[3].Processes);
    }

    [AvaloniaFact]
    public void EverythingElse_Exclusive()
    {
        using var s = Show();
        Open(s);

        Click(s, "Spotify");
        Click(s, "Everything else");
        Assert.Equal(new[] { "Everything else" }, On(s));
        // A tick replaces it.
        Click(s, "chrome");
        Assert.Equal(new[] { "chrome" }, On(s));
        Click(s, "Everything else");

        Done(s);
        Assert.Equal(SliderMappingType.Unmapped, s.Config.Sliders[0].Type);
        Assert.Empty(s.Config.Sliders[0].Processes);
        // The other Everything else slider becomes an apps slider with no apps.
        Assert.Equal(SliderMappingType.Processes, s.Config.Sliders[2].Type);
        Assert.Empty(s.Config.Sliders[2].Processes);
        Assert.True(s.Vm.Sliders.Items[2].IsApps);
        Assert.True(s.Vm.Sliders.Items[0].IsOtherApps);
    }

    [AvaloniaFact]
    public void ZeroApps_Refused_ErrorClearsWhenFixed()
    {
        using var s = Show();
        Open(s);

        Click(s, "Spotify");
        Click(s, "Spotify");
        Assert.Empty(On(s));
        Assert.Null(s.Dialog.Error);   // not before Done

        Done(s);
        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Tick at least one app, or choose a device.", s.Dialog.Error);
        Assert.True(In<TextBlock>(s.View, "ErrorText").IsVisible);
        Assert.Equal("Tick at least one app, or choose a device.", In<TextBlock>(s.View, "ErrorText").Text);
        Assert.Equal(SliderMappingType.Device, s.Config.Sliders[0].Type);

        // After the first Done the error follows each change.
        Click(s, "chrome");
        Assert.Null(s.Dialog.Error);
        Assert.False(In<TextBlock>(s.View, "ErrorText").IsVisible);
        Click(s, "chrome");
        Assert.NotNull(s.Dialog.Error);
        Click(s, "Speakers");
        Assert.Null(s.Dialog.Error);

        Done(s);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal("dev-1", s.Config.Sliders[0].Device!.Id);
    }

    [AvaloniaFact]
    public void Done_UnchangedInvalid_ShowsError_StaysOpen()
    {
        // A saved apps slider with no apps: Done validates even when nothing changed.
        using var s = Show(c => c.Sliders[3].Processes.Clear());
        Open(s, 3);
        Done(s);

        Assert.True(s.Controls.HasDialog);
        Assert.Equal("Tick at least one app, or choose a device.", s.Dialog.Error);
        Assert.True(In<TextBlock>(s.View, "ErrorText").IsVisible);
    }

    [AvaloniaFact]
    public void Done_UnchangedValid_ClosesWithoutWriting()
    {
        using var s = Show();
        var saved = s.Config.Sliders[1];
        Open(s, 1);
        Done(s);

        Assert.False(s.Controls.HasDialog);
        Assert.Same(saved, s.Config.Sliders[1]);
        Assert.Equal(new[] { "Discord.exe" }, s.Config.Sliders[1].Processes);
    }

    [AvaloniaFact]
    public void OtherApp_PickerFails_IsLogged_DialogStays()
    {
        using var s = Show();
        Open(s);
        s.Dialog.PickExe = () => Task.FromException<string?>(new InvalidOperationException("no picker"));

        Click(s, "Other app…");

        Assert.True(s.Controls.HasDialog);
        Assert.Equal(new[] { "Main output" }, On(s));
    }

    // ── Search ────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Query_spo_FindsSpotifyByExe()
    {
        using var s = Show();
        Open(s);

        s.Window.KeyTextInput("spo");
        Settle(s.Window);
        // Spotify by its name; Speakers only by its Windows name ("Speakers (Realtek Audio)").
        Assert.Equal(new[] { "Devices", "Apps" }, s.Dialog.Sections.Select(x => x.Name));
        Assert.True(s.Dialog.Sections.All(x => x.IsSearching));
        var speakers = Assert.Single(s.Dialog.Sections[0].Items);
        Assert.Equal("Speakers", speakers.Name);
        Assert.Empty(speakers.Highlight);
        var apps = s.Dialog.Sections[1];
        Assert.Equal("Spotify", apps.Items[0].Name);
        Assert.Equal(new[] { 0, 1, 2 }, apps.Items[0].Highlight);
        Assert.Equal(new[] { "Spo" }, In<HighlightText>(Row(s, "Spotify"), "ItemName").HighlightedRuns);
        Assert.Equal("1", In<TextBlock>(s.View, "MatchCountText").Text);
        // Enter goes to the best match, below the Devices section.
        Assert.True(apps.Items[0].IsCurrent);
        Assert.False(speakers.IsCurrent);
        PressEnter(s);
        Assert.Equal(new[] { "Spotify" }, On(s));

        // The exe name is secondary text: it matches with no highlight on the name.
        SearchBox(s).Text = "cord.exe";
        Settle(s.Window);
        var match = Assert.Single(s.Dialog.VisibleItems);
        Assert.Equal("Discord", match.Name);
        Assert.Empty(match.Highlight);

        SearchBox(s).Text = "zzz";
        Settle(s.Window);
        Assert.True(s.Dialog.HasNoMatches);
        Assert.True(In<TextBlock>(s.View, "NoMatchText").IsVisible);
        Assert.Equal("Nothing matches “zzz”.", In<TextBlock>(s.View, "NoMatchText").Text);

        SearchBox(s).Text = "";
        Settle(s.Window);
        Assert.Equal(3, s.Dialog.Sections.Count);
    }

    // ── Cancel ────────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Cancel_NoWrite()
    {
        using var s = Show();
        string Now() => string.Join(" | ", s.Config.Sliders.Select(x => $"{x.Type} {x.Device?.Id} {string.Join(",", x.Processes)}"));
        var before = Now();

        Open(s);
        Click(s, "Discord");
        Click(s, "Everything else");
        In<Button>(s.View, "CancelButton").Command!.Execute(null);
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal(before, Now());

        Open(s);
        Click(s, "Cans");
        s.Window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal(before, Now());

        Open(s);
        Click(s, "Discord");
        In<Button>(s.View, "CloseButton").Command!.Execute(null);
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal(before, Now());

        // A new dialog starts from the saved target again.
        Open(s);
        Assert.Equal(new[] { "Main output" }, On(s));
    }

    [AvaloniaFact]
    public void SliderRemoved_WhileOpen_ClosesWithoutWrite()
    {
        using var s = Show();
        Open(s, slider: 3);
        Click(s, "Spotify");
        s.Vm.Sliders.RemoveLast();
        Settle(s.Window);
        Assert.False(s.Controls.HasDialog);
        Assert.Equal(new[] { "Discord.exe" }, s.Config.Sliders[1].Processes);
    }

    // ── Device changes (ruling 5) ─────────────────────────────────────────────

    [AvaloniaFact]
    public void DevicePluggedIn_WhileOpen_AppearsInList()
    {
        using var s = Show();
        Open(s);

        var added = new AudioDevice("dev-3", "USB DAC", AudioFlow.Render, false);
        s.Rig.Engine.Devices.Add(added);
        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, added);
        Settle(s.Window);

        Assert.Contains("USB DAC", s.Dialog.Sections[0].Items.Select(i => i.Name));
        Click(s, "USB DAC");
        Done(s);
        Assert.Equal("dev-3", s.Config.Sliders[0].Device!.Id);

        // A closed dialog stops following.
        Open(s);
        var closed = s.Dialog;
        closed.Cancel();
        var gone = new AudioDevice("dev-4", "Dock", AudioFlow.Render, false);
        s.Rig.Engine.Devices.Add(gone);
        s.Rig.Engine.RaiseDeviceChanged(DeviceChangeKind.Added, gone);
        Settle(s.Window);
        Assert.DoesNotContain("Dock", closed.Sections[0].Items.Select(i => i.Name));
    }

    // ── App names, icons and meters ───────────────────────────────────────────

    private sealed class FakeIcons : IAppIcons
    {
        public List<string> Requests { get; } = new();

        public Task<Bitmap?> GetIconAsync(string iconSource, int size)
        {
            Requests.Add(iconSource);
            return Task.FromResult<Bitmap?>(new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96),
                                                                PixelFormat.Bgra8888, AlphaFormat.Premul));
        }
    }

    private static void Named(FakeAudioEngine e)
    {
        e.Sessions[0] = Session("Spotify.exe", 1) with { DisplayName = "Spotify Music", IconPath = @"C:\Apps\Spotify.exe" };
        e.Sessions[2] = Session("Spotify.exe", 3) with { DisplayName = "Spotify Music", IconPath = @"C:\Apps\Spotify.exe" };
    }

    [AvaloniaFact]
    public void AppRow_UsesSessionDisplayName_SecondaryExe()
    {
        using var s = Show(setup: Named);
        Open(s);

        var row = Item(s, "Spotify Music");
        Assert.Equal("Spotify.exe", row.Secondary);
        Assert.Equal("app:Spotify.exe", row.Key);
        Assert.Equal("chrome.exe", Item(s, "chrome").Secondary);   // no display name: the exe name above
        Assert.Equal("Not running", Item(s, "game").Secondary);
    }

    [AvaloniaFact]
    public void AppRow_SearchMatchesExeAndDisplayName()
    {
        using var s = Show(setup: Named);
        Open(s);

        s.Dialog.Query = "music";
        Assert.Contains(s.Dialog.VisibleItems, i => i.Name == "Spotify Music");
        s.Dialog.Query = "spotify.exe";
        Assert.Contains(s.Dialog.VisibleItems, i => i.Name == "Spotify Music");
    }

    [AvaloniaFact]
    public void AppRow_NoIconService_ShowsLetterTile()
    {
        using var s = Show(setup: Named);
        Open(s);

        var row = Item(s, "Spotify Music");
        Assert.Null(row.AppIcon);
        Assert.Equal("S", row.Letter);
        Assert.True(row.ShowLetter);
        Assert.Contains(Rows(s).Select(b => ((TargetItem)b.DataContext!).Name), n => n == "Spotify Music");
        Assert.Contains(s.View.GetVisualDescendants().OfType<Border>(), b => b.Name == "LetterTile" && b.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void AppRow_IconLoaded_Shown()
    {
        var icons = new FakeIcons();
        using var s = Show(icons: icons, setup: Named);
        Open(s);

        var row = Item(s, "Spotify Music");
        Assert.NotNull(row.AppIcon);
        Assert.True(row.ShowAppIcon);
        Assert.False(row.ShowLetter);
        Assert.Single(icons.Requests);   // once per distinct icon source, however many sessions
        Assert.Contains(s.View.GetVisualDescendants().OfType<Image>(), i => i.Name == "AppIconImage" && i.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void NotRunningApp_SavedExePath_ShowsItsIcon_OtherAppSavesThePath()
    {
        var icons = new FakeIcons();
        using var s = Show(c => c.RememberAppPath("game.exe", @"C:\Games\game.exe"), icons);
        Open(s);
        Settle(s.Window);

        var game = Item(s, "game");
        Assert.Equal("Not running", game.Secondary);
        Assert.NotNull(game.AppIcon);
        Assert.Contains(@"C:\Games\game.exe", icons.Requests);

        s.Dialog.PickExe = () => Task.FromResult<string?>(@"C:\Games\Quest\quest.exe");
        Click(s, "Other app…");
        Settle(s.Window);
        Assert.NotNull(Item(s, "quest").AppIcon);
        Assert.Null(s.Config.AppPathFor("quest.exe"));   // the draft's, until Done

        Done(s);
        Assert.Equal(@"C:\Games\Quest\quest.exe", s.Config.Sliders[0].AppPathFor("quest.exe"));
    }

    [AvaloniaFact]
    public void NoExePath_FallsBackToImageName_NoIcon()
    {
        var icons = new FakeIcons();
        using var s = Show(icons: icons);   // the plain sessions have neither display name nor icon path
        Open(s);

        var row = Item(s, "Spotify");
        Assert.Null(row.AppIcon);
        Assert.Equal("S", row.Letter);
        Assert.Empty(icons.Requests);
    }

    [AvaloniaFact]
    public void PollMeters_MaxOverSessions_SameMeterAcrossRebuilds()
    {
        using var s = Show();
        s.Rig.Engine.Peaks["s-1"] = 0.2f;
        s.Rig.Engine.Peaks["s-3"] = 0.7f;
        Open(s);

        var meter = Item(s, "Spotify").Meter!;
        Assert.Null(Item(s, "game").Meter);
        s.Dialog.PollMeters();
        Assert.Equal(0.7, meter.Level, 3);

        s.Dialog.Query = "spot";   // rebuilds the rows
        Assert.Same(meter, Item(s, "Spotify").Meter);
        s.Rig.Engine.Peaks["s-3"] = 0.1f;
        s.Dialog.PollMeters();
        Assert.Equal(0.2, meter.Level, 3);
    }

    [AvaloniaFact]
    public void Meters_TimerStopsWhenDialogCloses()
    {
        using var s = Show();
        Open(s);
        var view = s.View;
        Assert.True(view.MetersRunning);

        s.Controls.CloseDialog();
        Settle(s.Window);

        Assert.False(view.MetersRunning);
    }

    // ── Session changes ───────────────────────────────────────────────────────

    [AvaloniaFact]
    public void SessionStarted_WhileOpen_AddsRowAndKeepsDraft()
    {
        var icons = new FakeIcons();
        using var s = Show(icons: icons);
        Open(s, slider: 3);
        Click(s, "Spotify");
        s.Dialog.Query = "vlc";
        Assert.True(s.Dialog.HasNoMatches);

        var vlc = Session("vlc.exe", 9) with { DisplayName = "VLC media player", IconPath = @"C:\Apps\vlc.exe" };
        s.Rig.Engine.Sessions.Add(vlc);
        s.Rig.Engine.RaiseSessionCreated(vlc);
        Settle(s.Window);

        Assert.Equal("vlc", s.Dialog.Query);
        var row = Assert.Single(s.Dialog.VisibleItems);
        Assert.Equal("VLC media player", row.Name);
        Assert.NotNull(row.Meter);
        Assert.Contains(@"C:\Apps\vlc.exe", icons.Requests);
        Assert.NotNull(Item(s, "VLC media player").AppIcon);

        s.Dialog.Query = "";
        Assert.Equal(new[] { "Spotify", "game" }, On(s).OrderBy(n => n, StringComparer.Ordinal));
    }

    [AvaloniaFact]
    public void SessionEnded_WhileOpen_MeterDropsToZero()
    {
        using var s = Show();
        s.Rig.Engine.Peaks["s-2"] = 0.5f;
        Open(s);
        s.Dialog.PollMeters();
        var meter = Item(s, "chrome").Meter!;
        Assert.Equal(0.5, meter.Level, 3);

        var chrome = s.Rig.Engine.Sessions.Single(x => x.ImageName == "chrome.exe");
        s.Rig.Engine.Sessions.Remove(chrome);
        s.Rig.Engine.RaiseSessionExpired(chrome);
        Settle(s.Window);
        s.Dialog.PollMeters();

        Assert.Equal(0, meter.Level, 3);
        Assert.DoesNotContain(s.Dialog.VisibleItems, i => i.Name == "chrome");
    }

    [AvaloniaFact]
    public void Dispose_UnsubscribesFromSessions()
    {
        using var s = Show();
        var before = s.Rig.Engine.SessionsChangedSubscriberCount;
        Open(s);
        var dialog = s.Dialog;
        Assert.Equal(before + 1, s.Rig.Engine.SessionsChangedSubscriberCount);

        s.Controls.CloseDialog();
        Settle(s.Window);

        Assert.Equal(before, s.Rig.Engine.SessionsChangedSubscriberCount);
        var vlc = Session("vlc.exe", 9);
        s.Rig.Engine.Sessions.Add(vlc);
        s.Rig.Engine.RaiseSessionCreated(vlc);
        Settle(s.Window);
        Assert.DoesNotContain(dialog.Sections.SelectMany(x => x.Items), i => i.Name == "vlc");
    }

    [AvaloniaFact]
    public void UnpluggedSavedMic_ListedAsInput()
    {
        // No live device and no saved settings: only the endpoint id says it is an input.
        using var s = Show(c => c.Sliders[0].Device = new DeviceRefConfig { Id = "{0.0.1.00000000}.{rode}", FriendlyName = "Podcast Mic (Rode)" },
                           setup: AddMic);
        Open(s);

        var devices = s.Dialog.Sections[0].Items;
        Assert.Equal(new[] { "Main output", "Speakers", "Cans", "Microphone", "Podcast Mic" }, devices.Select(i => i.Name));
        var saved = devices[^1];
        Assert.True(saved.IsOn);
        Assert.Equal("Input · Podcast Mic (Rode)", saved.Secondary);
        Assert.Equal(AudioFlow.Capture, saved.Flow);
        Assert.Equal("Icon.Device.Mic", saved.IconKey);
    }
}
