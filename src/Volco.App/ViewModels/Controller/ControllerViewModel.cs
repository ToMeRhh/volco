using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Google.Protobuf;
using Volco.Core.Communication;
using Volco.Core.Config;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;
using Volco.Core.Firmware;
using Volco.Core.Runtime;

namespace Volco.App.ViewModels;

/// <summary>
/// The Controller page's controller logic (spec docs/design/hardware-page.md): the banner, a draft
/// of the controller's config with the editing model, presets, Save/Revert through
/// <see cref="ControllerConfigService"/>, inline confirmations, the hardware test and firmware.
/// The page's view models (<see cref="Hardware.HardwareViewModel"/> and the ones it makes) show
/// it. Every runtime and service event is posted to the UI thread; everything else runs there.
/// </summary>
public sealed partial class ControllerViewModel : ObservableObject, IDisposable
{
    private readonly ConfigManager _config;
    private readonly VolcoRuntime _runtime;
    private readonly ControllerConfigService _service;
    private readonly IUiDispatcher _ui;
    private bool _disposed;

    private ControllerConfig _draft = new();
    private byte[] _baseline = Array.Empty<byte>();
    private bool _fixIt;
    /// <summary>The (version, state, crc) of the Hello a "new draft" action was chosen under; the
    /// permission lasts until the mirror changes or a different Hello arrives (spec §8.4).</summary>
    private (byte, ControllerConfigState, uint)? _permission;
    private bool _hasPermission;
    private IDisposable? _testHandle;
    private bool _udpChangedInSave;

    private readonly bool _ownsConnection;

    /// <param name="connection">The window's connection view model, shared with the sidebar's
    /// status; the page makes (and disposes) its own when none is given.</param>
    public ControllerViewModel(ConfigManager config, VolcoRuntime runtime, IUiDispatcher ui,
                               FirmwareService? firmware = null, ConnectionViewModel? connection = null)
    {
        _config = config;
        _runtime = runtime;
        _service = runtime.ControllerConfig;
        _ui = ui;
        _ownsConnection = connection is null;
        Connection = connection ?? new ConnectionViewModel(config, runtime, ui);
        Connection.PropertyChanged += OnConnectionEdited;

        _service.BannerChanged += OnServiceChanged;
        _service.OperationChanged += OnServiceChanged;
        _service.MirrorChanged += OnMirrorChanged;
        _runtime.ControllerStateChanged += OnControllerStateChanged;
        _runtime.LinkStatusChanged += OnLinkStatusChanged;
        _runtime.InTestModeChanged += OnInTestModeChanged;

        InitFirmware(firmware);
        LoadDraftFromMirror();
        RefreshStatus();
    }

    // ── Status and banner ────────────────────────────────────────────────────────────────────

    /// <summary>The link status, Connect/Disconnect and how Volco talks to the controller (the
    /// Connection dialog and the sidebar card).</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>The Connection dialog changed the type or port: the firmware state follows at
    /// once, before the (debounced) save reaches the config's events.</summary>
    private void OnConnectionEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName is not (nameof(ConnectionViewModel.ConnectionType) or nameof(ConnectionViewModel.ComPort))) return;
        RefreshAll();
    }

    public string ChipText => _service.LastHello?.Chip == ChipFamily.Esp32 ? "ESP32" : _service.LastHello is null ? "" : "Unknown";

    public ControllerBanner Banner => _service.Banner;

    /// <summary>Drives the sidebar dot and the tray.</summary>
    public bool FirmwareUpdateAvailable => Banner.FirmwareUpdateAvailable;

    /// <summary>An operation runs, a test is on, or firmware is being written: every action that
    /// writes to the controller is disabled.</summary>
    internal bool Busy => _service.Operation != ConfigOperation.Idle || IsTesting || IsFirmwareRunning;

    /// <summary>The card's Retry after a failed read.</summary>
    [RelayCommand]
    private void Retry() => _ = _service.RetryReadAsync();

    /// <summary>Compare's Send Volco's: the mirror's own bytes go to the controller.</summary>
    [RelayCommand]
    private void ApplyMine()
    {
        if (_service.Mirror is { Length: > 0 } mirror) SaveBytes(mirror, udpChanged: false, layout: null);
    }

    /// <summary>The card's Fix it: Volco's saved setup as a new draft, with its problems marked.</summary>
    [RelayCommand]
    private void FixIt()
    {
        if (DecodeMirror() is { } mirror) StartNewDraft(mirror, fixIt: true);
    }

    // ── The draft and the editing model (spec §8.4) ───────────────────────────────────────────

    /// <summary>The config the page edits. The inspectors edit its messages in place.</summary>
    public ControllerConfig Draft => _draft;

    /// <summary>Which of Volco's saved sliders and buttons each draft control is (hardware-page spec
    /// §6.5): identity whenever a whole draft is set, changed by <see cref="RemoveSlider"/>,
    /// <see cref="RemoveButton"/> and the Add commands.</summary>
    public ControlLayout Layout { get; private set; } = ControlLayout.Identity(0, 0);

    /// <summary>A confirmed save applied a layout that removed or added controls: Volco's sliders
    /// and buttons were rewritten to match, so their view models must be rebuilt.</summary>
    public event EventHandler? ControlsShifted;

    /// <summary>After every change to the draft, its layout, its problems or whether it can be
    /// edited. UI thread.</summary>
    internal event EventHandler? DraftChanged;

    /// <summary>The draft's problems, as marked on its fields and tiles; empty while it can't be edited.</summary>
    internal IReadOnlyList<DraftProblem> Problems => _problems;

    /// <summary>Hello configState 1 with the mirror's CRC.</summary>
    public bool InSync =>
        _service.LastHello is { Version: ProtocolVersion.Current, ConfigState: ControllerConfigState.Stored } h
        && _service.Mirror is { Length: > 0 } m && h.ConfigCrc == ControllerConfigCodec.Crc32(m);

    /// <summary>The draft can be changed: never while a save runs, so the controller's answer is
    /// about the draft (and layout) that was sent, nor during a hardware test (editing is paused).</summary>
    public bool CanEdit => _runtime.ControllerState == ControllerState.Ready && (InSync || _hasPermission) && !IsSaving && !IsTesting;

    /// <summary>The baseline is Volco's saved copy, not what the controller holds: while setting up
    /// a controller that doesn't hold the draft, it is always unsaved.</summary>
    public bool IsDirty => _fixIt || IsSettingUp || !_draft.ToByteArray().AsSpan().SequenceEqual(_baseline);

    /// <summary>The draft is the Blank placeholder Volco starts from with no copy of the controller's
    /// setup, and nobody has started a draft from it: it counts as dirty against the empty baseline
    /// but holds nothing the user changed.</summary>
    public bool IsPlaceholder => _baseline.Length == 0 && !_hasPermission && !_fixIt
                                 && _draft.ToByteArray().AsSpan().SequenceEqual(BlankBytes);

    /// <summary>The Blank placeholder draft's bytes.</summary>
    private static readonly byte[] BlankBytes = new ControllerConfig { Preset = ControllerPresets.BlankId }.ToByteArray();

    /// <summary>The draft holds changes the user made and hasn't saved (dirty, and not the
    /// placeholder): leaving the Controller page asks first, and the drawing shows the draft's
    /// controls (controller-page spec §4.1, §4.4).</summary>
    public bool HasUnsavedChanges => IsDirty && !IsPlaceholder;

    public bool CanSave => CanEdit && IsDirty && !Busy;

    /// <summary>A new draft could be edited: the controller is ready and nothing (a save, a test,
    /// a flash) runs. Start over is offered only then.</summary>
    internal bool CanStartNewDraft => _runtime.ControllerState == ControllerState.Ready && !IsSaving && !IsTesting && !Busy;

    private bool _setupChanged;

    /// <summary>The controller's setup changed (Volco's copy moved) under an unsaved draft: the save
    /// bar says so, since Save would put the draft back over it. Null otherwise.</summary>
    public string? Notice => _setupChanged ? "The controller's setup changed. Discard to load it." : null;

    private void SetSetupChanged(bool value)
    {
        if (_setupChanged == value) return;
        _setupChanged = value;
        OnPropertyChanged(nameof(Notice));
    }

    /// <summary>Replaces the draft's wiring with <paramref name="preset"/>, asking first when the
    /// draft has hardware (the device card's "Load a preset…").</summary>
    internal void LoadPreset(ControllerPreset preset)
    {
        if (!preset.IsAvailable || !CanEdit) return;
        if (ControllerDraft.HasHardware(_draft))
            Ask($"Replace the current hardware with {preset.DisplayName}?", "Replace", () => { if (CanEdit) ApplyPreset(preset); });
        else ApplyPreset(preset);
    }

    /// <summary>How Volco talks to the controller (the saved settings).</summary>
    internal ConnectionConfig ConnectionConfig => _config.Config.Connection;

    /// <summary>A button has a screen: the screen settings apply.</summary>
    public bool ShowBehaviour => _draft.Buttons.Any(b => b.IndicatorCase == Button.IndicatorOneofCase.Screen);

    private void ApplyPreset(ControllerPreset preset)
    {
        SetDraft(ControllerPresets.ApplyTo(_draft, preset), keepFixIt: true);
    }

    private void StartNewDraft(ControllerConfig draft, bool fixIt)
    {
        _hasPermission = true;
        _permission = _service.LastHello is { } h ? (h.Version, h.ConfigState, h.ConfigCrc) : null;
        _fixIt = fixIt;
        SetDraft(draft, keepFixIt: true);
    }

    internal ControllerConfig? DecodeMirror()
    {
        var mirror = _service.Mirror;
        if (mirror is not { Length: > 0 }) return null;
        try { return ControllerConfigCodec.Decode(mirror); }
        catch (Exception) { return null; }
    }

    /// <summary>The draft = the mirror's decode (Blank when there is none); the baseline follows.</summary>
    private void LoadDraftFromMirror()
    {
        SetSetupChanged(false);
        var decoded = DecodeMirror();
        _baseline = decoded is null ? Array.Empty<byte>() : decoded.ToByteArray();
        _fixIt = false;
        SetDraft(decoded ?? new ControllerConfig { Preset = ControllerPresets.BlankId }, keepFixIt: false);
    }

    private void SetDraft(ControllerConfig draft, bool keepFixIt)
    {
        if (!keepFixIt) _fixIt = false;
        _remoteError = null;
        _pcIpText = null;          // Important 3: the box shows the new draft's address
        Confirmation = null;       // a question about the old draft must not act on the new one
        _draft = draft;
        Layout = ControlLayout.Identity(draft.Sliders.Count, draft.Buttons.Count);
        Recompute();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>After every user edit: the last save's message goes, then everything is recomputed.</summary>
    internal void OnDraftEdited()
    {
        if (!IsSaving) SetProgress(null);
        _remoteError = null;
        Recompute();
    }

    /// <summary>The (item, field) of the controller's last INVALID, shown until the next edit.</summary>
    private (byte Item, byte Field)? _remoteError;

    /// <summary>Dirty and the problems.</summary>
    private void Recompute()
    {
        if (!IsDirty) SetSetupChanged(false);   // nothing unsaved is left for the notice to warn about
        if (CanEdit) ShowLocalErrors();
        else ClearErrors();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(IsSettingUp));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowBehaviour));
        SaveCommand.NotifyCanExecuteChanged();
        DraftChanged?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void AddSlider()
    {
        if (!CanEdit) return;
        _draft.Sliders.Add(new Slider());
        Layout.AddSlider();
        OnDraftEdited();
    }

    [RelayCommand]
    private void AddButton()
    {
        if (!CanEdit) return;
        _draft.Buttons.Add(new Button());
        Layout.AddButton();
        OnDraftEdited();
    }

    /// <summary>Removes slider <paramref name="index"/> from the draft. The later ones move down
    /// and keep what they control (spec §6.5); the inspector asks first.</summary>
    internal void RemoveSlider(int index)
    {
        if (!CanEdit || index < 0 || index >= _draft.Sliders.Count) return;
        _draft.Sliders.RemoveAt(index);
        Layout.RemoveSlider(index);
        OnDraftEdited();
    }

    /// <summary>Removes button <paramref name="index"/> from the draft. The later ones move down
    /// and keep their actions (spec §6.5); the inspector asks first.</summary>
    internal void RemoveButton(int index)
    {
        if (!CanEdit || index < 0 || index >= _draft.Buttons.Count) return;
        _draft.Buttons.RemoveAt(index);
        Layout.RemoveButton(index);
        OnDraftEdited();
    }

    /// <summary>Remove (spec §6.5): "Remove Slider N?", then <see cref="RemoveSlider"/> and
    /// <paramref name="removed"/>.</summary>
    internal void AskRemoveSlider(int index, Action? removed = null)
    {
        if (!CanEdit || index < 0 || index >= _draft.Sliders.Count) return;
        Ask($"Remove Slider {index + 1}?", "Remove", () => { RemoveSlider(index); removed?.Invoke(); }, destructive: true);
    }

    /// <summary>Remove (spec §6.5): "Remove Button N?", then <see cref="RemoveButton"/> and
    /// <paramref name="removed"/>.</summary>
    internal void AskRemoveButton(int index, Action? removed = null)
    {
        if (!CanEdit || index < 0 || index >= _draft.Buttons.Count) return;
        Ask($"Remove Button {index + 1}?", "Remove", () => { RemoveButton(index); removed?.Invoke(); }, destructive: true);
    }

    // ── Problems ─────────────────────────────────────────────────────────────────────────────

    private void ClearErrors()
    {
        WifiError = null;
        _problems = Array.Empty<DraftProblem>();
        _wifiFields.Clear();
        OnWifiFieldsChanged();
    }

    /// <summary>The problems the save would stop on (all of them, so both ends of a pin conflict
    /// are marked).</summary>
    private IReadOnlyList<DraftProblem> _problems = Array.Empty<DraftProblem>();

    private void ShowLocalErrors()
    {
        ClearErrors();
        var problems = DraftProblems.Find(_draft, ChipInfo.Esp32).ToList();
        if (problems.Count == 0 && _remoteError is { } r)
            problems.Add(new DraftProblem((ConfigField)r.Field, r.Item,
                $"The controller didn't accept {ControllerDraft.FieldDescription((ConfigField)r.Field)}."));
        _problems = problems;
        foreach (var p in problems.Where(p => p.Field is ConfigField.UdpSsid or ConfigField.UdpPassword
                                                       or ConfigField.UdpPcIp or ConfigField.UdpPort))
        {
            // The Connection dialog's Wi-Fi tab marks its fields; the tiles and fields read the rest.
            WifiError = WifiError is null ? p.Message : WifiError + "\n" + p.Message;
            _wifiFields.Add(p.Field);
        }
        OnWifiFieldsChanged();
    }

    private readonly HashSet<ConfigField> _wifiFields = new();
    public bool SsidInvalid => _wifiFields.Contains(ConfigField.UdpSsid);
    public bool PasswordInvalid => _wifiFields.Contains(ConfigField.UdpPassword);
    public bool PcIpInvalid => _wifiFields.Contains(ConfigField.UdpPcIp);
    public bool PortsInvalid => _wifiFields.Contains(ConfigField.UdpPort);

    private void OnWifiFieldsChanged()
    {
        OnPropertyChanged(nameof(SsidInvalid));
        OnPropertyChanged(nameof(PasswordInvalid));
        OnPropertyChanged(nameof(PcIpInvalid));
        OnPropertyChanged(nameof(PortsInvalid));
    }

    [ObservableProperty] private string? _wifiError;

    // ── Save and Revert ──────────────────────────────────────────────────────────────────────

    /// <summary>The last save's result (or why it couldn't start); a failure shows on the device card.</summary>
    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private bool _isSaving;

    private LinkTone _progressTone = LinkTone.Neutral;

    /// <summary>The result's tone: a confirmed save is good, a failed one a danger.</summary>
    internal LinkTone ProgressTone => ProgressText is null ? LinkTone.Neutral : _progressTone;

    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(CanEdit));

    /// <summary>A save finished, with its status (the leave prompt continues on Confirmed). UI thread.</summary>
    internal event EventHandler<SaveStatus>? SaveFinished;

    private void SetProgress(string? text, LinkTone tone = LinkTone.Neutral)
    {
        _progressTone = tone;
        ProgressText = text;
        OnPropertyChanged(nameof(ProgressTone));
    }

    /// <summary>The device card's save line (hardware-page spec §4.1) while a save runs; null otherwise.</summary>
    internal string? SavePhaseText => !IsSaving ? null
        : _service.Progress is { Phase: SavePhase.Confirming } ? "Restarting…" : "Saving to the controller…";

    /// <summary>The last operation's error (a save's, or a firmware install's, update's or check's),
    /// which the device card shows instead of its detail line until the next action; null for none.</summary>
    internal string? Failure => !IsSaving && ProgressText is not null && _progressTone == LinkTone.Danger ? ProgressText
        : !IsFirmwareRunning && FirmwareMessage is not null && FirmwareMessageTone is LinkTone.Danger or LinkTone.Warning ? FirmwareMessage
        : null;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave) return;
        var v = ControllerConfigValidator.Validate(_draft, ChipInfo.Esp32);
        if (!v.IsOk)
        {
            ShowLocalErrors();
            SetProgress(_problems.Count == 1 ? "Fix the problem marked in red first."
                                             : $"Fix the {_problems.Count} problems marked in red first.", LinkTone.Danger);
            return;
        }
        bool udpChanged = !UdpBytes(_draft).AsSpan().SequenceEqual(UdpBytes(BaselineConfig()));
        if (udpChanged && _config.Config.Connection.Type == ConnectionType.Udp)
            Ask("Changing the Wi-Fi settings may disconnect the controller. If it doesn't come back, plug it in with USB.",
                "Save", () => { if (CanSave) EncodeAndSave(udpChanged); });
        else EncodeAndSave(udpChanged);
    }

    /// <summary>Encode can still refuse what the validator skips (Wi-Fi strings over their limit
    /// while Wi-Fi is off): say so instead of failing silently.</summary>
    private void EncodeAndSave(bool udpChanged)
    {
        byte[] blob;
        try { blob = ControllerConfigCodec.Encode(_draft); }
        catch (ControllerConfigFormatException ex)
        {
            SetProgress($"This setup can't be saved: {ex.Message}", LinkTone.Danger);
            return;
        }
        SaveBytes(blob, udpChanged, Layout.Clone());
    }

    private bool DraftEquals(byte[] blob)
    {
        try { return ControllerConfigCodec.Encode(_draft).AsSpan().SequenceEqual(blob); }
        catch (ControllerConfigFormatException) { return false; }
    }

    /// <summary>Volco's saved copy of the controller's config (empty when there is none).</summary>
    internal ControllerConfig BaselineConfig()
    {
        try { return _baseline.Length > 0 ? ControllerConfig.Parser.ParseFrom(_baseline) : new ControllerConfig(); }
        catch (InvalidProtocolBufferException) { return new ControllerConfig(); }
    }

    private static byte[] UdpBytes(ControllerConfig c) => c.Udp?.ToByteArray() ?? Array.Empty<byte>();

    private bool _savingSetup;

    /// <summary>A copy of the layout of the draft being saved; applied to Volco's settings once the
    /// controller confirms (the mirror's change may reset <see cref="Layout"/> before the result arrives).</summary>
    private ControlLayout? _savingLayout;

    /// <summary>The bytes being saved.</summary>
    private byte[]? _savingBlob;

    private void SaveBytes(byte[] blob, bool udpChanged, ControlLayout? layout)
    {
        _udpChangedInSave = udpChanged;
        _savingLayout = layout;
        _savingBlob = blob;
        _savingSetup = IsSettingUp || IsNotSetUp;
        IsSaving = true;
        FirmwareMessage = null;   // a firmware result belongs to the operation before
        SetProgress("Sending…");
        var save = _service.SaveAsync(blob);
        _ = Finish();

        async Task Finish()
        {
            var r = await save.ConfigureAwait(false);
            _ui.Post(() => OnSaved(r));
        }
    }

    private void OnSaved(ConfigSaveResult r)
    {
        if (_disposed) return;
        IsSaving = false;
        var text = r.Status switch
        {
            SaveStatus.Confirmed when _savingSetup => "Saved. The controller is set up: press Test controller to try it.",
            SaveStatus.Confirmed => "Saved. The controller restarted with the new setup.",
            SaveStatus.Rejected when r.Code == ConfigResultCode.Invalid
                => $"The controller rejected the setup: {ControllerDraft.Describe(r.Item, r.Field)}.",
            SaveStatus.Rejected when r.Code == ConfigResultCode.StorageFailed
                => "The controller couldn't save the setup. Its previous setup is unchanged.",
            SaveStatus.Rejected => "The controller couldn't read the setup. Update the firmware and try again.",
            SaveStatus.NotRunningNewSetup => "The controller restarted but isn't running the new setup.",
            SaveStatus.DidNotComeBack => _udpChangedInSave
                ? "The controller didn't come back after restarting. If you changed the Wi-Fi settings, plug it in with USB to check them."
                : "The controller didn't come back after restarting.",
            SaveStatus.NoAnswer => "The controller didn't answer.",
            SaveStatus.ConnectionLost => "The controller disconnected while saving. Once it's connected again, check which setup it has.",
            SaveStatus.Busy => "Another operation is running. Try again in a moment.",
            _ => null,
        };
        var confirmed = r.Status == SaveStatus.Confirmed;
        SetProgress(text, confirmed ? LinkTone.Good : LinkTone.Danger);
        if (r.Status == SaveStatus.Rejected && r.Code == ConfigResultCode.Invalid) _remoteError = (r.Item, r.Field);
        if (r.Status == SaveStatus.Confirmed)
        {
            _fixIt = false;
            SetSetupChanged(false);
        }
        if (r.Status == SaveStatus.Confirmed && _savingSetup) OfferTest = true;   // the device card offers a test
        var layout = _savingLayout;
        var saved = _savingBlob;
        _savingLayout = null;
        _savingBlob = null;
        if (r.Status == SaveStatus.Confirmed && layout is not null) ApplyLayout(layout, saved);
        RefreshAll();
        SaveFinished?.Invoke(this, r.Status);
    }

    /// <summary>Spec §6.5: the controller has the saved draft, so Volco's sliders and buttons follow
    /// its removed and added controls. A failed or discarded save never gets here.</summary>
    private void ApplyLayout(ControlLayout layout, byte[]? saved)
    {
        // Volco's lists now match the saved draft: its controls are the saved ones at their positions.
        if (saved is not null && DraftEquals(saved))
        {
            Layout = ControlLayout.Identity(_draft.Sliders.Count, _draft.Buttons.Count);
            OnPropertyChanged(nameof(Layout));
        }
        if (layout.IsIdentity) return;
        layout.ApplyTo(_config.Config);
        _config.RequestSave();
        ControlsShifted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The save bar's Discard: the draft goes back to Volco's saved copy.</summary>
    [RelayCommand]
    private void Revert()
    {
        SetProgress(null);
        _hasPermission = false;
        LoadDraftFromMirror();
        RefreshAll();
        Reverted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised after Discard (<see cref="RevertCommand"/>) put Volco's saved copy back.</summary>
    internal event EventHandler? Reverted;

    // ── Confirmations ────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private Confirmation? _confirmation;

    /// <summary>Asks inline. The answer only acts on the draft it was asked about: a question that
    /// outlived its draft (Revert, a rebase, a new preset) does nothing.</summary>
    private void Ask(string message, string confirmLabel, Action onConfirm, bool destructive = false)
    {
        var askedAbout = _draft;
        Confirmation = new Confirmation(message, confirmLabel,
            () => { if (ReferenceEquals(askedAbout, _draft)) onConfirm(); },
            () => Confirmation = null, destructive);
    }

    // ── Screen behaviour (ScreenBehaviour) ───────────────────────────────────────────────────

    // A number box's value sets its field; an emptied box (null) clears it, so it shows and saves
    // the proto default, which the firmware applies to an absent field too.
    private ScreenBehaviour S => _draft.EffectiveScreens();
    private void EditS(Action<ScreenBehaviour> edit, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!CanEdit) { OnPropertyChanged(name); return; }
        _draft.Screens ??= new ScreenBehaviour();
        edit(_draft.Screens);
        OnPropertyChanged(name);
        OnDraftEdited();
    }

    public decimal? Brightness { get => S.Brightness; set => EditS(s => { if (value is { } n) s.Brightness = (uint)n; else s.ClearBrightness(); }); }
    public bool DimEnabled { get => S.DimEnabled; set => EditS(s => s.DimEnabled = value); }
    public decimal? DimAfterS { get => S.DimAfterS; set => EditS(s => { if (value is { } n) s.DimAfterS = (uint)n; else s.ClearDimAfterS(); }); }
    public decimal? DimBrightness { get => S.DimBrightness; set => EditS(s => { if (value is { } n) s.DimBrightness = (uint)n; else s.ClearDimBrightness(); }); }
    public bool OffEnabled { get => S.OffEnabled; set => EditS(s => s.OffEnabled = value); }
    public decimal? OffAfterS { get => S.OffAfterS; set => EditS(s => { if (value is { } n) s.OffAfterS = (uint)n; else s.ClearOffAfterS(); }); }
    public bool PixelShiftEnabled { get => S.PixelShiftEnabled; set => EditS(s => s.PixelShiftEnabled = value); }
    public decimal? PixelShiftEveryS { get => S.PixelShiftEveryS; set => EditS(s => { if (value is { } n) s.PixelShiftEveryS = (uint)n; else s.ClearPixelShiftEveryS(); }); }
    public bool WakeSwallowsPress { get => S.WakeSwallowsPress; set => EditS(s => s.WakeSwallowsPress = value); }
    public decimal? ScrollHoldMs { get => S.ScrollHoldMs; set => EditS(s => { if (value is { } n) s.ScrollHoldMs = (uint)n; else s.ClearScrollHoldMs(); }); }
    public decimal? ScrollSpeed { get => S.ScrollSpeedPxS; set => EditS(s => { if (value is { } n) s.ScrollSpeedPxS = (uint)n; else s.ClearScrollSpeedPxS(); }); }
    public decimal? ScrollEndHoldMs { get => S.ScrollEndHoldMs; set => EditS(s => { if (value is { } n) s.ScrollEndHoldMs = (uint)n; else s.ClearScrollEndHoldMs(); }); }
    public decimal? TransitionMs { get => S.TransitionMs; set => EditS(s => { if (value is { } n) s.TransitionMs = (uint)n; else s.ClearTransitionMs(); }); }

    // ── Input tuning and Wi-Fi ───────────────────────────────────────────────────────────────

    private InputTuning In => _draft.EffectiveInput();
    private void EditIn(Action<InputTuning> edit, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!CanEdit) { OnPropertyChanged(name); return; }
        _draft.Input ??= new InputTuning();
        edit(_draft.Input);
        OnPropertyChanged(name);
        OnDraftEdited();
    }

    public decimal? DebounceMs { get => In.DebounceMs; set => EditIn(i => { if (value is { } n) i.DebounceMs = (uint)n; else i.ClearDebounceMs(); }); }
    public decimal? SliderIntervalMs { get => In.SliderIntervalMs; set => EditIn(i => { if (value is { } n) i.SliderIntervalMs = (uint)n; else i.ClearSliderIntervalMs(); }); }
    public decimal? SmoothingPct { get => In.FilterWeightPct; set => EditIn(i => { if (value is { } n) i.FilterWeightPct = (uint)n; else i.ClearFilterWeightPct(); }); }
    public decimal? Deadband { get => In.SliderDeadband; set => EditIn(i => { if (value is { } n) i.SliderDeadband = (uint)n; else i.ClearSliderDeadband(); }); }

    private Udp U => _draft.EffectiveUdp();
    private void EditU(Action<Udp> edit, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!CanEdit) { OnPropertyChanged(name); return; }
        _draft.Udp ??= new Udp();
        edit(_draft.Udp);
        OnPropertyChanged(name);
        OnDraftEdited();
    }

    public bool UdpEnabled
    {
        get => U.Enabled;
        set => EditU(u =>
        {
            u.Enabled = value;
            // Pre-filled from Volco's own UDP listen port (spec §8.4).
            if (value && !u.HasPcPort) u.PcPort = (uint)_config.Config.Connection.Udp.ListenPort;
            OnPropertyChanged(nameof(PcPort));
        });
    }

    public string Ssid { get => U.Ssid; set => EditU(u => u.Ssid = value ?? ""); }
    public string Password { get => U.Password; set => EditU(u => u.Password = value ?? ""); }

    [ObservableProperty] private bool _showPassword;
    public char PasswordChar => ShowPassword ? '\0' : '•';
    partial void OnShowPasswordChanged(bool value) => OnPropertyChanged(nameof(PasswordChar));

    private string? _pcIpText;

    /// <summary>Dotted IPv4. Text that isn't an address clears the field, which the validator reports.</summary>
    public string PcIp
    {
        get => _pcIpText ?? (U.HasPcIp ? FormatIp(U.PcIp) : "");
        set
        {
            _pcIpText = value;
            EditU(u =>
            {
                if (IPAddress.TryParse(value ?? "", out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                    && (value ?? "").Count(ch => ch == '.') == 3)
                {
                    var b = ip.GetAddressBytes();
                    u.PcIp = (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
                }
                else u.ClearPcIp();
            });
        }
    }

    private static string FormatIp(uint ip) => $"{ip >> 24}.{(ip >> 16) & 255}.{(ip >> 8) & 255}.{ip & 255}";

    public decimal? PcPort { get => U.PcPort; set => EditU(u => { if (value is { } n) u.PcPort = (uint)n; else u.ClearPcPort(); }); }
    public decimal? LocalPort { get => U.LocalPort; set => EditU(u => { if (value is { } n) u.LocalPort = (uint)n; else u.ClearLocalPort(); }); }

    // ── Test (hardware-page spec §8): on the drawing ─────────────────────────────────────────

    /// <summary>The hardware test runs: the drawing shows the raw input and editing is paused.</summary>
    [ObservableProperty] private bool _isTesting;

    public bool CanStartTest => !IsTesting && !IsFirmwareRunning && _runtime.ControllerState == ControllerState.Ready
                                && _service.Operation == ConfigOperation.Idle;

    partial void OnIsTestingChanged(bool value) => RefreshAll();

    partial void OnIsFirmwareRunningChanged(bool value) => RefreshAll();

    [RelayCommand]
    private void StartTest()
    {
        if (!CanStartTest) return;
        _testHandle = _runtime.EnterTestMode();
        IsTesting = _runtime.InTestMode;
    }

    [RelayCommand]
    private void StopTest()
    {
        var handle = _testHandle;
        _testHandle = null;
        handle?.Dispose();
        IsTesting = false;
    }

    /// <summary>The Controller page was left or the window hidden: the test ends (spec §8), and a
    /// pending question goes. The last firmware result stays: the card that shows it is on every
    /// page (controller-page spec §5).</summary>
    public void OnPageLeft()
    {
        if (_testHandle is not null || IsTesting) StopTest();
        Confirmation = null;
        PageLeft?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The window was hidden: as <see cref="OnPageLeft"/>, and the last firmware result
    /// ("Installed…", "Test your controller now", the install offer) goes too.</summary>
    public void OnWindowHidden()
    {
        OnPageLeft();
        ClearFirmwareResult();
    }

    /// <summary>Raised by <see cref="OnPageLeft"/>: Hardware was left or the window hidden.</summary>
    internal event EventHandler? PageLeft;

    // ── Runtime and service events (any thread → posted) ─────────────────────────────────────

    private void OnServiceChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var h = _service.LastHello;
        if (_hasPermission && (h is null ? _permission is not null : _permission != (h.Version, h.ConfigState, h.ConfigCrc)))
            _hasPermission = false;   // a different Hello ends a "new draft" permission
        OnPropertyChanged(nameof(SavePhaseText));
        RefreshAll();
    });

    private void OnMirrorChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        // With no usable copy in Volco the draft is the Blank placeholder. While nobody has started a
        // draft from it, it follows the copy that arrives (the automatic read, Retry, Keep): it only
        // counts as dirty against the empty baseline, and kept, the Controller page drew no controls
        // and the draft's layout (none) no longer matched the controller's setup.
        var placeholder = IsPlaceholder;
        _hasPermission = false;
        var decoded = DecodeMirror();
        var newBaseline = decoded?.ToByteArray() ?? Array.Empty<byte>();
        // A clean draft follows the new copy; a dirty one stays, and the save bar counts against it.
        var follow = placeholder || !IsDirty || _draft.ToByteArray().AsSpan().SequenceEqual(newBaseline);
        _baseline = newBaseline;
        if (follow)
        {
            _fixIt = false;
            SetSetupChanged(false);
            SetDraft(decoded ?? new ControllerConfig { Preset = ControllerPresets.BlankId }, keepFixIt: false);
        }
        else SetSetupChanged(true);
        RefreshAll();
    });

    private void OnControllerStateChanged(object? sender, ControllerState state) => _ui.Post(() =>
    {
        if (_disposed) return;
        // The controller went away: "Installed…" or "Test your controller now" no longer apply.
        if (_runtime.ControllerState == ControllerState.NoController) ClearFirmwareResult();
        RefreshAll();
    });

    private void OnLinkStatusChanged(object? sender, LinkStatus link) => _ui.Post(() =>
    {
        if (!_disposed) RefreshAll();
    });

    private void OnInTestModeChanged(object? sender, bool on) => _ui.Post(() =>
    {
        if (_disposed || on || _runtime.InTestMode) return;   // stale: a newer session is running
        _testHandle = null;   // the runtime ended it (Detach, replacement)
        IsTesting = false;
    });

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(FirmwareUpdateAvailable));
        OnPropertyChanged(nameof(IsNotSetUp));
    }

    private void RefreshAll()
    {
        // A pending question is only valid while the page could still act on it.
        if (Confirmation is not null && (!CanEdit || Busy)) Confirmation = null;
        RefreshStatus();
        OnPropertyChanged(nameof(InSync));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanStartTest));
        Recompute();
        RefreshFirmware();
        StartTestCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnPageLeft();
        DisposeFirmware();
        _disposed = true;
        _service.BannerChanged -= OnServiceChanged;
        _service.OperationChanged -= OnServiceChanged;
        _service.MirrorChanged -= OnMirrorChanged;
        _runtime.ControllerStateChanged -= OnControllerStateChanged;
        _runtime.LinkStatusChanged -= OnLinkStatusChanged;
        _runtime.InTestModeChanged -= OnInTestModeChanged;
        Connection.PropertyChanged -= OnConnectionEdited;
        if (_ownsConnection) Connection.Dispose();
    }
}
