using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Volco.Config;
using Volco.Updates;

namespace Volco.UI;

/// <summary>
/// Self-contained updater dialog: checks GitHub for a newer release (or the latest CI artifact
/// when the advanced option is enabled), downloads it, and relaunches into the new build.
/// Only reachable from the tray menu on release builds (see <see cref="Updates.BuildInfo.IsRelease"/>).
/// </summary>
public sealed partial class UpdateWindow : Window
{
    private readonly ConfigManager _configManager;
    private readonly GitHubUpdater _updater = new();
    private readonly UpdateInstaller _installer = new();
    private UpdateInfo? _available;

    public UpdateWindow(ConfigManager configManager)
    {
        InitializeComponent();
        _configManager = configManager;

        AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 460));
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
        }
        Title = "Volco — Software Update";

        CurrentVersionText.Text = $"Current version: {BuildInfo.DisplayVersion}";

        var cfg = _configManager.Config.Updates;
        UseCiCheckBox.IsChecked = cfg.UseCiArtifact;
        TokenBox.Password = cfg.GitHubToken ?? "";
        TokenPanel.Visibility = cfg.UseCiArtifact ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnUseCiChanged(object sender, RoutedEventArgs e)
    {
        var useCi = UseCiCheckBox.IsChecked == true;
        TokenPanel.Visibility = useCi ? Visibility.Visible : Visibility.Collapsed;
        // A previously-found update no longer matches the selected source.
        _available = null;
        UpdateButton.IsEnabled = false;
    }

    private async void OnCheckClicked(object sender, RoutedEventArgs e)
    {
        PersistPreferences();
        SetBusy(true, "Checking for updates…");
        NotesText.Visibility = Visibility.Collapsed;
        UpdateButton.IsEnabled = false;
        _available = null;

        try
        {
            var useCi = UseCiCheckBox.IsChecked == true;
            UpdateInfo? info = useCi
                ? await _updater.GetLatestCiArtifactAsync(TokenBox.Password)
                : await _updater.GetLatestReleaseAsync();

            if (info == null)
            {
                StatusText.Text = useCi
                    ? "No CI build artifact was found."
                    : "No published release was found.";
                return;
            }

            // For releases we can compare versions; CI artifacts are unversioned, so always offer them.
            if (info.Source == UpdateSource.Release && info.Version != null && info.Version <= BuildInfo.Version)
            {
                StatusText.Text = $"You're up to date ({BuildInfo.DisplayVersion}).";
                return;
            }

            _available = info;
            StatusText.Text = $"Update available: {info.DisplayName}";
            if (!string.IsNullOrWhiteSpace(info.Notes))
            {
                NotesText.Text = info.Notes;
                NotesText.Visibility = Visibility.Visible;
            }
            UpdateButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update check failed: {ex}");
            StatusText.Text = $"Update check failed: {ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnUpdateClicked(object sender, RoutedEventArgs e)
    {
        if (_available == null) return;

        PersistPreferences();
        SetBusy(true, "Downloading update…");
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = false;
        Progress.Value = 0;

        try
        {
            var token = UseCiCheckBox.IsChecked == true ? TokenBox.Password : null;
            var progress = new Progress<double>(p =>
                DispatcherQueue.TryEnqueue(() => Progress.Value = p));

            var stagedExe = await _installer.DownloadAsync(_available, token, progress);

            StatusText.Text = "Update downloaded. Restarting Volco…";
            AppLog.Info($"Update downloaded to {stagedExe}; handing off to installer.");

            UpdateInstaller.ApplyAndRestart(stagedExe);
            App.RequestExit();
        }
        catch (Exception ex)
        {
            AppLog.Error($"Update install failed: {ex}");
            StatusText.Text = $"Update failed: {ex.Message}";
            SetBusy(false);
        }
    }

    private void PersistPreferences()
    {
        var cfg = _configManager.Config.Updates;
        cfg.UseCiArtifact = UseCiCheckBox.IsChecked == true;
        cfg.GitHubToken = string.IsNullOrWhiteSpace(TokenBox.Password) ? null : TokenBox.Password;
        _configManager.RequestSave();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        CheckButton.IsEnabled = !busy;
        if (status != null) StatusText.Text = status;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        Progress.IsIndeterminate = busy;
        if (busy) UpdateButton.IsEnabled = false;
    }
}
