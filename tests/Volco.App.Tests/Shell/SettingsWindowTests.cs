using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views;
using Volco.App.Views.Pages;
using Volco.App.Views.Stage;
using Volco.Core.Config;
using Volco.Core.Runtime;
using Volco.Core.Updates;
using Volco.Core.Tests.Audio;
using Xunit;

namespace Volco.App.Tests.Shell;

public class SettingsWindowTests
{
    private static SettingsWindow Create(Action<AppConfig>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigManager(Path.Combine(dir, "config.json"));
        config.Load();
        configure?.Invoke(config.Config);
        var runtime = new VolcoRuntime(config, new FakeAudioEngine(), audioReady: true);
        var vm = new SettingsViewModel(config, runtime.Audio, runtime, new AvaloniaUiDispatcher());
        return new SettingsWindow(vm, runtime);
    }

    [AvaloniaFact]
    public void SettingsWindow_Close_HidesAndCanReopen()
    {
        var window = Create();
        window.Show();
        Assert.True(window.IsVisible);

        window.Close();

        Assert.False(window.IsVisible);

        window.Show();
        Assert.True(window.IsVisible);
        var host = window.FindControl<ContentControl>("PageHost")!;
        Assert.IsType<GeneralPage>(host.Content);
    }

    [AvaloniaFact]
    public void SettingsWindow_NavSelection_SwapsPage()
    {
        var window = Create();
        window.Show();
        var nav = window.FindControl<ListBox>("NavList")!;
        var host = window.FindControl<ContentControl>("PageHost")!;

        var expected = new[]
        {
            typeof(GeneralPage), typeof(StagePage),
        };
        Assert.Equal(2, nav.ItemCount);
        for (var i = 0; i < expected.Length; i++)
        {
            nav.SelectedIndex = i;
            Assert.IsType(expected[i], host.Content);
        }
        Assert.Equal(
            new[] { "General", "Controller" },
            nav.Items.Cast<NavItem>().Select(n => n.Label).ToArray());
    }

    [AvaloniaFact]
    public void SettingsWindow_ShowPage_SwitchesByTag()
    {
        var window = Create();
        window.Show();

        window.ShowPage("controls");
        Assert.IsType<StagePage>(window.CurrentPage);

        window.ShowPage("general");
        Assert.IsType<GeneralPage>(window.CurrentPage);
    }

    [AvaloniaFact]
    public void SettingsWindow_FooterShowsTheConnectionState()
    {
        var window = Create();
        window.Show();

        Assert.Equal("Looking for your controller", window.FindControl<TextBlock>("FooterText")!.Text);
    }

    [AvaloniaFact]
    public void SettingsWindow_AboutSitsAboveTheConnectionState()
    {
        var window = Create();
        window.Show();
        var about = window.FindControl<StackPanel>("AboutFooter")!;
        var connection = window.FindControl<StackPanel>("ConnectionFooter")!;

        Assert.True(about.IsEffectivelyVisible);
        Assert.Equal(new AboutViewModel().VersionText, window.FindControl<TextBlock>("VersionText")!.Text);
        var link = window.FindControl<HyperlinkButton>("RepoLink")!;
        Assert.Equal(new Uri("https://github.com/ToMeRhh/volco"), link.NavigateUri);
        Assert.True(link.IsEffectivelyVisible);
        var aboutBottom = about.TranslatePoint(new Point(0, about.Bounds.Height), window)!.Value.Y;
        var connectionTop = connection.TranslatePoint(new Point(0, 0), window)!.Value.Y;
        Assert.True(aboutBottom <= connectionTop, $"About ends at {aboutBottom}, the connection starts at {connectionTop}");
    }

    [Fact]
    public void About_DevBuild_CarriesTheBuildDetails_ReleaseJustTheVersion()
    {
        var dev = new AboutViewModel(isRelease: false);
        Assert.Equal($"Volco dev {BuildInfo.Commit}", dev.VersionText);
        Assert.Equal(BuildInfo.BuildDetails, dev.BuildDetails);
        Assert.Contains("Branch ", dev.BuildDetails);

        var release = new AboutViewModel(isRelease: true);
        Assert.Equal($"Volco v{BuildInfo.Version}", release.VersionText);
        Assert.Null(release.BuildDetails);
    }

    [AvaloniaFact]
    public void SettingsWindow_Notices_OpenAsADialog_AndClose()
    {
        var window = Create();
        window.Show();
        var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
        var overlay = window.FindControl<Border>("NoticesOverlay")!;
        var box = window.FindControl<TextBox>("NoticesBox")!;
        Assert.False(overlay.IsVisible);
        Assert.True(string.IsNullOrEmpty(box.Text));   // loaded on first open

        window.FindControl<HyperlinkButton>("NoticesLink")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsVisible);
        Assert.Contains("nanopb", box.Text);
        Assert.Contains("GNU LESSER GENERAL PUBLIC LICENSE", box.Text);
        Assert.Contains("SIL OPEN FONT LICENSE", box.Text!.ToUpperInvariant());

        window.FindControl<Button>("CloseNoticesButton")!.Command!.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(overlay.IsVisible);

        vm.About.OpenNoticesCommand.Execute(null);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.About.IsNoticesOpen);

        vm.About.OpenNoticesCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        window.MouseDown(new Point(20, window.Bounds.Height / 2), MouseButton.Left);   // the scrim, left of the card
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.About.IsNoticesOpen);
    }

    [AvaloniaFact]
    public void SettingsWindow_ExtendsIntoTheTitleBar_WithDragRegions()
    {
        var window = Create();
        window.Show();

        Assert.True(window.ExtendClientAreaToDecorationsHint);
        Assert.Equal(WindowDecorations.Full, window.WindowDecorations);
        foreach (var name in new[] { "SidebarTitleBar", "PageTitleBar" })
        {
            var region = window.FindControl<Border>(name)!;
            Assert.Equal("TitleBar", region.GetValue(WindowDecorationProperties.ElementRoleProperty).ToString());
        }
        Assert.Equal("Volco", window.FindControl<TextBlock>("BrandText")!.Text);
    }

    [AvaloniaFact]
    public void SettingsWindow_Decorations_HideTheTitleAndFullScreenButton()
    {
        var window = Create();
        window.Show();

        var theme = window.WindowDecorationsTheme!;
        Assert.Equal(typeof(WindowDrawnDecorations), theme.TargetType);
        Assert.NotNull(theme.BasedOn);
        var hidden = theme.Children.OfType<Style>()
            .Where(s => s.Setters.OfType<Setter>().Any(x => x.Property == Visual.IsVisibleProperty && Equals(x.Value, false)))
            .Select(s => s.Selector!.ToString())
            .ToList();
        Assert.Contains(hidden, s => s.Contains("Panel#PART_TitleTextPanel") && s.EndsWith("TextBlock"));
        Assert.Contains(hidden, s => s.EndsWith("Button#PART_FullScreenButton"));
    }

    [AvaloniaFact]
    public void SettingsWindow_UndoMessage_ShowsAndUndoes()
    {
        var window = Create();
        window.Show();
        var vm = Assert.IsType<SettingsViewModel>(window.DataContext);
        var toast = window.FindControl<Border>("Toast")!;
        Assert.False(toast.IsVisible);
        var undone = false;

        vm.Toast.Show("Slider 2 reset", () => undone = true);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(toast.IsVisible);
        Assert.Equal("Slider 2 reset", window.FindControl<TextBlock>("ToastText")!.Text);

        window.FindControl<Button>("UndoButton")!.Command!.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(undone);
        Assert.False(toast.IsVisible);
    }

    /// <summary>
    /// At the smallest window size every page still fits: nothing reaches past the page's right
    /// edge, and no wrapping text is squeezed into a narrow column (a letter or word per line).
    /// The Controls page shows a button with a program action (its USB tag and warning) in the
    /// inspector, with Timing open, in the compact layout.
    /// </summary>
    [AvaloniaFact]
    public void SettingsWindow_AtItsMinimumSize_EveryPageFits()
    {
        var window = Create(c =>
        {
            c.Modes.Add(new ModeConfig { Id = "gaming", Name = "A mode with a rather long name" });
            c.Buttons[0].Default().SinglePress.Action = ButtonActionType.OpenProgram;
            c.Buttons[0].Default().SinglePress.Command = new CommandConfig { Path = @"C:\Program Files\Some App\app.exe" };
            c.Buttons[1].Default().SinglePress.Action = ButtonActionType.ToggleOutputDevice;
        });
        window.Width = window.MinWidth;
        window.Height = window.MinHeight;
        window.Show();
        var nav = window.FindControl<ListBox>("NavList")!;
        var host = window.FindControl<ContentControl>("PageHost")!;
        var problems = new List<string>();

        for (int i = 0; i < nav.ItemCount; i++)
        {
            nav.SelectedIndex = i;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            if (host.Content is StagePage { DataContext: ControlsViewModel controls })
            {
                controls.Select(controls.Buttons.Items[0]);
                controls.Buttons.Items[0].IsAdvancedOpen = true;
            }
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var page = (Control)host.Content!;
            Collect(page, page, page.GetType().Name, problems);
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    /// <summary>Walks the page's visible controls, not into the inside of input controls (a text
    /// box's text may scroll past its edge).</summary>
    private static void Collect(Control page, Control control, string pageName, List<string> problems)
    {
        foreach (var child in control.GetVisualChildren().OfType<Control>())
        {
            if (!child.IsVisible || child.Bounds.Width <= 0) continue;
            var topLeft = child.TranslatePoint(new Point(0, 0), page);
            // A scroll bar's parts sit over the page's edge by design.
            if (child is Avalonia.Controls.Primitives.ScrollBar) continue;
            if (topLeft is { } p && p.X + child.Bounds.Width > page.Bounds.Width + 0.5)
                problems.Add($"{pageName}: {Describe(child)} ends at {p.X + child.Bounds.Width:F0}, page is {page.Bounds.Width:F0} wide");
            // Squeezed: four or more lines in a column under 200 px.
            if (child is TextBlock { TextWrapping: Avalonia.Media.TextWrapping.Wrap } text
                && text.Bounds.Height >= 4 * text.FontSize * 1.2 && text.Bounds.Width < 200)
                problems.Add($"{pageName}: {Describe(child)} \"{text.Text}\" is squeezed to {text.Bounds.Width:F0} wide");
            if (child is TextBox or ComboBox or NumericUpDown or AutoCompleteBox) continue;
            Collect(page, child, pageName, problems);
        }
    }

    private static string Describe(Control c)
        => c.Name is { Length: > 0 } name ? $"{c.GetType().Name} {name}" : c.GetType().Name;

    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, false, false, true)]
    [InlineData(WindowCloseReason.Undefined, false, false, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false, false, false)]
    [InlineData(WindowCloseReason.OSShutdown, false, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, true, false, false)]
    [InlineData(WindowCloseReason.WindowClosing, false, true, false)]
    public void SettingsWindow_ShouldHideInsteadOfClose_HonoursShutdownReasons(
        WindowCloseReason reason, bool allowClose, bool isExiting, bool expectedHide)
    {
        Assert.Equal(expectedHide, SettingsWindow.ShouldHideInsteadOfClose(reason, allowClose, isExiting));
    }
}
