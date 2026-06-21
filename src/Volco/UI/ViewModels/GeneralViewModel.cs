using CommunityToolkit.Mvvm.ComponentModel;
using Volco.Config;

namespace Volco.UI.ViewModels;

/// <summary>
/// ViewModel for the General settings tab.
/// Currently exposes debug-logging toggle; additional general settings can be
/// added here in future tasks.
/// </summary>
public partial class GeneralViewModel : ObservableObject
{
    private readonly ConfigManager _config;

    [ObservableProperty]
    private bool _debugLogging;

    public GeneralViewModel(ConfigManager config)
    {
        _config = config;
        _debugLogging = config.Config.DebugLogging;
    }

    partial void OnDebugLoggingChanged(bool value)
    {
        _config.Config.DebugLogging = value;
        AppLog.IsDebugEnabled = value;
        Persist();
    }

    private void Persist() => _config.RequestSave();
}
