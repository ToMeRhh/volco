using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>One card of the setup chooser: a title, the counts or a sentence, a small picture of
/// its controls (none for Start empty), and what picking it does.</summary>
public sealed partial class ChooserCard : ObservableObject
{
    public ChooserCard(string title, string summary, string description, ControllerConfig? picture, bool isPrimary, Action pick)
    {
        Title = title;
        Summary = summary;
        Description = description;
        Picture = picture;
        IsPrimary = isPrimary;
        PickCommand = new RelayCommand(pick);
    }

    public string Title { get; }

    /// <summary>"5 sliders, 4 buttons, 1 screen"; empty for Start empty.</summary>
    public string Summary { get; }

    /// <summary>A sentence under the title (Start empty's); empty for the others.</summary>
    public string Description { get; }

    public bool HasSummary => Summary.Length > 0;
    public bool HasDescription => Description.Length > 0;

    /// <summary>The config drawn on the card; null draws the dashed empty card.</summary>
    public ControllerConfig? Picture { get; }

    /// <summary>Volco's saved copy: first and outlined.</summary>
    public bool IsPrimary { get; }

    /// <summary>Start empty: a dashed card.</summary>
    public bool IsDashed => Picture is null;

    public ICommand PickCommand { get; }

    /// <summary>False while picking the card can't work (the saved copy needs a ready controller).</summary>
    [ObservableProperty] private bool _isEnabled = true;

    /// <summary>Why the card is disabled; null while it is enabled.</summary>
    [ObservableProperty] private string? _toolTip;
}

/// <summary>
/// The setup chooser (hardware-page spec §7.1), in the drawing's place when the controller isn't
/// set up, its setup can't be used, or the user chose Start over: Volco's saved copy (with a
/// mirror), each available preset, then Start empty. Volco's saved copy is sent to the controller at
/// once (controller-page spec §4.4); a preset or Start empty starts a new draft, sent by Save. After Start over, Back (or Escape) returns to the drawing.
/// </summary>
public sealed partial class SetupChooserViewModel : ObservableObject
{
    private readonly ControllerViewModel _controller;
    private readonly Action _picked;
    private byte[]? _shownFor;

    /// <param name="picked">A card was picked.</param>
    /// <param name="back">Back: the Start over is cancelled.</param>
    public SetupChooserViewModel(ControllerViewModel controller, Action picked, Action back)
    {
        _controller = controller;
        _picked = picked;
        BackCommand = new RelayCommand(back);
        Refresh();
    }

    /// <summary>The chooser shows for Start over: Back is offered.</summary>
    [ObservableProperty] private bool _canGoBack;

    /// <summary>Back: the drawing comes back, the draft unchanged.</summary>
    public ICommand BackCommand { get; }

    public string Title => "How do you want to start?";

    public string Subtitle => "Presets and Start empty are sent only when you Save.";

    public ObservableCollection<ChooserCard> Cards { get; } = new();

    /// <summary>Rebuilds the cards when Volco's saved copy changed since they were made.</summary>
    public void Refresh()
    {
        var mirror = _controller.DecodeMirror();
        var key = mirror?.ToByteArray() ?? Array.Empty<byte>();
        if (_saved is not null) SetSavedAvailability();
        if (_shownFor is not null && _shownFor.AsSpan().SequenceEqual(key)) return;
        _shownFor = key;

        Cards.Clear();
        _saved = null;
        if (mirror is not null)
        {
            _saved = new ChooserCard("Volco's saved copy", ConfigDiff.Counts(mirror), "", mirror, true,
                                     () => Pick(() => _controller.SetupApplySavedCommand.Execute(null)));
            SetSavedAvailability();
            Cards.Add(_saved);
        }
        foreach (var preset in _controller.SetupPresets)
        {
            var hardware = ControllerPresets.ApplyTo(new ControllerConfig(), preset);
            Cards.Add(new ChooserCard(preset.DisplayName, ConfigDiff.Counts(hardware), "", hardware, false,
                                      () => Pick(() => _controller.StartFromPresetCommand.Execute(preset))));
        }
        Cards.Add(new ChooserCard("Start empty", "", "Add sliders and buttons yourself", null, false,
                                  () => Pick(() => _controller.StartFromScratchCommand.Execute(null))));
    }

    private ChooserCard? _saved;

    /// <summary>The saved copy is sent at once, so it needs a controller that answers.</summary>
    private void SetSavedAvailability()
    {
        var ok = _controller.CanStartNewDraft;
        _saved!.IsEnabled = ok;
        _saved.ToolTip = ok ? null : "Connect the controller first";
    }

    private void Pick(Action start)
    {
        start();
        _picked();
    }
}
