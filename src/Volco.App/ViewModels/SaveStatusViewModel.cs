using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;

namespace Volco.App.ViewModels;

/// <summary>The sidebar line above the connection state: shown while changes wait to be written,
/// and, differently, while saving fails (<see cref="SaveText"/>).</summary>
public sealed partial class SaveStatusViewModel : ObservableObject, IDisposable
{
    private readonly ConfigManager _config;
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible), nameof(Text), nameof(Tone), nameof(ToolTip), nameof(CanRetry))]
    private ConfigSaveState _state;

    private string? _error;

    public bool IsVisible => State != ConfigSaveState.Saved;
    public string Text => SaveText.Footer(State);
    public LinkTone Tone => SaveText.Tone(State);
    public string ToolTip => SaveText.ToolTip(State, _error);
    public bool CanRetry => State == ConfigSaveState.Failed;

    public SaveStatusViewModel(ConfigManager config, IUiDispatcher ui)
    {
        _config = config;
        _ui = ui;
        _config.SaveStateChanged += OnSaveStateChanged;
        Refresh();
    }

    [RelayCommand]
    private void Retry() => _config.RetrySave();

    private void OnSaveStateChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed) Refresh();
    });

    private void Refresh()
    {
        _error = _config.SaveError;
        State = _config.SaveState;
        OnPropertyChanged(nameof(ToolTip));   // the reason can change while the state stays Failed
    }

    public void Dispose()
    {
        _disposed = true;
        _config.SaveStateChanged -= OnSaveStateChanged;
    }
}
