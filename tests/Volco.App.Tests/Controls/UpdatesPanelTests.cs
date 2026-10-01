using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Volco.App.Controls;
using Volco.Core.Config;
using Volco.Core.Updates;
using Xunit;

namespace Volco.App.Tests.Controls;

public class UpdatesPanelTests
{
    /// <summary>Shows the panel in a window (as the General page does) and returns it.</summary>
    private static UpdatesPanel Host(UpdatesPanel panel)
    {
        new Window { Content = panel }.Show();
        return panel;
    }

    private static ConfigManager NewConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigManager(Path.Combine(dir, "config.json"));
        config.Load();
        return config;
    }

    [AvaloniaFact]
    public void UpdatesPanel_Renders_WithCurrentDisplayVersion()
    {
        // The default constructor builds a GitHubUpdater but never calls it: like the WinUI
        // window, nothing is checked until the user presses the button, so no network here.
        var window = Host(new UpdatesPanel(NewConfig()));

        Assert.Equal($"Current version: {BuildInfo.DisplayVersion}",
            window.FindControl<TextBlock>("CurrentVersionText")!.Text);
        Assert.False(window.FindControl<Button>("UpdateButton")!.IsEnabled);
        Assert.False(window.FindControl<ProgressBar>("Progress")!.IsVisible);
    }

    [AvaloniaFact]
    public void UpdatesPanel_CheckWithoutToken_PromptsForTokenWithoutNetwork()
    {
        var window = Host(new UpdatesPanel(NewConfig()));

        window.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Enter a GitHub token to check for updates.",
            window.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(window.FindControl<Button>("CheckButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void UpdatesPanel_SeedsTokenAndCiFlagFromConfig()
    {
        var config = NewConfig();
        config.Config.Updates.GitHubToken = "ghp_abc";
        config.Config.Updates.UseCiArtifact = true;

        var window = Host(new UpdatesPanel(config));

        var token = window.FindControl<TextBox>("TokenBox")!;
        Assert.Equal("ghp_abc", token.Text);
        Assert.NotEqual('\0', token.PasswordChar);
        Assert.True(window.FindControl<CheckBox>("UseCiCheckBox")!.IsChecked);
    }

    // ---- Check flow, driven against a stubbed GitHub API (no network) ----

    private sealed class StubHandler(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(respond());
    }

    private static HttpResponseMessage Json(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body) };

    private static string ReleaseJson(string tag, string? notes) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            tag_name = tag,
            body = notes,
            assets = new[] { new { name = "Volco.exe", url = "https://api.github.com/assets/1" } },
        });

    private static UpdatesPanel Open(Func<HttpResponseMessage> respond)
    {
        var updater = new GitHubUpdater(new HttpClient(new StubHandler(respond)));
        var window = Host(new UpdatesPanel(NewConfig(), updater));
        window.FindControl<TextBox>("TokenBox")!.Text = "ghp_test";
        return window;
    }

    /// <summary>Clicks Check and pumps the UI thread until the check finishes (bounded to 5s).</summary>
    private static void ClickCheckAndWait(UpdatesPanel window)
    {
        var check = window.FindControl<Button>("CheckButton")!;
        check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!check.IsEnabled && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        Assert.True(check.IsEnabled, "check did not finish within 5s");
    }

    private static string Status(UpdatesPanel w) => w.FindControl<TextBlock>("StatusText")!.Text!;
    private static bool UpdateEnabled(UpdatesPanel w) => w.FindControl<Button>("UpdateButton")!.IsEnabled;

    [AvaloniaFact]
    public void UpdatesPanel_Check_NewerRelease_OffersUpdateWithNotes()
    {
        var window = Open(() => Json(ReleaseJson("v99.0.0", "Fixes things")));

        ClickCheckAndWait(window);

        Assert.StartsWith("Update available:", Status(window));
        Assert.True(UpdateEnabled(window));
        var notes = window.FindControl<TextBlock>("NotesText")!;
        Assert.True(notes.IsVisible);
        Assert.Equal("Fixes things", notes.Text);
        Assert.False(window.FindControl<ProgressBar>("Progress")!.IsVisible);
    }

    [AvaloniaFact]
    public void UpdatesPanel_Check_SameVersion_ReportsUpToDate()
    {
        var window = Open(() => Json(ReleaseJson($"v{BuildInfo.Version}", "old notes")));

        ClickCheckAndWait(window);

        Assert.Equal($"You're up to date ({BuildInfo.DisplayVersion}).", Status(window));
        Assert.False(UpdateEnabled(window));
        Assert.False(window.FindControl<TextBlock>("NotesText")!.IsVisible);
    }

    [AvaloniaFact]
    public void UpdatesPanel_Check_NoRelease_ReportsNoneFound()
    {
        // No tag_name: GitHubUpdater returns null.
        var window = Open(() => Json("{}"));

        ClickCheckAndWait(window);

        Assert.Equal("No published release was found.", Status(window));
        Assert.False(UpdateEnabled(window));
    }

    [AvaloniaFact]
    public void UpdatesPanel_Check_ServerError_ShowsFailureAndReenablesCheck()
    {
        var window = Open(() => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError));

        ClickCheckAndWait(window);

        Assert.StartsWith("Update check failed:", Status(window));
        Assert.False(UpdateEnabled(window));
        Assert.True(window.FindControl<Button>("CheckButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void UpdatesPanel_TogglingCiAfterAvailable_DisablesUpdate()
    {
        var window = Open(() => Json(ReleaseJson("v99.0.0", null)));
        ClickCheckAndWait(window);
        Assert.True(UpdateEnabled(window));

        window.FindControl<CheckBox>("UseCiCheckBox")!.IsChecked = true;

        Assert.False(UpdateEnabled(window));
    }
}
