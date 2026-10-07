using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Volco.App.Controls;
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

    [AvaloniaFact]
    public void UpdatesPanel_Renders_WithCurrentDisplayVersion()
    {
        // The default constructor builds a GitHubUpdater but never calls it: nothing is checked
        // until the user presses the button, so no network here.
        var window = Host(new UpdatesPanel());

        Assert.Equal($"Current version: {BuildInfo.DisplayVersion}",
            window.FindControl<TextBlock>("CurrentVersionText")!.Text);
        Assert.False(window.FindControl<Button>("UpdateButton")!.IsEnabled);
        Assert.False(window.FindControl<ProgressBar>("Progress")!.IsVisible);
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
            assets = new[] { new { name = "Volco.exe", browser_download_url = "https://github.com/x/Volco.exe" } },
        });

    private static UpdatesPanel Open(Func<HttpResponseMessage> respond)
    {
        var updater = new GitHubUpdater(new HttpClient(new StubHandler(respond)));
        return Host(new UpdatesPanel(updater));
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
    public void UpdatesPanel_Check_NotFound_ReportsNoneFound()
    {
        // GitHub answers 404 when the repository has no published release.
        var window = Open(() => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

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
}
