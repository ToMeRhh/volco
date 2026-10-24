using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// The Controller page's drawing (spec docs/design/hardware-page.md §5): the draft's controls in
/// draft order, each the Controls item its <see cref="ControlLayout"/> source names (so a slider
/// that moved down keeps its target) or a detached item for an added one, with its wiring captions
/// (<see cref="TileWiring"/>) and the live raw input. Every Controls item has a wiring from the start,
/// so tiles keep their size when the stage switches to Hardware. UI thread; runtime events are posted.
/// </summary>
public sealed partial class HardwareViewModel : ObservableObject, IDisposable
{
    private const double MaxRaw = 4095.0;

    private readonly ControllerViewModel _controller;
    private readonly ControlsViewModel _controls;
    private readonly VolcoRuntime _runtime;
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    public HardwareViewModel(ControllerViewModel controller, ControlsViewModel controls, VolcoRuntime runtime, IUiDispatcher ui)
    {
        _controller = controller;
        _controls = controls;
        _runtime = runtime;
        _ui = ui;
        _controller.DraftChanged += OnDraftChanged;
        _controller.PropertyChanged += OnControllerPropertyChanged;
        _controller.PageLeft += OnStartOverEnded;
        _controller.Reverted += OnStartOverEnded;
        _controls.Sliders.Items.CollectionChanged += OnItemsChanged;
        _controls.Buttons.Items.CollectionChanged += OnItemsChanged;
        _controls.PropertyChanged += OnControlsPropertyChanged;
        _runtime.RawInput += OnRawInput;
        _runtime.CalibrationChanged += OnCalibrationChanged;
        _runtime.PinActivity += OnPinActivity;
        Status = new ControllerStatusViewModel(controller, this);
        SaveCommand = new RelayCommand(() => _controller.SaveCommand.Execute(null), () => _controller.CanSave && _controller.Problems.Count == 0);
        Chooser = new SetupChooserViewModel(controller, OnChooserPicked, CancelStartOver);
        Refresh();
    }

    /// <summary>The controller status: the sidebar line and the page banner (controller-page spec §5).</summary>
    public ControllerStatusViewModel Status { get; }

    // ── What the status opens (spec §4.2, §7, §10); the views attach in later steps ──────────────

    /// <summary>Compare (spec §7.2) is open.</summary>
    [ObservableProperty] private bool _isCompareOpen;

    /// <summary>The Connection dialog (spec §10) is open.</summary>
    [ObservableProperty] private bool _isConnectionDialogOpen;

    /// <summary>The Connection dialog opens on its Wi-Fi tab, else on USB.</summary>
    [ObservableProperty] private bool _connectionDialogOnWifi;

    /// <summary>The open Connection dialog's view model, or null.</summary>
    [ObservableProperty] private ConnectionDialogViewModel? _connectionDialog;

    /// <summary>The setup chooser (spec §7.1), shown in the drawing's place by <see cref="ShowChooser"/>.</summary>
    public SetupChooserViewModel Chooser { get; }

    /// <summary>The open Compare dialog's view model, or null.</summary>
    [ObservableProperty] private CompareViewModel? _compare;

    /// <summary>The user chose Start over and hasn't picked a card yet.</summary>
    private bool _startingOver;

    private bool _showChooser;

    /// <summary>The setup chooser (spec §7.1) fills the case: the controller isn't set up or its
    /// setup can't be used and no new draft was started, or the user chose Start over.</summary>
    public bool ShowChooser
    {
        get => _showChooser;
        private set
        {
            if (_showChooser == value) return;
            _showChooser = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShowSaveBar));
        }
    }

    private void UpdateChooser()
    {
        var needsSetup = _controller.Banner.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror or BannerKind.Unusable;
        ShowChooser = _startingOver || (needsSetup && !_controller.HasNewDraft);
        Chooser.CanGoBack = _startingOver;
        if (ShowChooser) Chooser.Refresh();
    }

    private void OnChooserPicked()
    {
        _startingOver = false;
        UpdateChooser();
    }

    /// <summary>The user chose Start over and hasn't picked a card: Escape and the chooser's Back cancel it.</summary>
    public bool IsStartingOver => _startingOver;

    /// <summary>Escape or the chooser's Back: the drawing comes back with the draft as it was.</summary>
    public void CancelStartOver()
    {
        if (_disposed || !_startingOver) return;
        _startingOver = false;
        UpdateChooser();
    }

    // Discard and leaving Hardware end a Start over that wasn't picked.
    private void OnStartOverEnded(object? sender, EventArgs e) => CancelStartOver();

    /// <summary>The banner's Compare…: the controller's setup beside Volco's (spec §7.2); opening it
    /// reads the controller's setup.</summary>
    public void OpenCompare()
    {
        if (_disposed) return;
        Compare?.Dispose();
        Compare = new CompareViewModel(_controller, _runtime.ControllerConfig, _ui, CloseCompare);
        IsCompareOpen = true;
        Compare.Start();
    }

    /// <summary>Closes Compare (Escape, a click outside, or after Keep or Send).</summary>
    public void CloseCompare()
    {
        IsCompareOpen = false;
        Compare?.Dispose();
        Compare = null;
    }

    /// <summary>The status menu's Connection…: the Connection dialog, on the Wi-Fi tab
    /// when <paramref name="wifi"/>.</summary>
    public void OpenConnection(bool wifi)
    {
        if (_disposed) return;
        ConnectionDialogOnWifi = wifi;
        ConnectionDialog?.Dispose();
        ConnectionDialog = new ConnectionDialogViewModel(_controller, wifi, CloseConnection);
        IsConnectionDialogOpen = true;
    }

    /// <summary>Closes the Connection dialog (Done, Escape, or a click outside).</summary>
    public void CloseConnection()
    {
        IsConnectionDialogOpen = false;
        var dialog = ConnectionDialog;
        ConnectionDialog = null;
        dialog?.Dispose();
    }

    // ── Re-calibrate (slider-calibration spec, "The sweep") ───────────────────────────────────

    /// <summary>The open sweep dialog's view model, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCalibrationOpen))]
    private CalibrationDialogViewModel? _calibration;

    /// <summary>The sweep dialog is open.</summary>
    public bool IsCalibrationOpen => Calibration is not null;

    /// <summary>A slider's Re-calibrate: the sweep dialog for draft slider <paramref name="sliderIndex"/>
    /// (0-based, as drawn), which runs on its hardware input. Nothing happens while Re-calibrate
    /// is disabled for it.</summary>
    public void OpenCalibration(int sliderIndex)
    {
        if (_disposed || Calibration is not null || !CanRecalibrate(sliderIndex, out _)) return;
        if (Source(_controller.Layout.Sliders, sliderIndex) is not int input) return;
        CalibrationDialogViewModel? dialog = null;
        dialog = new CalibrationDialogViewModel(input, _runtime, _ui, () => { if (ReferenceEquals(Calibration, dialog)) CloseCalibration(); },
                                                number: sliderIndex + 1,
                                                reversed: Saved(_controller.BaselineConfig().Sliders, input)?.Reverse == true);
        Calibration = dialog;
    }

    /// <summary>Closes the sweep dialog; one whose sweep hadn't ended cancels it.</summary>
    public void CloseCalibration()
    {
        var dialog = Calibration;
        Calibration = null;
        dialog?.Dispose();
    }

    /// <summary>Re-calibrate is enabled for draft slider <paramref name="index"/> (spec, "The sweep"): the
    /// controller is ready and runs Volco's saved setup, the slider is saved and unchanged, and no
    /// test, save or firmware operation runs. <paramref name="why"/>: the button's tooltip when the
    /// slider's unsaved changes or a controller that holds another setup hold it off.</summary>
    private bool CanRecalibrate(int index, out string? why)
    {
        why = null;
        if (index < 0 || index >= Sliders.Count) return false;
        var tile = Sliders[index];
        if (tile.IsNew) return false;
        if (tile.Wiring is { HasChange: true })
        {
            why = "Save to controller first";
            return false;
        }
        if (_controller.IsControllerReady && !_controller.InSync)
        {
            why = "Volco's setup differs from the controller's";
            return false;
        }
        return Source(_controller.Layout.Sliders, index) is not null
               && _controller.IsControllerReady && _controller.CanEdit && !_controller.Busy;
    }

    /// <summary>The inspected slider's calibration rows: the controller's last report for its input
    /// (none while it isn't ready) and whether Re-calibrate is enabled.</summary>
    private void UpdateCalibrationRows()
    {
        if (_disposed || Inspected is not SliderWiringViewModel s || s.Index >= Sliders.Count) return;
        var source = Source(_controller.Layout.Sliders, s.Index);
        var saved = Saved(_controller.BaselineConfig().Sliders, source);
        var state = source is int input && _controller.IsControllerReady ? _runtime.GetCalibration(input) : null;
        var can = CanRecalibrate(s.Index, out var why);
        s.UpdateCalibration(Sliders[s.Index].IsNew ? null : saved, state, can, why);
    }

    private void OnCalibrationChanged(object? sender, (int Slider, SliderCalibrationState? State) e) => _ui.Post(UpdateCalibrationRows);

    // ── Leaving with unsaved changes (spec §3.3) ─────────────────────────────────────────────

    /// <summary>The draft isn't on the controller yet.</summary>
    public bool IsDirty => _controller.IsDirty;

    /// <summary>The draft holds the user's unsaved changes (<see cref="ControllerViewModel.HasUnsavedChanges"/>):
    /// leaving the Controller page asks first, and the drawing shows the draft's controls.</summary>
    public bool HasUnsavedChanges => _controller.HasUnsavedChanges;

    /// <summary>The open leave prompt, or null.</summary>
    [ObservableProperty] private LeavePromptViewModel? _leavePrompt;

    /// <summary>Asks before leaving Hardware with unsaved changes; <paramref name="proceed"/> goes
    /// on once the draft was saved (and confirmed) or discarded. While a prompt is open it stays
    /// (a save it runs goes on) and only where it continues to changes.</summary>
    public void AskToLeave(Action proceed)
    {
        if (_disposed) return;
        if (LeavePrompt is { } open) open.Retarget(proceed);
        else LeavePrompt = new LeavePromptViewModel(_controller, proceed, CloseLeavePrompt);
    }

    /// <summary>Closes the leave prompt: the user stays on Hardware.</summary>
    public void CloseLeavePrompt()
    {
        var prompt = LeavePrompt;
        LeavePrompt = null;
        prompt?.Dispose();
    }

    // ── Commands of the title row (spec §3.2) ────────────────────────────────────────────────

    /// <summary>+ Add › Slider.</summary>
    public IRelayCommand AddSliderCommand => _controller.AddSliderCommand;

    /// <summary>+ Add › Button.</summary>
    public IRelayCommand AddButtonCommand => _controller.AddButtonCommand;

    /// <summary>⋯ Start over…: the drawing returns to the setup chooser; the draft stays until a
    /// choice is made.</summary>
    public void StartOver()
    {
        if (_disposed || !_controller.CanStartNewDraft) return;
        _startingOver = true;
        UpdateChooser();
    }

    /// <summary>The draft's sliders, in draft order.</summary>
    public ObservableCollection<SliderItemViewModel> Sliders { get; } = new();

    /// <summary>The draft's buttons, in draft order.</summary>
    public ObservableCollection<ButtonItemViewModel> Buttons { get; } = new();

    /// <summary>The draft can't be edited (spec §5.4): the drawing is greyed and the inspector's
    /// fields are disabled.</summary>
    public bool IsReadOnly => !_controller.CanEdit;

    /// <summary>
    /// What the inspector shows (spec §5.3, §6): the <see cref="SliderWiringViewModel"/> or
    /// <see cref="ButtonWiringViewModel"/> of the tile selected (the selection is Controls', so it
    /// carries over), or <see cref="ControllerSettingsViewModel"/>; null when the selection is not in
    /// the drawing.
    /// </summary>
    [ObservableProperty] private object? _inspected;

    /// <summary>The question asked inline ("Remove Slider 3?"), or null.</summary>
    public Confirmation? Confirmation => _controller.Confirmation;

    // ── Building the drawing ─────────────────────────────────────────────────────────────────

    private void OnDraftChanged(object? sender, EventArgs e) => Refresh();

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void OnControllerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Try the lights and Find screens read the controller's readiness and test, which raise no
        // event of their own.
        if (Inspected is ButtonWiringViewModel button) button.TryLightsCommand.NotifyCanExecuteChanged();
        if (Buses is { } buses)
            foreach (var bus in buses.Buses) bus.FindScreensCommand.NotifyCanExecuteChanged();
        if (e.PropertyName is nameof(ControllerViewModel.CanEdit) or null or "")
        {
            OnPropertyChanged(nameof(IsReadOnly));
            foreach (var field in _pinFields.Values) field.IsEditable = _controller.CanEdit;
            RefreshInspected();
        }
        if (e.PropertyName is nameof(ControllerViewModel.Confirmation) or null or "") OnPropertyChanged(nameof(Confirmation));
        if (e.PropertyName == nameof(ControllerViewModel.Layout)) Refresh();
        if (e.PropertyName is nameof(ControllerViewModel.IsTesting) or null or "") OnTestingChanged();
        if (e.PropertyName is nameof(ControllerViewModel.CanStartTest) or null or "") OnPropertyChanged(nameof(CanStartTest));
        if (e.PropertyName is nameof(ControllerViewModel.IsDirty) or nameof(ControllerViewModel.HasUnsavedChanges)
            or nameof(ControllerViewModel.IsSaving)
            or nameof(ControllerViewModel.IsSettingUp) or nameof(ControllerViewModel.CanSave)
            or nameof(ControllerViewModel.Notice) or null or "") UpdateSaveBar();
        if (e.PropertyName is nameof(ControllerViewModel.IsDirty) or null or "") OnPropertyChanged(nameof(IsDirty));
        if (e.PropertyName is nameof(ControllerViewModel.HasUnsavedChanges) or null or "") OnPropertyChanged(nameof(HasUnsavedChanges));
        UpdateChooser();
        UpdateCalibrationRows();
    }

    // ── The save bar (spec §6.5) ─────────────────────────────────────────────────────────────

    /// <summary>The bar at the bottom of the drawing: shown while the draft holds the user's unsaved
    /// changes (not the Blank placeholder), no save runs and the setup chooser isn't in the
    /// drawing's place.</summary>
    public bool ShowSaveBar => _controller.HasUnsavedChanges && !_controller.IsSaving && !ShowChooser;

    /// <summary>"1 change not saved", "N changes not saved"; for a new draft, that the setup isn't on the
    /// controller; and after the controller's setup changed under the draft, that Discard loads it.</summary>
    public string SaveBarText
    {
        get
        {
            if (_controller.Notice is { } notice) return notice;
            if (_controller.IsSettingUp) return "This setup isn't on the controller yet";
            var n = Math.Max(1, CountChanges());
            return n == 1 ? "1 change not saved" : $"{n} changes not saved";
        }
    }

    /// <summary>The note beside the count, while Save sends the controller's config; null when only
    /// the button timing (Volco's own settings) changed.</summary>
    public string? SaveBarNote => _controller.ControllerDirty ? "Saving restarts the controller" : null;

    /// <summary>Discard: the draft goes back to Volco's copy.</summary>
    public ICommand DiscardCommand => _controller.RevertCommand;

    /// <summary>Save to controller; disabled while the draft has problems or can't be saved.</summary>
    public IRelayCommand SaveCommand { get; }

    /// <summary>Changed tiles, removed and added controls, changed buses and changed settings sections.</summary>
    private int CountChanges()
    {
        var layout = _controller.Layout;
        var baseline = _controller.BaselineConfig();
        var draft = _controller.Draft;
        int n = 0;
        n += Sliders.Where((t, i) => Source(layout.Sliders, i) is not null && t.Wiring is { HasChange: true }).Count();
        n += Buttons.Where((t, i) => Source(layout.Buttons, i) is not null && t.Wiring is { HasChange: true }).Count();
        n += layout.Sliders.Count(s => s is null) + Math.Max(0, baseline.Sliders.Count - layout.Sliders.Count(s => s is not null));
        n += layout.Buttons.Count(s => s is null) + Math.Max(0, baseline.Buttons.Count - layout.Buttons.Count(s => s is not null));
        for (int bus = 0; bus <= 3; bus++)
            if (!Equals(ControllerDraft.Bus(draft, bus), ControllerDraft.Bus(baseline, bus))) n++;
        if (!Equals(draft.Screens, baseline.Screens)) n++;
        // Controller settings' Sliders and Buttons cards (the button timing is Volco's, in the draft too).
        if (InputDiffers(draft.Input, baseline.Input, i => i.HasFilterWeightPct, i => i.FilterWeightPct)
            || InputDiffers(draft.Input, baseline.Input, i => i.HasSliderDeadband, i => i.SliderDeadband)
            || InputDiffers(draft.Input, baseline.Input, i => i.HasSliderIntervalMs, i => i.SliderIntervalMs)) n++;
        if (InputDiffers(draft.Input, baseline.Input, i => i.HasDebounceMs, i => i.DebounceMs) || _controller.TimingDirty) n++;
        if (!Equals(draft.Udp, baseline.Udp)) n++;
        return n;
    }

    /// <summary>One field of the input tuning differs, its presence included (an absent message has
    /// none of its fields).</summary>
    private static bool InputDiffers(InputTuning? a, InputTuning? b, Func<InputTuning, bool> has, Func<InputTuning, uint> value)
    {
        bool hasA = a is not null && has(a), hasB = b is not null && has(b);
        return hasA != hasB || (hasA && value(a!) != value(b!));
    }

    private void UpdateSaveBar()
    {
        if (_disposed) return;
        OnPropertyChanged(nameof(ShowSaveBar));
        OnPropertyChanged(nameof(SaveBarText));
        OnPropertyChanged(nameof(SaveBarNote));
        SaveCommand.NotifyCanExecuteChanged();
    }

    private void Refresh()
    {
        if (_disposed) return;
        var draft = _controller.Draft;
        var layout = _controller.Layout;
        var baseline = _controller.BaselineConfig();
        var problems = _controller.Problems;

        Sync(Sliders, _controls.Sliders.Items, draft.Sliders.Count, layout.Sliders, _controls.Sliders.CreateDetached);
        Sync(Buttons, _controls.Buttons.Items, draft.Buttons.Count, layout.Buttons, _controls.Buttons.CreateDetached);

        for (int i = 0; i < Sliders.Count; i++)
        {
            var w = Sliders[i].Wiring ??= new TileWiring();
            var slider = draft.Sliders[i];
            w.Title = $"Slider {i + 1}";
            w.HasChange = !Equals(Saved(baseline.Sliders, Source(layout.Sliders, i)), slider);
            w.Problem = TileProblem(draft, problems, i, new[] { PinOwnerKind.Slider }, _ => false);
        }
        for (int i = 0; i < Buttons.Count; i++)
        {
            var w = Buttons[i].Wiring ??= new TileWiring();
            var button = draft.Buttons[i];
            w.Title = $"Button {i + 1}";
            w.HasChange = !Equals(Saved(baseline.Buttons, Source(layout.Buttons, i)), button);
            w.Problem = TileProblem(draft, problems, i,
                                    new[] { PinOwnerKind.Button, PinOwnerKind.Light, PinOwnerKind.LightA, PinOwnerKind.LightB },
                                    f => f is ConfigField.ScreenBus or ConfigField.ScreenAddress);
        }

        // A button whose light the draft changes follows the new one only after the save
        // (controller-page spec §4.3): until then the controller still has the Hello's.
        foreach (var item in _controls.Buttons.Items) item.IsLightChanged = false;
        for (int i = 0; i < Buttons.Count; i++)
        {
            var kind = KindOf(draft.Buttons[i]);
            Buttons[i].IsLightChanged = !Buttons[i].IsNew && Buttons[i].Indicator is { } hello && hello != kind;
            Buttons[i].IsLightRemoved = Buttons[i].IsLightChanged && kind == IndicatorKind.None;
        }

        // The Controller page names a control by its place in the draft while the draft draws it
        // (hardware-page spec §6.5): after an unsaved removal the later ones are numbered up.
        foreach (var item in _controls.Sliders.Items) item.DraftHeading = null;
        foreach (var item in _controls.Buttons.Items) item.DraftHeading = null;
        for (int i = 0; i < Sliders.Count; i++) Sliders[i].DraftHeading = $"Slider {i + 1}";
        for (int i = 0; i < Buttons.Count; i++) Buttons[i].DraftHeading = $"Button {i + 1}";

        // A Controls tile the draft doesn't draw still measures its wiring layer.
        foreach (var item in _controls.Sliders.Items) item.Wiring ??= new TileWiring { Title = item.Heading };
        foreach (var item in _controls.Buttons.Items) item.Wiring ??= new TileWiring { Title = item.Heading };

        // A detached tile the draft no longer draws (after Discard, Start over) can't stay selected:
        // Controls has no such control.
        if (_controls.Selected is { } selected && !Drawn(selected)) _controls.Selected = null;

        UpdateTestStates();
        foreach (var field in _pinFields.Values) UpdatePinField(field);
        UpdateInspected();
        UpdateChooser();
        UpdateSaveBar();
    }

    /// <summary><paramref name="item"/> is a tile of the drawing or a Controls item.</summary>
    private bool Drawn(object item) => item switch
    {
        SliderItemViewModel s => Sliders.Contains(s) || _controls.Sliders.Items.Contains(s),
        ButtonItemViewModel b => Buttons.Contains(b) || _controls.Buttons.Items.Contains(b),
        _ => false,
    };

    // ── The inspector (spec §5.3, §6.1, §6.2, §6.6) ────────────────────────────────────────

    /// <summary>The inspector shows the controller's settings (spec §6.6).</summary>
    private bool _settingsSelected;

    /// <summary>The title row's "Controller settings" chip: the inspector shows the controller's
    /// settings and the tile selection is cleared. Not during a hardware test: the
    /// inspector is hidden then (spec §8), so the chip is disabled.</summary>
    /// <param name="openSliders">Opens the Sliders card too (a slider's Smoothing link).</param>
    public void SelectControllerSettings(bool openSliders = false)
    {
        if (_disposed || IsTesting) return;
        _settingsSelected = true;
        _controls.Selected = null;
        UpdateInspected();
        if (openSliders && Inspected is ControllerSettingsViewModel settings) settings.IsSlidersOpen = true;
    }

    private IRelayCommand? _selectControllerSettingsCommand;

    /// <summary>The title row's "Controller settings" chip.</summary>
    public IRelayCommand SelectControllerSettingsCommand
        => _selectControllerSettingsCommand ??= new RelayCommand(() => SelectControllerSettings(), () => !IsTesting);

    /// <summary>The inspector shows the controller's settings: the chip is marked.</summary>
    public bool IsSettingsSelected => Inspected is ControllerSettingsViewModel;

    partial void OnInspectedChanged(object? value)
    {
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(InspectedSlider));
        OnPropertyChanged(nameof(InspectedButton));
        OnPropertyChanged(nameof(InspectedPanel));
    }

    /// <summary>The selected slider's Hardware section (the body of its inspector's section), or null.</summary>
    public SliderWiringViewModel? InspectedSlider => Inspected as SliderWiringViewModel;

    /// <summary>The selected button's Hardware section, or null.</summary>
    public ButtonWiringViewModel? InspectedButton => Inspected as ButtonWiringViewModel;

    /// <summary>What the side panel shows in place of a tile's inspector: Controller settings; null
    /// while a tile (or nothing) is selected.</summary>
    public object? InspectedPanel => Inspected as ControllerSettingsViewModel;

    /// <summary>A button's bus link: the I2C buses dialog, bus <paramref name="bus"/>'s card marked.</summary>
    public void ShowBus(int bus) => OpenBuses(bus);

    // ── The I2C buses dialog (controller-page spec §4.3) ─────────────────────────────────────

    /// <summary>The open I2C buses dialog's view model, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBusesOpen))]
    private I2cBusesViewModel? _buses;

    /// <summary>The I2C buses dialog is open.</summary>
    public bool IsBusesOpen => Buses is not null;

    /// <summary>Opens the I2C buses dialog (Controller settings › Screens, a button's bus link),
    /// with <paramref name="bus"/>'s card marked; not during the hardware test.</summary>
    public void OpenBuses(int? bus = null)
    {
        if (_disposed || IsTesting || Buses is not null) return;
        I2cBusesViewModel? dialog = null;
        dialog = new I2cBusesViewModel(_controller, PinField, () => { if (ReferenceEquals(Buses, dialog)) CloseBuses(); }, bus, _runtime.ScanI2cAsync);
        Buses = dialog;
    }

    /// <summary>Closes the I2C buses dialog; its edits stay in the draft.</summary>
    public void CloseBuses() => Buses = null;

    private void OnControlsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName != nameof(ControlsViewModel.Selected)) return;
        if (_controls.Selected is not null) _settingsSelected = false;
        UpdateInspected();
    }

    /// <summary>Points <see cref="Inspected"/> at the selection, keeping its view model while it
    /// is the same control at the same place.</summary>
    private void UpdateInspected()
    {
        object? next = Inspected;
        if (_settingsSelected)
        {
            if (Inspected is not ControllerSettingsViewModel) next = new ControllerSettingsViewModel(_controller, () => OpenConnection(true), () => OpenBuses());
        }
        else if (_controls.Selected is SliderItemViewModel slider && Sliders.IndexOf(slider) is >= 0 and var si)
        {
            if (Inspected is not SliderWiringViewModel { } shown || shown.Index != si || !ReferenceEquals(shown.Item, slider))
            {
                var index = si;
                next = new SliderWiringViewModel(si, slider, PinField(new PinOwner(PinOwnerKind.Slider, si)), () => AskRemove(slider),
                                                 _controller, () => OpenCalibration(index),
                                                 () => SelectControllerSettings(openSliders: true));
            }
        }
        else if (_controls.Selected is ButtonItemViewModel button && Buttons.IndexOf(button) is >= 0 and var bi)
        {
            if (Inspected is not ButtonWiringViewModel { } shown || shown.Index != bi || !ReferenceEquals(shown.Item, button))
                next = new ButtonWiringViewModel(bi, button, _controller, kind => PinField(new PinOwner(kind, bi)), () => AskRemove(button), ShowBus, _runtime.TryLights);
        }
        else next = null;

        if (!ReferenceEquals(next, Inspected))
        {
            (Inspected as IDisposable)?.Dispose();
            Inspected = next;
        }
        RefreshInspected();
    }

    private void RefreshInspected()
    {
        switch (Inspected)
        {
            case SliderWiringViewModel s:
                s.IsEditable = _controller.CanEdit;
                s.HasProblem = s.Item.Wiring?.HasProblem == true;
                UpdateCalibrationRows();
                break;
            case ButtonWiringViewModel b:
                b.Refresh();
                b.HasProblem = b.Item.Wiring?.HasProblem == true;
                break;
            case ControllerSettingsViewModel settings: settings.RefreshBuses(); break;
        }
        Buses?.Refresh();
    }

    /// <summary>"Remove Slider N?"; once removed, the tile now at its place (else the one before)
    /// is selected.</summary>
    private void AskRemove(ObservableObject item)
    {
        if (item is SliderItemViewModel slider && Sliders.IndexOf(slider) is >= 0 and var si)
            _controller.AskRemoveSlider(si, () => SelectAfterRemove(Sliders, si));
        else if (item is ButtonItemViewModel button && Buttons.IndexOf(button) is >= 0 and var bi)
            _controller.AskRemoveButton(bi, () => SelectAfterRemove(Buttons, bi));
    }

    private void SelectAfterRemove<T>(IList<T> tiles, int index) where T : ObservableObject
    {
        if (tiles.Count > 0) _controls.Select(tiles[Math.Min(index, tiles.Count - 1)]);
        else _controls.Selected = null;
    }

    // ── Pin fields (spec §6.3) ───────────────────────────────────────────────────────────────

    private readonly Dictionary<PinOwner, PinFieldViewModel> _pinFields = new();

    /// <summary>The last pick, with the pin its field had before: a pick that took another use's pin
    /// is undone by giving that use the old pin (the swap).</summary>
    private (PinOwner Owner, uint? Before)? _lastPick;

    /// <summary>
    /// The pin field of one use (a slider's, a button's, a light's, a bus's SDA or SCL), made once
    /// per use and kept current after every draft change: its value, whether it can be edited, and
    /// its problem with the swap fix (<see cref="DraftPins.ProblemFor"/>). The inspectors bind these.
    /// </summary>
    public PinFieldViewModel PinField(PinOwner owner)
    {
        if (_pinFields.TryGetValue(owner, out var field)) return field;
        field = new PinFieldViewModel(DraftPins.LabelOf(owner.Kind), DraftPins.RoleOf(owner.Kind), owner,
                                      () => _controller.Draft, () => DraftPins.Get(_controller.Draft, owner),
                                      pin => SetPin(owner, pin), SwapPins, _runtime.StartPinWatch, ui: _ui);
        field.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PinFieldViewModel.IsOpen)) OnPinFieldOpenChanged(field);
        };
        _pinFields[owner] = field;
        UpdatePinField(field);
        return field;
    }

    /// <summary>The pin field whose pin dialog is open (a window-level overlay), or null. Opening
    /// one closes any other.</summary>
    [ObservableProperty] private PinFieldViewModel? _pinDialog;

    private void OnPinFieldOpenChanged(PinFieldViewModel field)
    {
        if (field.IsOpen)
        {
            foreach (var other in _pinFields.Values)
                if (!ReferenceEquals(other, field) && other.IsOpen) other.Cancel();
            PinDialog = field;
        }
        else if (ReferenceEquals(PinDialog, field)) PinDialog = null;
    }

    /// <summary>Detect: the controller saw a pin move or be pressed (transport thread); the open
    /// pin dialog, at most one, marks its pin.</summary>
    private void OnPinActivity(object? sender, int pin) => _ui.Post(() =>
    {
        if (_disposed || pin < 0) return;
        foreach (var field in _pinFields.Values)
            if (field.IsOpen) field.OnPinActivity((uint)pin);
    });

    /// <summary>Closes the open pin dialog, which stops its pin watch: the window is hidden or the
    /// page left, and the visual tree stays.</summary>
    public void ClosePinDialog()
    {
        foreach (var field in _pinFields.Values) field.Cancel();
    }

    private void UpdatePinField(PinFieldViewModel field)
    {
        field.IsEditable = _controller.CanEdit;
        field.Refresh();
        var (message, other) = DraftPins.ProblemFor(_controller.Draft, _controller.Problems, field.Owner);
        field.SetProblem(message, other is not null && CanSwap(field.Owner, other) ? other : null);
    }

    private void SetPin(PinOwner owner, uint? pin)
    {
        if (!_controller.CanEdit) return;
        var draft = _controller.Draft;
        _lastPick = (owner, DraftPins.Get(draft, owner));
        DraftPins.Set(draft, owner, pin);
        _controller.OnDraftEdited();
    }

    /// <summary>The fix of a shared pin: <paramref name="a"/> and <paramref name="b"/> exchange
    /// pins. When they hold the same one because one of them just took it, that one keeps it and
    /// the other gets the pin it had before.</summary>
    private void SwapPins(PinOwner a, PinOwner b)
    {
        if (!_controller.CanEdit) return;
        var draft = _controller.Draft;
        if (DraftPins.Get(draft, a) == DraftPins.Get(draft, b))
        {
            if (_lastPick is not { Before: { } before } pick || (pick.Owner != a && pick.Owner != b)) return;
            DraftPins.Set(draft, pick.Owner == a ? b : a, before);
        }
        else DraftPins.Swap(draft, a, b);
        _lastPick = null;
        _controller.OnDraftEdited();
    }

    private bool CanSwap(PinOwner a, PinOwner b)
    {
        var draft = _controller.Draft;
        if (DraftPins.Get(draft, a) != DraftPins.Get(draft, b)) return true;
        return _lastPick is { Before: { } before } pick && (pick.Owner == a || pick.Owner == b)
               && before != DraftPins.Get(draft, pick.Owner);
    }

    /// <summary>Makes <paramref name="target"/> the draft's controls: the saved item each source
    /// names, else a detached one (the previous detached items are reused in order).</summary>
    private static void Sync<T>(ObservableCollection<T> target, IList<T> saved, int count, IReadOnlyList<int?> sources,
                                Func<int, T> createDetached) where T : class
    {
        var detached = new Queue<T>(target.Where(t => !saved.Contains(t)));
        var wanted = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            if (Source(sources, i) is int s && s < saved.Count) wanted.Add(saved[s]);
            else wanted.Add(detached.Count > 0 ? detached.Dequeue() : createDetached(i));
        }
        if (target.SequenceEqual(wanted)) return;
        target.Clear();
        foreach (var item in wanted) target.Add(item);
    }

    private static int? Source(IReadOnlyList<int?> sources, int i) => i < sources.Count ? sources[i] : null;

    private static T? Saved<T>(IList<T> baseline, int? source) where T : class
        => source is int s && s < baseline.Count ? baseline[s] : null;

    /// <summary>The kind of light the draft gives <paramref name="button"/>.</summary>
    private static IndicatorKind KindOf(Button button) => button.IndicatorCase switch
    {
        Button.IndicatorOneofCase.Led => IndicatorKind.Led,
        Button.IndicatorOneofCase.LedPair => IndicatorKind.LedPair,
        Button.IndicatorOneofCase.Screen => IndicatorKind.Screen,
        _ => IndicatorKind.None,
    };

    /// <summary>A tile's problem (its mark's tooltip and the danger outline, controller-page spec
    /// §4.1), or null: each of its pin fields' problems as the field words it (a shared pin reads
    /// "Slider 1 also uses GPIO 33.", so both ends of a conflict are marked), then its other
    /// problems (<paramref name="other"/> fields), one per line.</summary>
    private static string? TileProblem(ControllerConfig draft, IReadOnlyList<DraftProblem> problems, int item,
                                       PinOwnerKind[] pins, Func<ConfigField, bool> other)
    {
        var mine = pins.Select(kind => DraftPins.ProblemFor(draft, problems, new PinOwner(kind, item)).Message)
            .OfType<string>()
            .Concat(problems.Where(p => p.Item == item && other(p.Field)).Select(p => p.Message))
            .Distinct().ToList();
        return mine.Count == 0 ? null : string.Join("\n", mine);
    }

    // ── The hardware test on the drawing (spec §8) ───────────────────────────────────────────

    /// <summary>A hardware test runs (<see cref="ControllerViewModel.IsTesting"/>): the case is
    /// outlined, the inspector is hidden and the draft can't be edited.</summary>
    public bool IsTesting => _controller.IsTesting;

    /// <summary>A test can start: the controller is ready and nothing else runs.</summary>
    public bool CanStartTest => _controller.CanStartTest;

    /// <summary>The title row's Test.</summary>
    public IRelayCommand StartTestCommand => _controller.StartTestCommand;

    /// <summary>The title row's Stop test.</summary>
    public IRelayCommand StopTestCommand => _controller.StopTestCommand;

    /// <summary>The band under the case while a test runs.</summary>
    public string TestBandText => "Testing: sliders and buttons don't change volume or run actions.";

    // Per hardware input: the last raw value of each slider; this test's presses and held state
    // of each button.
    private readonly Dictionary<int, int> _raw = new();
    private readonly Dictionary<int, int> _testPresses = new();
    private readonly HashSet<int> _testHeld = new();

    /// <summary>The Controller page was left (for General): a running test ends, and Controls gets
    /// a selection of its own when the inspected one (a bus, the settings chip) isn't a tile.</summary>
    public void OnPageLeft()
    {
        if (_disposed) return;
        ClosePinDialog();
        _controller.OnPageLeft();
        _controls.FixSelection();
    }

    private void OnTestingChanged()
    {
        if (_disposed) return;
        if (IsTesting) CloseBuses();   // the test runs on the drawing, under no dialog
        _testPresses.Clear();
        _testHeld.Clear();
        OnPropertyChanged(nameof(IsTesting));
        _selectControllerSettingsCommand?.NotifyCanExecuteChanged();
        UpdateTestStates();
    }

    /// <summary>A click on button tile <paramref name="tile"/>'s light: it moves on as one press
    /// would (the runtime pushes it to the controller; no action runs).</summary>
    private void AdvanceLight(int tile)
    {
        if (_disposed || !IsTesting || Source(_controller.Layout.Buttons, tile) is not int input) return;
        var presses = _runtime.AdvanceTestIndicator(input);
        if (presses <= 0) return;
        _testPresses[input] = Math.Max(presses, _testPresses.GetValueOrDefault(input));
        UpdateTestStates();
    }

    /// <summary>Every tile's test state, from this test's raw input and the controller's Hello.</summary>
    private void UpdateTestStates()
    {
        var testing = IsTesting;
        var layout = _controller.Layout;
        var kinds = _runtime.Config.Config.Controller?.Buttons ?? new List<IndicatorKind>();
        for (int i = 0; i < Sliders.Count; i++)
        {
            if (Sliders[i].Wiring is not { } w) continue;
            w.IsTesting = testing;
            w.TestText = testing && Source(layout.Sliders, i) is int input && _raw.TryGetValue(input, out var raw) ? $"{raw}" : testing ? "–" : "";
        }
        for (int i = 0; i < Buttons.Count; i++)
        {
            if (Buttons[i].Wiring is not { } w) continue;
            var tile = i;
            w.Advance = () => AdvanceLight(tile);
            w.IsTesting = testing;
            if (!testing || Source(layout.Buttons, i) is not int input || input >= kinds.Count)
            {
                w.TestKind = null;
                w.TestLight = TestIndicator.Off;
                w.TestScreen = null;
                w.TestText = "";
                w.IsPressed = false;
                continue;
            }
            var kind = kinds[input];
            var presses = _testPresses.GetValueOrDefault(input);
            w.TestKind = kind;
            w.TestLight = TestMode.Indicator(kind, presses);
            w.TestScreen = kind == IndicatorKind.Screen ? TestMode.Screen(input, presses) : null;
            w.TestText = kind switch
            {
                IndicatorKind.Led or IndicatorKind.LedPair => w.TestLight.ToString(),
                _ => TestMode.PressText(presses),
            };
            w.IsPressed = _testHeld.Contains(input);
        }
    }

    // ── Raw input (spec §5.2: the fader shows the live raw level, a press flashes the tile) ──

    private void OnRawInput(object? sender, RawInputEventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var layout = _controller.Layout;
        if (e.Kind == RawInputKind.Slider) _raw[e.Index] = e.Value;
        if (e.Kind == RawInputKind.Slider && Find(Sliders, layout.Sliders, e.Index) is { } slider)
            slider.Wiring!.RawLevel = Math.Clamp(e.Value / MaxRaw, 0, 1);
        else if (e.Kind == RawInputKind.Button && e.Value != 0 && Find(Buttons, layout.Buttons, e.Index) is { } button)
            button.Pulse();
        if (!IsTesting || !_runtime.InTestMode) return;
        if (e.Kind == RawInputKind.Button)
        {
            if (e.Value != 0) _testHeld.Add(e.Index);
            else _testHeld.Remove(e.Index);
            // A click on a light may have counted on after this message was posted.
            _testPresses[e.Index] = Math.Max(e.Presses, _testPresses.GetValueOrDefault(e.Index));
        }
        UpdateTestStates();
    });

    /// <summary>The tile of hardware input <paramref name="input"/>: the draft control whose source it is.</summary>
    private static T? Find<T>(IList<T> tiles, IReadOnlyList<int?> sources, int input) where T : class
    {
        for (int i = 0; i < tiles.Count && i < sources.Count; i++)
            if (sources[i] == input) return tiles[i];
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _controller.DraftChanged -= OnDraftChanged;
        _controller.PropertyChanged -= OnControllerPropertyChanged;
        _controller.PageLeft -= OnStartOverEnded;
        _controller.Reverted -= OnStartOverEnded;
        _controls.Sliders.Items.CollectionChanged -= OnItemsChanged;
        _controls.Buttons.Items.CollectionChanged -= OnItemsChanged;
        _controls.PropertyChanged -= OnControlsPropertyChanged;
        _runtime.RawInput -= OnRawInput;
        _runtime.CalibrationChanged -= OnCalibrationChanged;
        _runtime.PinActivity -= OnPinActivity;
        ClosePinDialog();
        Status.Dispose();
        CloseCalibration();
        CloseBuses();
        Compare?.Dispose();
        ConnectionDialog?.Dispose();
        LeavePrompt?.Dispose();
        (Inspected as IDisposable)?.Dispose();
    }
}
