using System.Collections.Specialized;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Config;
using Volco.Core.Input;

namespace Volco.App.ViewModels;

/// <summary>
/// The Controls page (spec docs/design/controls-page.md): one drawing of the controller, its
/// sliders and buttons, and an inspector for the selected one. It owns the Sliders and Buttons
/// view models, which keep the lists and their edits, and adds the drawing's counts, the selection
/// (with touch-to-select from the controller) and the page's dialog.
/// </summary>
public sealed partial class ControlsViewModel : ObservableObject
{
    /// <summary>The smallest level change that counts as moving a slider (spec §4.5).</summary>
    private const float MoveThreshold = 0.02f;

    /// <summary>How long after a Hello slider reports are its sync, not a move.</summary>
    private static readonly TimeSpan HelloSyncWindow = TimeSpan.FromSeconds(1);

    private readonly ConfigManager _config;
    private readonly UndoToastViewModel _toast;
    private readonly TimeProvider _time;

    // The level each slider had when it last selected itself (or was first seen).
    private readonly Dictionary<int, float> _levelAtSelection = new();
    private DateTimeOffset? _helloAt;

    public SlidersViewModel Sliders { get; }
    public ButtonsViewModel Buttons { get; }

    /// <summary>The selected tile: a <see cref="SliderItemViewModel"/> or a <see cref="ButtonItemViewModel"/>.</summary>
    [ObservableProperty]
    private ObservableObject? _selected;

    /// <summary>More than 8 sliders or more than 8 buttons (spec §4.4).</summary>
    [ObservableProperty]
    private bool _isDense;

    /// <summary>No Hello yet: the drawing comes from the config, with indicators unknown.</summary>
    [ObservableProperty]
    private bool _showConnectLine;

    public string ConnectLine => "Connect your controller to see its lights and screens.";

    /// <summary>The open dialog's view model, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDialog))]
    private ObservableObject? _dialog;

    public bool HasDialog => Dialog is not null;

    /// <summary>The mode menu is open; like a dialog, it keeps the selection where it is.</summary>
    [ObservableProperty]
    private bool _isMenuOpen;

    /// <summary>The dialogs' collapsed sections for this app run, keyed "action:{section}" or
    /// "target:{section}".</summary>
    public Dictionary<string, bool> CollapsedSections { get; } = new();

    public ControlsViewModel(SlidersViewModel sliders, ButtonsViewModel buttons, ConfigManager config,
                             UndoToastViewModel toast, TimeProvider? time = null)
    {
        Sliders = sliders;
        Buttons = buttons;
        _config = config;
        _toast = toast;
        _time = time ?? TimeProvider.System;

        Sliders.AddMissing();
        Buttons.AddMissing();
        // Add, Remove last and their Undo change the lists: the counts and the selection follow.
        Sliders.Items.CollectionChanged += OnItemsChanged;
        Buttons.Items.CollectionChanged += OnItemsChanged;
        Buttons.Presets.CollectionChanged += (_, _) => CloseStaleDialog();
        RefreshCounts();
        FixSelection();
    }

    // ── Selection ─────────────────────────────────────────────────────────────

    public void Select(ObservableObject item) => Selected = item;

    // The tiles draw their own selection outline.
    partial void OnSelectedChanged(ObservableObject? oldValue, ObservableObject? newValue)
    {
        SetSelected(oldValue, false);
        SetSelected(newValue, true);
    }

    private static void SetSelected(ObservableObject? item, bool selected)
    {
        if (item is SliderItemViewModel slider) slider.IsSelected = selected;
        else if (item is ButtonItemViewModel button) button.IsSelected = selected;
    }

    /// <summary>Slider 1, else button 1, else nothing.</summary>
    private ObservableObject? DefaultSelection()
        => Sliders.Items.Count > 0 ? Sliders.Items[0]
           : Buttons.Items.Count > 0 ? Buttons.Items[0]
           : null;

    /// <summary>Moves the selection to the default when its control is no longer in the lists.
    /// A control that is only missing from the controller stays selected.</summary>
    private void FixSelection()
    {
        var present = Selected switch
        {
            SliderItemViewModel s => Sliders.Items.Contains(s),
            ButtonItemViewModel b => Buttons.Items.Contains(b),
            _ => false,
        };
        if (!present) Selected = DefaultSelection();
    }

    private bool SelectionLocked => Dialog is not null || IsMenuOpen;

    // ── Dialog ────────────────────────────────────────────────────────────────

    public void OpenDialog(ObservableObject dialog) => Dialog = dialog;

    [RelayCommand]
    public void CloseDialog() => Dialog = null;

    // A closed dialog stops following the page (device changes).
    partial void OnDialogChanged(ObservableObject? oldValue, ObservableObject? newValue)
        => (oldValue as IDisposable)?.Dispose();

    /// <summary>Opens the action dialog for one gesture of a button (spec §6.1).</summary>
    public void OpenActionDialog(ButtonItemViewModel button, PressType gesture)
        => OpenDialog(new ActionDialogViewModel(button, gesture, Buttons, CollapsedSections, CloseDialog));

    /// <summary>The action dialog's mode or button went away (a delete, an Undo): it closes
    /// without writing.</summary>
    private void CloseStaleDialog()
    {
        if (Dialog is ActionDialogViewModel action && !action.IsStillValid(_config.Config)) CloseDialog();
    }

    /// <summary>Opens the target dialog for a slider (spec §6.2).</summary>
    public void OpenTargetDialog(SliderItemViewModel slider)
        => OpenDialog(new PendingDialogRequest(slider, null));

    /// <summary>An inspector action row: the slot's button and gesture.</summary>
    [RelayCommand]
    private void OpenAction(ActionSlotViewModel slot)
    {
        var button = Buttons.Items.FirstOrDefault(b => b.Slots.Contains(slot));
        if (button is null) return;
        var gesture = slot == button.Press ? PressType.Single : slot == button.DoublePress ? PressType.Double : PressType.Long;
        OpenActionDialog(button, gesture);
    }

    [RelayCommand]
    private void OpenTarget(SliderItemViewModel slider) => OpenTargetDialog(slider);

    [RelayCommand]
    private void Clear(ButtonItemViewModel button) => ClearButton(button);

    // ── Controller events (UI thread) ─────────────────────────────────────────

    /// <summary>A button was pressed on the controller: it pulses, and is selected unless a dialog
    /// or the menu is open.</summary>
    public void OnButtonActivity(int index)
    {
        Buttons.Pulse(index);
        if (index < 0 || index >= Buttons.Items.Count || SelectionLocked) return;
        Selected = Buttons.Items[index];
    }

    /// <summary>A slider reported a level. Its fader follows; it is selected only when it really
    /// moved: by 2 % since it last selected itself, and not as the sync right after a Hello.</summary>
    public void OnSliderLevel(int index, float level)
    {
        Sliders.UpdateLiveLevel(index, level);
        if (index < 0 || index >= Sliders.Items.Count) return;

        var inSync = _helloAt is { } hello && _time.GetUtcNow() - hello < HelloSyncWindow;
        if (!_levelAtSelection.TryGetValue(index, out var last) || inSync)
        {
            _levelAtSelection[index] = level;
            return;
        }
        // A hair under 2 %, so that 0.50 → 0.52 counts despite float rounding.
        if (Math.Abs(level - last) < MoveThreshold - 1e-4f || SelectionLocked) return;
        _levelAtSelection[index] = level;
        Selected = Sliders.Items[index];
    }

    /// <summary>A Hello arrived (<see cref="AppConfig.Controller"/> is already updated).</summary>
    public void OnHello()
    {
        _helloAt = _time.GetUtcNow();
        Sliders.AddMissing();
        Buttons.AddMissing();
        RefreshCounts();
        FixSelection();
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RefreshCounts();
        FixSelection();
        CloseStaleDialog();
    }

    /// <summary>Marks the controls the last Hello doesn't have, and the dense layout.</summary>
    private void RefreshCounts()
    {
        var hello = _config.Config.Controller;
        ShowConnectLine = hello is null;
        for (int i = 0; i < Sliders.Items.Count; i++)
            Sliders.Items[i].IsMissing = hello is not null && i >= hello.Sliders;
        for (int i = 0; i < Buttons.Items.Count; i++)
            Buttons.Items[i].IsMissing = hello is not null && i >= hello.Buttons.Count;
        IsDense = Sliders.Items.Count > 8 || Buttons.Items.Count > 8;
    }

    // ── Clear ─────────────────────────────────────────────────────────────────

    /// <summary>Clears the button's actions in the edited preset only, with Undo.</summary>
    public void ClearButton(ButtonItemViewModel item)
    {
        var presetId = Buttons.EditingPresetId;
        item.Config.Actions.TryGetValue(presetId, out var before);
        item.Config.Actions[presetId] = new ButtonBindingConfig();
        item.Reload();
        _config.RequestSave();
        _toast.Show($"Cleared button {item.Index + 1} in {Buttons.EditingName}", () =>
        {
            if (before is null) item.Config.Actions.Remove(presetId);
            else item.Config.Actions[presetId] = before;
            item.Reload();
            _config.RequestSave();
        });
    }
}

/// <summary>A dialog the Controls page asked for: the control and, for a button, the gesture.
/// A placeholder until the target dialog replaces it.</summary>
public sealed class PendingDialogRequest(ObservableObject control, PressType? gesture) : ObservableObject
{
    public ObservableObject Control { get; } = control;
    public PressType? Gesture { get; } = gesture;
}
