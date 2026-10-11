using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.Platform;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;

namespace Volco.App.ViewModels;

/// <summary>
/// The General page: start at sign-in (where the platform has it), updates (release builds only),
/// troubleshooting, the app's version and a link to the project on GitHub. The device section is
/// <see cref="DevicesViewModel"/>. The update flow itself lives in <see cref="Controls.UpdatesPanel"/>.
/// </summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly ConfigManager _config;
    private readonly IStartWithSystem? _startWithSystem;
    private bool _startAtSignIn;

    [ObservableProperty]
    private bool _debugLogging;

    /// <summary>Why the last change to start at sign-in failed; null when it didn't.</summary>
    [ObservableProperty]
    private string? _startAtSignInError;

    public GeneralViewModel(ConfigManager config, bool isRelease, IStartWithSystem? startWithSystem = null)
    {
        _config = config;
        _startWithSystem = startWithSystem;
        _debugLogging = config.Config.DebugLogging;
        _startAtSignIn = ReadStartAtSignIn();
        ShowUpdates = isRelease;
        VersionText = $"Volco {BuildInfo.FormatVersion(isRelease, BuildInfo.Version, BuildInfo.Commit)}";
        BuildDetails = isRelease ? null : BuildInfo.BuildDetails;
    }

    public GeneralViewModel(ConfigManager config, IStartWithSystem? startWithSystem = null)
        : this(config, BuildInfo.IsRelease, startWithSystem) { }

    /// <summary>The toggle shows only where the platform can start Volco at sign-in.</summary>
    public bool ShowStartAtSignIn => _startWithSystem is not null;

    /// <summary>
    /// Whether the OS starts Volco (in the tray) when the user signs in. Read from and written to the
    /// OS, which keeps the only record; after a change it shows what the OS now says, so a refused
    /// change flips the toggle back.
    /// </summary>
    public bool StartAtSignIn
    {
        get => _startAtSignIn;
        set
        {
            if (_startWithSystem is null || value == _startAtSignIn) return;
            try
            {
                _startWithSystem.SetEnabled(value);
                StartAtSignInError = null;
                AppLog.Info($"Start at sign-in turned {(value ? "on" : "off")}");
            }
            catch (Exception ex)
            {
                AppLog.Error($"Could not turn start at sign-in {(value ? "on" : "off")}: {ex.Message}");
                StartAtSignInError = $"Couldn't change this: {ex.Message}";
            }
            _startAtSignIn = ReadStartAtSignIn();
            OnPropertyChanged();
        }
    }

    /// <summary>Re-reads the OS setting (the page calls it when shown: it may have changed in the
    /// OS's own startup settings).</summary>
    public void RefreshStartAtSignIn()
    {
        var now = ReadStartAtSignIn();
        if (now == _startAtSignIn) return;
        _startAtSignIn = now;
        OnPropertyChanged(nameof(StartAtSignIn));
    }

    private bool ReadStartAtSignIn()
    {
        if (_startWithSystem is null) return false;
        try { return _startWithSystem.IsEnabled; }
        catch (Exception ex)
        {
            AppLog.Error($"Could not read start at sign-in: {ex.Message}");
            return false;
        }
    }

    /// <summary>The in-app updater only makes sense for official release builds.</summary>
    public bool ShowUpdates { get; }

    /// <summary>"Volco v1.2.3" on releases, "Volco dev a1b2c3d" on other builds.</summary>
    public string VersionText { get; }

    /// <summary>
    /// Branch, commit, build time and origin, one per line, on non-release builds; null on
    /// releases, which show just the version.
    /// </summary>
    public string? BuildDetails { get; }

    public string LogPath => AppLog.LogPath;

    /// <summary>The project's GitHub page, linked from About.</summary>
    public Uri RepoUri { get; } = new(BuildInfo.RepoUrl);

    partial void OnDebugLoggingChanged(bool value)
    {
        _config.Config.DebugLogging = value;
        AppLog.IsDebugEnabled = value;
        _config.RequestSave();
    }

    /// <summary>Opens the folder holding volco.log in the system file browser. Best effort.</summary>
    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var dir = Path.GetDirectoryName(AppLog.LogPath);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not open the log folder: {ex.Message}");
        }
    }
}
