using System.Collections.ObjectModel;
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
/// The Controller page (spec §8.4, §8.7): the controller's status and banner, a draft of its
/// config with the editing model, presets, Save/Revert through <see cref="ControllerConfigService"/>,
/// inline confirmations, and the hardware test. Every runtime and service event is posted to the
/// UI thread; everything else runs there.
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
        _runtime.ControllerInfoChanged += OnControllerInfoChanged;
        _runtime.InTestModeChanged += OnInTestModeChanged;
        _runtime.RawInput += OnRawInput;

        InitFirmware(firmware);
        LoadDraftFromMirror();
        RebuildTest();
        RefreshStatus();
    }

    // ── Status and banner ────────────────────────────────────────────────────────────────────

    /// <summary>The page's connection section: the link status, Connect/Disconnect and how Volco
    /// talks to the controller.</summary>
    public ConnectionViewModel Connection { get; }

    /// <summary>The Connection section changed the type or port: the firmware panel follows at
    /// once, before the (debounced) save reaches the config's events.</summary>
    private void OnConnectionEdited(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed || e.PropertyName is not (nameof(ConnectionViewModel.ConnectionType) or nameof(ConnectionViewModel.ComPort))) return;
        RefreshAll();
    }

    /// <summary>The same headline as the sidebar and the tray (<see cref="LinkText"/>).</summary>
    public string StatusText => LinkText.Headline(_runtime.LinkStatus, _config.Config.Connection);

    /// <summary>An expected wait is running: connecting, a firmware operation or a restart.</summary>
    public bool IsWaiting => _runtime.LinkStatus.State == LinkState.Waiting;

    /// <summary>Hardware, Test, Screens and Advanced need a connected controller.</summary>
    public bool ShowEditor => _runtime.ControllerState == ControllerState.Ready;

    public string EditorPlaceholder
    {
        get
        {
            var link = _runtime.LinkStatus;
            var connection = _config.Config.Connection;
            return link.State switch
            {
                LinkState.Disconnected => "Volco is disconnected. Press Connect above.",
                LinkState.Incompatible => "Update the controller's firmware to set it up.",
                LinkState.NoAnswer => "Check the controller's firmware above, then set it up here.",
                LinkState.Waiting => LinkText.Activity(link.Activity),
                LinkState.Searching when connection.Type == ConnectionType.Serial
                    => "Plug the controller in with USB and choose its port above.",
                _ => LinkText.Explanation(link, connection),
            };
        }
    }

    public string ChipText => _service.LastHello?.Chip == ChipFamily.Esp32 ? "ESP32" : _service.LastHello is null ? "" : "Unknown";

    public string FirmwareText => _service.LastHello switch
    {
        null => "",
        { FirmwareVersion: ControllerBannerLogic.DevFirmware } => "Development firmware",
        { FirmwareVersion: "" } => "Unknown firmware",
        var h => FirmwareVersionText(h.FirmwareVersion),
    };

    /// <summary>The firmware line's icon: Volco's own build is good, an update or another build
    /// is a warning, firmware this app can't talk to is a danger, a development build is neutral.</summary>
    public LinkTone FirmwareTone => _service.LastHello switch
    {
        null => LinkTone.Neutral,
        { Version: not ProtocolVersion.Current } => LinkTone.Danger,
        { FirmwareVersion: ControllerBannerLogic.DevFirmware } => LinkTone.Neutral,
        _ when Banner.FirmwareUpdateAvailable => LinkTone.Warning,
        { FirmwareVersion: "" } => LinkTone.Warning,
        var h when _firmware?.Catalog.BundledVersion == h.FirmwareVersion => LinkTone.Good,
        _ => LinkTone.Neutral,
    };

    public bool HasFirmwareText => FirmwareText.Length > 0;

    /// <summary>The firmware line shows its icon: an update, another build, or one this app can't use.</summary>
    public bool FirmwareNeedsAttention => FirmwareTone is LinkTone.Warning or LinkTone.Danger;

    public ControllerBanner Banner => _service.Banner;

    /// <summary>The banner's icon: reading is neutral, a setup to choose is a warning, a broken or
    /// unreadable one (or firmware that can't be used) is a danger.</summary>
    public LinkTone BannerTone => Banner.Kind switch
    {
        BannerKind.Reading => LinkTone.Neutral,
        BannerKind.DifferentSetup or BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror => LinkTone.Warning,
        _ => LinkTone.Danger,
    };

    /// <summary>The §8.3 banner text; null when there is none.</summary>
    public string? BannerText => Banner.Kind switch
    {
        BannerKind.Incompatible => "This controller's firmware is incompatible. Update it to use it.",
        BannerKind.Reading => "Reading the controller's setup…",
        BannerKind.DifferentSetup => "This controller has a different setup.",
        BannerKind.ReadFailed => "Couldn't read the controller's setup.",
        BannerKind.NotSetUpHasMirror or BannerKind.NotSetUp => "This controller isn't set up yet.",
        BannerKind.Unusable => "The controller's saved setup can't be used.",
        BannerKind.BrokenWithFirmware => "Your saved setup doesn't work with this firmware.",
        BannerKind.NoAnswer => $"The device on {_config.Config.Connection.Serial.ComPort} isn't answering as a Volco controller. " +
                               "It may not have Volco's firmware yet: check it, install it if needed, then set it up.",
        _ => null,
    };

    public bool HasBanner => BannerText is not null;
    private bool Has(BannerActions a) => Banner.Actions.HasFlag(a) && !Busy;
    public bool ShowUseControllers => Has(BannerActions.UseControllers);
    public bool ShowApplyMine => Has(BannerActions.ApplyMine);
    public bool ShowRetry => Has(BannerActions.Retry);
    public bool ShowSetUpFromScratch => Has(BannerActions.SetUpFromScratch);
    public bool ShowApplySaved => Has(BannerActions.ApplySaved);
    public bool ShowSetUp => Has(BannerActions.SetUp);
    public bool ShowFixIt => Has(BannerActions.FixIt);
    public bool ShowCheckFirmware => Has(BannerActions.CheckFirmware) && CanUseFirmware;

    /// <summary>Drives the sidebar dot and the tray.</summary>
    public bool FirmwareUpdateAvailable => Banner.FirmwareUpdateAvailable;

    [ObservableProperty] private string? _notice;
    public bool HasNotice => Notice is not null;
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    /// <summary>An operation runs, a test is on, or firmware is being written: every action that
    /// writes to the controller is disabled.</summary>
    internal bool Busy => _service.Operation != ConfigOperation.Idle || IsTesting || IsFirmwareRunning;

    [RelayCommand]
    private void UseControllers() => _ = ReadAndReport(_service.ReadAsync(adopt: true));

    [RelayCommand]
    private void Retry() => _ = ReadAndReport(_service.RetryReadAsync());

    [RelayCommand]
    private void ApplyMine()
    {
        if (_service.Mirror is { Length: > 0 } mirror) SaveBytes(mirror, udpChanged: false, layout: null);   // the mirror's own bytes
    }

    [RelayCommand]
    private void ApplySaved() => ApplyMine();

    [RelayCommand]
    private void SetUp() => StartNewDraft(new ControllerConfig { Preset = ControllerPresets.BlankId }, fixIt: false);

    [RelayCommand]
    private void SetUpFromScratch() => SetUp();

    [RelayCommand]
    private void FixIt()
    {
        if (DecodeMirror() is { } mirror) StartNewDraft(mirror, fixIt: true);
    }

    private async Task ReadAndReport(Task<ConfigReadResult> read)
    {
        var r = await read.ConfigureAwait(false);
        _ui.Post(() => { if (!_disposed && r.Status == ReadStatus.Ok) Notice = null; });
    }

    // ── The draft and the editing model (spec §8.4) ───────────────────────────────────────────

    /// <summary>The config the page edits. Rows edit its messages in place.</summary>
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

    /// <summary>The draft's problems, as marked on its rows; empty while it can't be edited.</summary>
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

    public bool CanSave => CanEdit && IsDirty && !Busy;

    /// <summary>The snackbar shows: a question, a save running or its result, or unsaved changes.</summary>
    public bool ShowFooter => IsDirty || IsSaving || ProgressText is not null || HasConfirmation;

    public ObservableCollection<HwSliderRowViewModel> Sliders { get; } = new();
    public ObservableCollection<HwButtonRowViewModel> Buttons { get; } = new();
    public ObservableCollection<I2cBusRowViewModel> Buses { get; } = new();
    public bool HasBuses => Buses.Count > 0;

    public IReadOnlyList<ControllerPreset> Presets => ControllerPresets.All;

    private ControllerPreset? _selectedPreset;

    /// <summary>The preset picker. Choosing one asks first when the draft has hardware.</summary>
    public ControllerPreset? SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (value is null || ReferenceEquals(value, _selectedPreset)) return;
            LoadPreset(value);
        }
    }

    /// <summary>Replaces the draft's wiring with <paramref name="preset"/>, asking first when the
    /// draft has hardware (the device card's "Load a preset…" and the preset picker).</summary>
    internal void LoadPreset(ControllerPreset preset)
    {
        if (!preset.IsAvailable || !CanEdit)
        {
            OnPropertyChanged(nameof(SelectedPreset));   // the combo goes back to the current one
            return;
        }
        if (ControllerDraft.HasHardware(_draft))
        {
            Ask($"Replace the current hardware with {preset.DisplayName}?", "Replace", () => { if (CanEdit) ApplyPreset(preset); });
            OnPropertyChanged(nameof(SelectedPreset));
        }
        else ApplyPreset(preset);
    }

    /// <summary>How Volco talks to the controller (the saved settings).</summary>
    internal ConnectionConfig ConnectionConfig => _config.Config.Connection;

    public string BasedOnText => ControllerDraft.BasedOn(_draft) is { } p && p.Id != ControllerPresets.BlankId
        ? (ControllerDraft.IsModified(_draft, p) ? $"Based on {p.DisplayName} (modified)" : $"Based on {p.DisplayName}")
        : "";

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
        Notice = null;
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
        _selectedPreset = ControllerDraft.BasedOn(draft);
        RebuildRows();
        Recompute(structural: true);
        OnPropertyChanged(nameof(SelectedPreset));
        OnPropertyChanged(string.Empty);
    }

    private void RebuildRows()
    {
        Sliders.Clear();
        for (int i = 0; i < _draft.Sliders.Count; i++) Sliders.Add(new HwSliderRowViewModel(this, i, _draft.Sliders[i]));
        Buttons.Clear();
        for (int i = 0; i < _draft.Buttons.Count; i++) Buttons.Add(new HwButtonRowViewModel(this, i, _draft.Buttons[i]));
        RebuildBuses();
    }

    private void RebuildBuses()
    {
        var used = ControllerDraft.UsedBuses(_draft);
        if (Buses.Select(b => b.Index).SequenceEqual(used)) return;
        Buses.Clear();
        foreach (var bus in used) Buses.Add(new I2cBusRowViewModel(this, bus));
        OnPropertyChanged(nameof(HasBuses));
    }

    /// <summary>After every user edit: the last save's message goes, then everything is recomputed.</summary>
    internal void OnDraftEdited(bool structural = false)
    {
        if (!IsSaving) SetProgress(null);
        _remoteError = null;
        Recompute(structural);
    }

    /// <summary>The (item, field) of the controller's last INVALID, shown until the next edit.</summary>
    private (byte Item, byte Field)? _remoteError;

    /// <summary>Dirty, errors, hints, the caption, the I2C rows.</summary>
    private void Recompute(bool structural = false)
    {
        if (structural) RebuildBuses();
        if (CanEdit) ShowLocalErrors();
        else ClearErrors();
        ShowHints();
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(IsSettingUp));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(BasedOnText));
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
        Sliders.Add(new HwSliderRowViewModel(this, _draft.Sliders.Count - 1, _draft.Sliders[^1]));
        OnDraftEdited();
    }

    [RelayCommand]
    private void AddButton()
    {
        if (!CanEdit) return;
        _draft.Buttons.Add(new Button());
        Layout.AddButton();
        Buttons.Add(new HwButtonRowViewModel(this, _draft.Buttons.Count - 1, _draft.Buttons[^1]));
        OnDraftEdited();
    }

    /// <summary>Removes slider <paramref name="index"/> from the draft. The later ones move down
    /// and keep what they control (spec §6.5); the view asks first.</summary>
    internal void RemoveSlider(int index)
    {
        if (!CanEdit || index < 0 || index >= _draft.Sliders.Count) return;
        _draft.Sliders.RemoveAt(index);
        Layout.RemoveSlider(index);
        RebuildRows();
        OnDraftEdited(structural: true);
    }

    /// <summary>Removes button <paramref name="index"/> from the draft. The later ones move down
    /// and keep their actions (spec §6.5); the view asks first.</summary>
    internal void RemoveButton(int index)
    {
        if (!CanEdit || index < 0 || index >= _draft.Buttons.Count) return;
        _draft.Buttons.RemoveAt(index);
        Layout.RemoveButton(index);
        RebuildRows();
        OnDraftEdited(structural: true);
    }

    /// <summary>Remove (spec §6.5): "Remove Slider N?", then <see cref="RemoveSlider"/> and
    /// <paramref name="removed"/>.</summary>
    internal void AskRemoveSlider(int index, Action? removed = null)
    {
        if (!CanEdit || index < 0 || index >= _draft.Sliders.Count) return;
        Ask($"Remove Slider {index + 1}?", "Remove", () => { RemoveSlider(index); removed?.Invoke(); });
    }

    /// <summary>Remove (spec §6.5): "Remove Button N?", then <see cref="RemoveButton"/> and
    /// <paramref name="removed"/>.</summary>
    internal void AskRemoveButton(int index, Action? removed = null)
    {
        if (!CanEdit || index < 0 || index >= _draft.Buttons.Count) return;
        Ask($"Remove Button {index + 1}?", "Remove", () => { RemoveButton(index); removed?.Invoke(); });
    }

    // ── Errors and hints ─────────────────────────────────────────────────────────────────────

    private void ClearErrors()
    {
        foreach (var r in AllRows()) r.ClearErrors();
        BehaviourError = null;
        WifiError = null;
        InputError = null;
        PageError = null;
        _problems = Array.Empty<DraftProblem>();
        _wifiFields.Clear();
        OnWifiFieldsChanged();
    }

    private IEnumerable<HardwareRowViewModel> AllRows()
        => Sliders.Cast<HardwareRowViewModel>().Concat(Buttons).Concat(Buses);

    /// <summary>The problems the save would stop on, shown on their rows (all of them, so both ends
    /// of a pin conflict are marked).</summary>
    private IReadOnlyList<DraftProblem> _problems = Array.Empty<DraftProblem>();

    private void ShowLocalErrors()
    {
        ClearErrors();
        var problems = DraftProblems.Find(_draft, ChipInfo.Esp32).ToList();
        if (problems.Count == 0 && _remoteError is { } r)
            problems.Add(new DraftProblem((ConfigField)r.Field, r.Item,
                $"The controller didn't accept {ControllerDraft.FieldDescription((ConfigField)r.Field)}."));
        _problems = problems;
        foreach (var p in problems) ShowError(p);
    }

    /// <summary>Puts a problem on the row or section its (item, field) points at.</summary>
    private void ShowError(DraftProblem p)
    {
        HardwareRowViewModel? row = p.Field switch
        {
            ConfigField.SliderPin => Sliders.ElementAtOrDefault(p.Item),
            ConfigField.ButtonPin or ConfigField.LedPin or ConfigField.PairPinA or ConfigField.PairPinB
                or ConfigField.ScreenBus or ConfigField.ScreenAddress => Buttons.ElementAtOrDefault(p.Item),
            ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock => Buses.FirstOrDefault(b => b.Index == p.Item),
            _ => null,
        };
        static string? Append(string? text, string line) => text is null ? line : text + "\n" + line;
        if (row is not null) row.AddError(p.Field, p.Message);
        else if (p.Field is ConfigField.UdpSsid or ConfigField.UdpPassword or ConfigField.UdpPcIp or ConfigField.UdpPort)
        {
            WifiError = Append(WifiError, p.Message);
            _wifiFields.Add(p.Field);
            OnWifiFieldsChanged();
            IsAdvancedOpen = true;   // the error must be visible
        }
        else if (p.Field == ConfigField.InputValue)
        {
            InputError = Append(InputError, p.Message);
            IsAdvancedOpen = true;
        }
        else if (p.Field == ConfigField.ScreenValue && ShowBehaviour) BehaviourError = Append(BehaviourError, p.Message);
        else PageError = Append(PageError, p.Message);   // counts, a row that isn't there, screen values with no screen shown
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

    [ObservableProperty] private string? _behaviourError;
    [ObservableProperty] private string? _wifiError;
    [ObservableProperty] private string? _inputError;
    [ObservableProperty] private string? _pageError;

    private void ShowHints()
    {
        foreach (var r in Sliders) r.HintText = null;
        foreach (var r in Buttons) r.HintText = null;
        foreach (var h in ControllerDraft.Hints(_draft, ChipInfo.Esp32))
        {
            if (h.Kind == DraftHintKind.SliderAdc2 && Sliders.ElementAtOrDefault(h.Index) is { } s)
                s.HintText = "This pin can't be read if you enable Wi-Fi.";
            if (h.Kind == DraftHintKind.ButtonNoPullUp && Buttons.ElementAtOrDefault(h.Index) is { } b)
                b.HintText = "This pin has no internal pull-up; add a resistor to 3.3 V.";
        }
    }

    // ── Save and Revert ──────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string? _progressText;
    [ObservableProperty] private bool _isSaving;
    partial void OnProgressTextChanged(string? value)
    {
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(FooterText));
        OnPropertyChanged(nameof(FooterTone));
        OnPropertyChanged(nameof(CanDismissFooter));
    }

    /// <summary>The snackbar's text: the save's progress or result, else what is unsaved.</summary>
    public string? FooterText => ProgressText
        ?? (IsSettingUp ? "This setup isn't on the controller yet." : IsDirty ? "Unsaved changes" : null);

    private LinkTone _progressTone = LinkTone.Neutral;

    /// <summary>The snackbar's icon: a confirmed save is good, a failed one a danger.</summary>
    public LinkTone FooterTone => ProgressText is null ? LinkTone.Neutral : _progressTone;

    /// <summary>A save's result can be closed (×); progress and unsaved changes can't.</summary>
    public bool CanDismissFooter => ProgressText is not null && !IsSaving;

    /// <summary>How long a successful save's message stays; failures stay until closed.</summary>
    internal TimeSpan ResultTimeout { get; set; } = TimeSpan.FromSeconds(5);

    partial void OnIsSavingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(CanDismissFooter));
        OnPropertyChanged(nameof(CanEdit));
        foreach (var r in AllRows()) r.RefreshCanEdit();
    }

    /// <summary>Shows a save's progress or result. A good result goes by itself after
    /// <see cref="ResultTimeout"/>, unless something replaced it meanwhile.</summary>
    private void SetProgress(string? text, LinkTone tone = LinkTone.Neutral, bool autoDismiss = false)
    {
        _progressTone = tone;
        ProgressText = text;
        OnPropertyChanged(nameof(FooterTone));
        if (!autoDismiss || text is null) return;
        var shown = ++_progressVersion;
        _ = Task.Delay(ResultTimeout).ContinueWith(_ => _ui.Post(() =>
        {
            if (!_disposed && shown == _progressVersion) DismissFooter();
        }), TaskScheduler.Default);
    }

    private int _progressVersion;

    /// <summary>The device card's save line (hardware-page spec §4.1) while a save runs; null otherwise.</summary>
    internal string? SavePhaseText => !IsSaving ? null
        : _service.Progress is { Phase: SavePhase.Confirming } ? "Restarting…" : "Saving to the controller…";

    /// <summary>The last operation's error (a save's, or a firmware install's, update's or check's),
    /// which the device card shows instead of its detail line until the next action; null for none.</summary>
    internal string? Failure => !IsSaving && ProgressText is not null && _progressTone == LinkTone.Danger ? ProgressText
        : !IsFirmwareRunning && FirmwareMessage is not null && FirmwareMessageTone is LinkTone.Danger or LinkTone.Warning ? FirmwareMessage
        : null;

    [RelayCommand]
    private void DismissFooter()
    {
        if (IsSaving) return;
        SetProgress(null);
    }

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
        SetProgress(text, confirmed ? LinkTone.Good : LinkTone.Danger, autoDismiss: confirmed);
        if (r.Status == SaveStatus.Rejected && r.Code == ConfigResultCode.Invalid) _remoteError = (r.Item, r.Field);
        if (r.Status == SaveStatus.Confirmed) _fixIt = false;
        if (r.Status == SaveStatus.Confirmed && _savingSetup) OfferTest = true;   // the device card offers a test
        var layout = _savingLayout;
        var saved = _savingBlob;
        _savingLayout = null;
        _savingBlob = null;
        if (r.Status == SaveStatus.Confirmed && layout is not null) ApplyLayout(layout, saved);
        RefreshAll();
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

    [RelayCommand]
    private void Revert()
    {
        Notice = null;
        SetProgress(null);
        _hasPermission = false;
        LoadDraftFromMirror();
        RefreshAll();
    }

    // ── Confirmations ────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private Confirmation? _confirmation;
    public bool HasConfirmation => Confirmation is not null;
    partial void OnConfirmationChanged(Confirmation? value)
    {
        OnPropertyChanged(nameof(HasConfirmation));
        OnPropertyChanged(nameof(ShowFooter));
    }

    /// <summary>Asks inline. The answer only acts on the draft it was asked about: a question that
    /// outlived its draft (Revert, a rebase, a new preset) does nothing.</summary>
    private void Ask(string message, string confirmLabel, Action onConfirm)
    {
        var askedAbout = _draft;
        Confirmation = new Confirmation(message, confirmLabel,
            () => { if (ReferenceEquals(askedAbout, _draft)) onConfirm(); },
            () => Confirmation = null);
    }

    /// <summary>A modal dialog covers the page: the Install confirmation or the test.</summary>
    public bool HasDialog => ConfirmInstallPending || IsTesting;

    /// <summary>Escape or a click outside the card: the dialog closes as its Cancel would.</summary>
    [RelayCommand]
    private void CloseDialog()
    {
        if (IsTesting) StopTest();
        else if (ConfirmInstallPending) ConfirmInstallPending = false;
    }

    // ── Behaviour (ScreenBehaviour) ──────────────────────────────────────────────────────────

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

    // ── Advanced: input tuning and Wi-Fi ─────────────────────────────────────────────────────

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

    /// <summary>The Advanced section is open (collapsed by default, spec §8.7).</summary>
    [ObservableProperty] private bool _isAdvancedOpen;

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

    // ── Test (spec §8.5): a dialog over the page ─────────────────────────────────────────────

    public ObservableCollection<TestSliderViewModel> TestSliders { get; } = new();
    public ObservableCollection<TestButtonViewModel> TestButtons { get; } = new();

    /// <summary>The test runs, and its dialog is open: closing the dialog ends it.</summary>
    [ObservableProperty] private bool _isTesting;

    public bool CanStartTest => !IsTesting && !IsFirmwareRunning && _runtime.ControllerState == ControllerState.Ready
                                && _service.Operation == ConfigOperation.Idle;

    partial void OnIsTestingChanged(bool value)
    {
        OnPropertyChanged(nameof(HasDialog));
        RefreshAll();
    }

    partial void OnIsFirmwareRunningChanged(bool value) => RefreshAll();

    [RelayCommand]
    private void StartTest()
    {
        if (!CanStartTest) return;
        RebuildTest();
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
        foreach (var b in TestButtons) b.Reset();
    }

    /// <summary>The page was left or the window hidden: the test ends (spec §8.5).</summary>
    public void OnPageLeft()
    {
        if (_testHandle is not null || IsTesting) StopTest();
        Confirmation = null;
        ClearFirmwareResult();
    }

    private void RebuildTest()
    {
        var info = _config.Config.Controller;
        TestSliders.Clear();
        for (int i = 0; i < (info?.Sliders ?? 0); i++) TestSliders.Add(new TestSliderViewModel(i));
        TestButtons.Clear();
        var kinds = info?.Buttons ?? new List<IndicatorKind>();
        for (int i = 0; i < kinds.Count; i++) TestButtons.Add(new TestButtonViewModel(i, kinds[i]));
    }

    // ── Runtime and service events (any thread → posted) ─────────────────────────────────────

    private void OnServiceChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var h = _service.LastHello;
        if (_hasPermission && (h is null ? _permission is not null : _permission != (h.Version, h.ConfigState, h.ConfigCrc)))
            _hasPermission = false;   // a different Hello ends a "new draft" permission
        if (_service.Progress is { Phase: SavePhase.Confirming } && IsSaving) SetProgress("Restarting…");
        RefreshAll();
    });

    private void OnMirrorChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        _hasPermission = false;
        var decoded = DecodeMirror();
        var newBaseline = decoded?.ToByteArray() ?? Array.Empty<byte>();
        if (!IsDirty || _draft.ToByteArray().AsSpan().SequenceEqual(newBaseline))
        {
            _baseline = newBaseline;
            _fixIt = false;
            SetDraft(decoded ?? new ControllerConfig { Preset = ControllerPresets.BlankId }, keepFixIt: false);
        }
        else
        {
            _baseline = newBaseline;
            Notice = "The controller's setup changed. Revert to load it.";
        }
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

    private void OnControllerInfoChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (!_disposed && !IsTesting) RebuildTest();
    });

    private void OnInTestModeChanged(object? sender, bool on) => _ui.Post(() =>
    {
        if (_disposed || on || _runtime.InTestMode) return;   // stale: a newer session is running
        _testHandle = null;   // the runtime ended it (Detach, replacement)
        IsTesting = false;
        foreach (var b in TestButtons) b.Reset();
    });

    private void OnRawInput(object? sender, RawInputEventArgs e) => _ui.Post(() =>
    {
        if (_disposed || !IsTesting) return;
        if (e.Kind == RawInputKind.Slider && TestSliders.ElementAtOrDefault(e.Index) is { } s) s.Value = e.Value;
        if (e.Kind == RawInputKind.Button && TestButtons.ElementAtOrDefault(e.Index) is { } b)
        {
            b.IsPressed = e.Value != 0;
            b.Presses = e.Presses;
        }
    });

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(FirmwareText));
        OnPropertyChanged(nameof(HasFirmwareText));
        OnPropertyChanged(nameof(FirmwareTone));
        OnPropertyChanged(nameof(FirmwareNeedsAttention));
        OnPropertyChanged(nameof(BannerTone));
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(HasBanner));
        foreach (var n in new[] { nameof(ShowUseControllers), nameof(ShowApplyMine), nameof(ShowRetry), nameof(ShowSetUpFromScratch),
                                  nameof(ShowApplySaved), nameof(ShowSetUp), nameof(ShowFixIt), nameof(FirmwareUpdateAvailable),
                                  nameof(ShowCheckFirmware), nameof(IsWaiting), nameof(ShowEditor), nameof(EditorPlaceholder),
                                  nameof(IsNotSetUp) })
            OnPropertyChanged(n);
    }

    private void RefreshAll()
    {
        // A pending question is only valid while the page could still act on it.
        if (Confirmation is not null && (!CanEdit || Busy)) Confirmation = null;
        RefreshStatus();
        OnPropertyChanged(nameof(InSync));
        OnPropertyChanged(nameof(CanEdit));
        OnPropertyChanged(nameof(CanStartTest));
        foreach (var r in AllRows()) r.RefreshCanEdit();
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
        _runtime.ControllerInfoChanged -= OnControllerInfoChanged;
        _runtime.InTestModeChanged -= OnInTestModeChanged;
        _runtime.RawInput -= OnRawInput;
        Connection.PropertyChanged -= OnConnectionEdited;
        if (_ownsConnection) Connection.Dispose();
    }
}
