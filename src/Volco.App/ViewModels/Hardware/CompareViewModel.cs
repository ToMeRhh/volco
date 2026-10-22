using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// Compare (hardware-page spec §7.2): the controller's setup beside Volco's copy, each with a small
/// picture and only the lines that differ (<see cref="ConfigDiff"/>). Opening it reads the
/// controller's setup without adopting it; Keep reads it again and adopts it as Volco's copy, Send
/// is Apply mine. Results are posted to the UI thread.
/// </summary>
public sealed partial class CompareViewModel : ObservableObject, IDisposable
{
    private readonly ControllerViewModel _controller;
    private readonly ControllerConfigService _service;
    private readonly IUiDispatcher _ui;
    private readonly Action _close;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public CompareViewModel(ControllerViewModel controller, ControllerConfigService service, IUiDispatcher ui, Action close)
    {
        _controller = controller;
        _service = service;
        _ui = ui;
        _close = close;
        VolcoPicture = controller.DecodeMirror();
    }

    public string Title => "The controller has a different setup";

    /// <summary>The controller's setup, once read.</summary>
    [ObservableProperty] private ControllerConfig? _controllerPicture;

    /// <summary>Volco's copy (the mirror).</summary>
    public ControllerConfig? VolcoPicture { get; }

    /// <summary>The two pictures: the controller's, then Volco's.</summary>
    public IReadOnlyList<ControllerConfig?> Pictures => new[] { ControllerPicture, VolcoPicture };

    /// <summary>What only the controller's setup has.</summary>
    [ObservableProperty] private IReadOnlyList<string> _controllerLines = Array.Empty<string>();

    /// <summary>What only Volco's copy has.</summary>
    [ObservableProperty] private IReadOnlyList<string> _volcoLines = Array.Empty<string>();

    /// <summary>A read (Compare's or Keep's) is running.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(KeepCommand), nameof(SendCommand), nameof(RetryCommand))]
    [NotifyPropertyChangedFor(nameof(IsReading))]
    private bool _isBusy;

    /// <summary>The first read runs: "Reading the controller's setup…".</summary>
    public bool IsReading => IsBusy && !HasComparison;

    /// <summary>"Couldn't read the controller's setup", or null.</summary>
    [ObservableProperty] private string? _error;

    public bool HasError => Error is not null;

    /// <summary>The controller's setup was read: the columns show it.</summary>
    public bool HasComparison => ControllerPicture is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnControllerPictureChanged(ControllerConfig? value)
    {
        OnPropertyChanged(nameof(HasComparison));
        OnPropertyChanged(nameof(Pictures));
        OnPropertyChanged(nameof(IsReading));
    }

    /// <summary>Reads the controller's setup to compare (the user's click on Compare starts it).</summary>
    public void Start() => _ = Read();

    private bool CanAct => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Retry() => _ = Read();

    /// <summary>Keep the controller's setup: read it and adopt it as Volco's copy. The controller
    /// is untouched.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private async Task Keep()
    {
        IsBusy = true;
        Error = null;
        var r = await _service.ReadAsync(adopt: true, _cts.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            IsBusy = false;
            if (r.Status == ReadStatus.Ok) _close();
            else Error = "Couldn't read the controller's setup";
        });
    }

    /// <summary>Send Volco's copy: today's Apply mine. The controller restarts with it.</summary>
    [RelayCommand(CanExecute = nameof(CanAct))]
    private void Send()
    {
        _controller.ApplyMineCommand.Execute(null);
        _close();
    }

    private async Task Read()
    {
        if (_disposed) return;
        IsBusy = true;
        Error = null;
        var r = await _service.ReadAsync(adopt: false, _cts.Token).ConfigureAwait(false);
        _ui.Post(() =>
        {
            if (_disposed) return;
            IsBusy = false;
            if (r.Status != ReadStatus.Ok || Decode(r.Blob) is not { } theirs)
            {
                Error = "Couldn't read the controller's setup";
                return;
            }
            var mine = VolcoPicture ?? new ControllerConfig();
            ControllerLines = ConfigDiff.Lines(mine, theirs, forTheirs: true);
            VolcoLines = ConfigDiff.Lines(mine, theirs, forTheirs: false);
            ControllerPicture = theirs;
        });
    }

    private static ControllerConfig? Decode(byte[]? blob)
    {
        if (blob is null) return null;
        if (blob.Length == 0) return new ControllerConfig();
        try { return ControllerConfigCodec.Decode(blob); }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();   // a read still running ends; its result is dropped
    }
}
