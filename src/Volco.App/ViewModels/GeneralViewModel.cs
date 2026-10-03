using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Diagnostics;
using Volco.Core.Updates;

namespace Volco.App.ViewModels;

/// <summary>
/// The General page: updates (release builds only, as before), troubleshooting and the app's
/// version. The update
/// flow itself lives in <see cref="Controls.UpdatesPanel"/>, which needs the config it saves to.
/// </summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly ConfigManager _config;

    [ObservableProperty]
    private bool _debugLogging;

    public GeneralViewModel(ConfigManager config, bool showUpdates)
    {
        _config = config;
        _debugLogging = config.Config.DebugLogging;
        ShowUpdates = showUpdates;
    }

    public GeneralViewModel(ConfigManager config) : this(config, BuildInfo.IsRelease) { }

    /// <summary>The in-app updater only makes sense for official release builds.</summary>
    public bool ShowUpdates { get; }

    internal ConfigManager ConfigManager => _config;

    /// <summary>"Volco v1.2.3" on releases, "Volco dev a1b2c3d" on other builds.</summary>
    public string VersionText => $"Volco {BuildInfo.DisplayVersion}";

    /// <summary>The commit and build time, or null when neither was stamped.</summary>
    public string? BuildDetails => BuildInfo.BuildDetails;

    public string LogPath => AppLog.LogPath;

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
