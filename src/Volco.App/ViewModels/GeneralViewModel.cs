using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.App.Platform;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;

namespace Volco.App.ViewModels;

/// <summary>
/// The General page: start at sign-in (where the platform has it), updates (release builds only)
/// and troubleshooting (the log and config file locations). Device names and icons are edited from
/// the Controller page's dialogs; the version and GitHub link are <see cref="AboutViewModel"/>.
/// The update flow itself lives in <see cref="Controls.UpdatesPanel"/>.
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

    public string LogPath => AppLog.LogPath;

    /// <summary>Where the settings are saved (config.json).</summary>
    public string ConfigPath => _config.ConfigPath;

    partial void OnDebugLoggingChanged(bool value)
    {
        _config.Config.DebugLogging = value;
        AppLog.IsDebugEnabled = value;
        _config.RequestSave();
    }

    /// <summary>Opens the folder holding volco.log in the system file browser. Best effort.</summary>
    [RelayCommand]
    private void OpenLogFolder() => OpenFolderOf(AppLog.LogPath, "log");

    /// <summary>Opens the folder holding config.json in the system file browser. Best effort.</summary>
    [RelayCommand]
    private void OpenConfigFolder() => OpenFolderOf(_config.ConfigPath, "config");

    private static void OpenFolderOf(string file, string what)
    {
        try
        {
            var dir = Path.GetDirectoryName(file);
            if (string.IsNullOrEmpty(dir)) return;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Error($"Could not open the {what} folder: {ex.Message}");
        }
    }
}
