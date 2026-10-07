using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;

namespace Volco.App.Controls;

/// <summary>
/// The General page's Updates panel: checks GitHub Releases for a newer Volco, downloads its
/// exe, and relaunches into it. Shown on release builds only (see <see cref="BuildInfo.IsRelease"/>).
/// It does not check on open; the user presses "Check for updates".
/// </summary>
public partial class UpdatesPanel : UserControl
{
    private readonly GitHubUpdater _updater;
    private readonly UpdateInstaller _installer;
    private readonly TextBlock _currentVersionText;
    private readonly TextBlock _statusText;
    private readonly ProgressBar _progress;
    private readonly TextBlock _notesText;
    private readonly Button _checkButton;
    private readonly Button _updateButton;
    private UpdateInfo? _available;

    // Updater and installer are injectable so tests never touch the network.
    public UpdatesPanel(GitHubUpdater? updater = null, UpdateInstaller? installer = null)
    {
        _updater = updater ?? new GitHubUpdater();
        _installer = installer ?? new UpdateInstaller();

        AvaloniaXamlLoader.Load(this);
        _currentVersionText = this.FindControl<TextBlock>("CurrentVersionText")!;
        _statusText = this.FindControl<TextBlock>("StatusText")!;
        _progress = this.FindControl<ProgressBar>("Progress")!;
        _notesText = this.FindControl<TextBlock>("NotesText")!;
        _checkButton = this.FindControl<Button>("CheckButton")!;
        _updateButton = this.FindControl<Button>("UpdateButton")!;

        _currentVersionText.Text = $"Current version: {BuildInfo.DisplayVersion}";

        _checkButton.Click += OnCheckClicked;
        _updateButton.Click += OnUpdateClicked;
    }

    private async void OnCheckClicked(object? sender, RoutedEventArgs e)
    {
        SetBusy(true, "Checking for updates…");
        _notesText.IsVisible = false;
        _available = null;

        try
        {
            var info = await _updater.GetLatestReleaseAsync();

            // Continuations normally resume on the UI thread; marshal anyway so a detached
            // context can never touch controls off-thread.
            await RunOnUiAsync(() => ShowCheckResult(info));
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

    private void ShowCheckResult(UpdateInfo? info)
    {
        if (info == null)
        {
            _statusText.Text = "No published release was found.";
            return;
        }

        if (info.Version != null && info.Version <= BuildInfo.Version)
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

        SetBusy(true, "Downloading update…");
        _progress.IsVisible = true;
        _progress.IsIndeterminate = false;
        _progress.Value = 0;

        try
        {
            var progress = new Progress<double>(p =>
                Dispatcher.UIThread.Post(() => _progress.Value = p));

            var stagedExe = await _installer.DownloadAsync(_available, progress);

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

    private void SetBusy(bool busy, string? status = null)
    {
        _checkButton.IsEnabled = !busy;
        if (status != null) _statusText.Text = status;
        _progress.IsVisible = busy;
        _progress.IsIndeterminate = busy;
        if (busy) _updateButton.IsEnabled = false;
    }
}
