using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;

namespace Volco.App.Views;

/// <summary>
/// Self-contained updater dialog: checks GitHub for a newer release (or the latest CI artifact
/// when the advanced option is enabled), downloads it, and relaunches into the new build.
/// Only reachable from the tray menu on release builds (see <see cref="BuildInfo.IsRelease"/>).
/// Like the WinUI original it does not check on open; the user presses "Check for Updates".
/// </summary>
public partial class UpdateWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly GitHubUpdater _updater;
    private readonly UpdateInstaller _installer;
    private readonly TextBlock _currentVersionText;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progress;
    private readonly TextBlock _notesText;
    private readonly TextBox _tokenBox;
    private readonly CheckBox _useCiCheckBox;
    private readonly Button _checkButton;
    private readonly Button _updateButton;
    private UpdateInfo? _available;

    // Updater and installer are injectable so tests never touch the network.
    public UpdateWindow(ConfigManager configManager, GitHubUpdater? updater = null, UpdateInstaller? installer = null)
    {
        _configManager = configManager;
        _updater = updater ?? new GitHubUpdater();
        _installer = installer ?? new UpdateInstaller();

        AvaloniaXamlLoader.Load(this);
        _currentVersionText = this.FindControl<TextBlock>("CurrentVersionText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
        _notesText = this.FindControl<TextBlock>("NotesText")!;
        _tokenBox = this.FindControl<TextBox>("TokenBox")!;
        _useCiCheckBox = this.FindControl<CheckBox>("UseCiCheckBox")!;
        _checkButton = this.FindControl<Button>("CheckButton")!;
        _updateButton = this.FindControl<Button>("UpdateButton")!;

        _currentVersionText.Text = $"Current version: {BuildInfo.DisplayVersion}";

        var cfg = _configManager.Config.Updates;
        _useCiCheckBox.IsChecked = cfg.UseCiArtifact;
        _tokenBox.Text = cfg.GitHubToken ?? "";

        // Subscribe after seeding so the initial IsChecked does not look like a user change.
        _useCiCheckBox.IsCheckedChanged += OnUseCiChanged;
        _checkButton.Click += OnCheckClicked;
        _updateButton.Click += OnUpdateClicked;
    }

    private string Token => _tokenBox.Text ?? "";

    private void OnUseCiChanged(object? sender, RoutedEventArgs e)
    {
        // A previously-found update no longer matches the selected source.
        _available = null;
        _updateButton.IsEnabled = false;
    }

    private async void OnCheckClicked(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Token))
        {
            _statusText.Text = "Enter a GitHub token to check for updates.";
            return;
        }

        PersistPreferences();
        SetBusy(true, "Checking for updates…");
        _notesText.IsVisible = false;
        _updateButton.IsEnabled = false;
        _available = null;

        try
        {
            var useCi = _useCiCheckBox.IsChecked == true;
            var token = Token;
            UpdateInfo? info = useCi
                ? await _updater.GetLatestCiArtifactAsync(token)
                : await _updater.GetLatestReleaseAsync(token);

            // Continuations normally resume on the UI thread; marshal anyway so a detached
            // context can never touch controls off-thread.
            await RunOnUiAsync(() => ShowCheckResult(info, useCi));
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update check failed: {ex}");
            await RunOnUiAsync(() => _statusText.Text = $"Update check failed: {ex.Message}");
        }
        finally
        {
            await RunOnUiAsync(() => SetBusy(false));
        }
    }

    private void ShowCheckResult(UpdateInfo? info, bool useCi)
    {
        if (info == null)
        {
            _statusText.Text = useCi
                ? "No CI build artifact was found."
                : "No published release was found.";
            return;
        }

        // For releases we can compare versions; CI artifacts are unversioned, so always offer them.
        if (info.Source == UpdateSource.Release && info.Version != null && info.Version <= BuildInfo.Version)
        {
            _statusText.Text = $"You're up to date ({BuildInfo.DisplayVersion}).";
            return;
        }

        _available = info;
        _statusText.Text = $"Update available: {info.DisplayName}";
        if (!string.IsNullOrWhiteSpace(info.Notes))
        {
            _notesText.Text = info.Notes;
            _notesText.IsVisible = true;
        }
        _updateButton.IsEnabled = true;
    }

    private async void OnUpdateClicked(object? sender, RoutedEventArgs e)
    {
        if (_available == null) return;

        PersistPreferences();
        SetBusy(true, "Downloading update…");
        _progress.IsVisible = true;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;

        try
        {
            var token = Token;
            var progress = new Progress<double>(p =>
                Dispatcher.UIThread.Post(() => _progress.Value = p));

            var stagedExe = await _installer.DownloadAsync(_available, token, progress);

            await RunOnUiAsync(() => _statusText.Text = "Update downloaded. Restarting Volco…");
            AppLog.Info($"Update downloaded to {stagedExe}; handing off to installer.");

            UpdateInstaller.ApplyAndRestart(stagedExe);
            VolcoApplication.RequestExit();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update install failed: {ex}");
            await RunOnUiAsync(() =>
            {
                _statusText.Text = $"Update failed: {ex.Message}";
                SetBusy(false);
            });
        }
    }

    private static Task RunOnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return Dispatcher.UIThread.InvokeAsync(action).GetTask();
    }

    private void PersistPreferences()
    {
        var cfg = _configManager.Config.Updates;
        cfg.UseCiArtifact = _useCiCheckBox.IsChecked == true;
        cfg.GitHubToken = string.IsNullOrWhiteSpace(Token) ? null : Token;
        _configManager.RequestSave();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _checkButton.IsEnabled = !busy;
        if (status != null) _statusText.Text = status;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = busy;
        if (busy) _updateButton.IsEnabled = false;
    }
}
