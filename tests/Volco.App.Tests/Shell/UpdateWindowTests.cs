using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Volco.App.Views;
using Volco.Core.Config;
using Volco.Core.Updates;
using Xunit;

namespace Volco.App.Tests.Shell;

public class UpdateWindowTests
{
    private static ConfigManager NewConfig()
    {
        var dir = Path.Combine(Path.GetTempPath(), "volco-app-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var config = new ConfigManager(Path.Combine(dir, "config.json"));
        config.Load();
        return config;
    }

    [AvaloniaFact]
    public void UpdateWindow_Renders_WithCurrentDisplayVersion()
    {
        // The default constructor builds a GitHubUpdater but never calls it: like the WinUI
        // window, nothing is checked until the user presses the button, so no network here.
        var window = new UpdateWindow(NewConfig());
        window.Show();

        Assert.Equal($"Current version: {BuildInfo.DisplayVersion}",
            window.FindControl<TextBlock>("CurrentVersionText")!.Text);
        Assert.Equal(560, window.Width);
        Assert.Equal(460, window.Height);
        Assert.False(window.FindControl<Button>("UpdateButton")!.IsEnabled);
        Assert.False(window.FindControl<ProgressBar>("Progress")!.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void UpdateWindow_CheckWithoutToken_PromptsForTokenWithoutNetwork()
    {
        var window = new UpdateWindow(NewConfig());
        window.Show();

        window.FindControl<Button>("CheckButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        Assert.Equal("Enter a GitHub token to check for updates.",
            window.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(window.FindControl<Button>("CheckButton")!.IsEnabled);
        window.Close();
    }

    [AvaloniaFact]
    public void UpdateWindow_SeedsTokenAndCiFlagFromConfig()
    {
        var config = NewConfig();
        config.Config.Updates.GitHubToken = "ghp_abc";
        config.Config.Updates.UseCiArtifact = true;

        var window = new UpdateWindow(config);
        window.Show();

        var token = window.FindControl<TextBox>("TokenBox")!;
        Assert.Equal("ghp_abc", token.Text);
        Assert.NotEqual('\0', token.PasswordChar);
        Assert.True(window.FindControl<CheckBox>("UseCiCheckBox")!.IsChecked);
        window.Close();
    }
}
