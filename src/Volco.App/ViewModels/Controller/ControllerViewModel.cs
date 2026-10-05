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

    public ControllerViewModel(ConfigManager config, VolcoRuntime runtime, IUiDispatcher ui,
                               FirmwareService? firmware = null)
    {
        _config = config;
        _runtime = runtime;
        _service = runtime.ControllerConfig;
        _ui = ui;

        _service.BannerChanged += OnServiceChanged;
        _service.OperationChanged += OnServiceChanged;
        _service.MirrorChanged += OnMirrorChanged;
        _runtime.ControllerStateChanged += OnControllerStateChanged;
        _runtime.ControllerSilentChanged += OnControllerSilentChanged;
        _runtime.ControllerInfoChanged += OnControllerInfoChanged;
        _runtime.InTestModeChanged += OnInTestModeChanged;
        _runtime.RawInput += OnRawInput;

        InitFirmware(firmware);
        LoadDraftFromMirror();
        RebuildTest();
        RefreshStatus();
    }

    // ── Status and banner ────────────────────────────────────────────────────────────────────

    public string StatusText => _runtime.ControllerState switch
    {
        ControllerState.Ready => "Connected",
        ControllerState.AwaitingHello when _runtime.ControllerSilent => "No controller answering",
        ControllerState.AwaitingHello => "Waiting for the controller…",
        ControllerState.VersionMismatch => "Firmware mismatch",
        _ => "Not connected",
    };

    /// <summary>The port is open and the first seconds of waiting for a Hello are running.</summary>
    public bool IsWaiting => _runtime.ControllerState == ControllerState.AwaitingHello && !_runtime.ControllerSilent;

    /// <summary>Hardware, Test, Screens and Advanced need a controller that answers.</summary>
    public bool ShowEditor => _runtime.ControllerState == ControllerState.Ready;

    public string EditorPlaceholder => _runtime.ControllerState switch
    {
        ControllerState.VersionMismatch => "Update the controller's firmware to set it up.",
        ControllerState.AwaitingHello when _runtime.ControllerSilent => "Check the controller's firmware above, then set it up here.",
        ControllerState.AwaitingHello => "Waiting for the controller…",
        _ => "Connect the controller to set it up. Choose its port on the Connection page.",
    };

    public string ChipText => _service.LastHello?.Chip == ChipFamily.Esp32 ? "ESP32" : _service.LastHello is null ? "" : "Unknown";

    public string FirmwareText => _service.LastHello switch
    {
        null => "",
        { FirmwareVersion: ControllerBannerLogic.DevFirmware } => "Development firmware",
        { FirmwareVersion: "" } => "Unknown firmware",
        var h => FirmwareVersionText(h.FirmwareVersion),
    };

    public ControllerBanner Banner => _service.Banner;

    /// <summary>The §8.3 banner text; null when there is none.</summary>
    public string? BannerText => Banner.Kind switch
    {
        BannerKind.Incompatible => "This controller's firmware is incompatible. Update it to use it.",
        BannerKind.Reading => "Reading the controller's setup…",
        BannerKind.DifferentSetup => "This controller has a different setup.",
        BannerKind.ReadFailed => "Couldn't read the controller's setup.",
        BannerKind.NotSetUpHasMirror => "This controller isn't set up.",
        BannerKind.NotSetUp => "Set up your controller.",
        BannerKind.Unusable => "The controller's saved setup can't be used.",
        BannerKind.BrokenWithFirmware => "Your saved setup doesn't work with this firmware.",
        BannerKind.NoAnswer => $"No Volco controller answered on {_config.Config.Connection.Serial.ComPort}. " +
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

    /// <summary>Drives the sidebar dot (and in stage 6 the tray).</summary>
    public bool FirmwareUpdateAvailable => Banner.FirmwareUpdateAvailable;

    [ObservableProperty] private string? _notice;
    public bool HasNotice => Notice is not null;
    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    /// <summary>An operation runs, a test is on, or firmware is being written: every action that
    /// writes to the controller is disabled.</summary>
    private bool Busy => _service.Operation != ConfigOperation.Idle || IsTesting || IsFirmwareRunning;

    [RelayCommand]
    private void UseControllers() => _ = ReadAndReport(_service.ReadAsync(adopt: true));

    [RelayCommand]
    private void Retry() => _ = ReadAndReport(_service.RetryReadAsync());

    [RelayCommand]
    private void ApplyMine()
    {
        if (_service.Mirror is { Length: > 0 } mirror) SaveBytes(mirror, udpChanged: false);   // the mirror's own bytes
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

    /// <summary>Hello configState 1 with the mirror's CRC.</summary>
    public bool InSync =>
        _service.LastHello is { Version: ProtocolVersion.Current, ConfigState: ControllerConfigState.Stored } h
        && _service.Mirror is { Length: > 0 } m && h.ConfigCrc == ControllerConfigCodec.Crc32(m);

    public bool CanEdit => _runtime.ControllerState == ControllerState.Ready && (InSync || _hasPermission);

    public bool IsDirty => _fixIt || !_draft.ToByteArray().AsSpan().SequenceEqual(_baseline);

    public bool CanSave => CanEdit && IsDirty && !Busy;

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
            if (!value.IsAvailable || !CanEdit)
            {
                OnPropertyChanged();   // the combo goes back to the current one
                return;
            }
            if (ControllerDraft.HasHardware(_draft))
            {
                Ask($"Replace the current hardware with {value.DisplayName}?", "Replace", () => { if (CanEdit) ApplyPreset(value); });
                OnPropertyChanged();
            }
            else ApplyPreset(value);
        }
    }

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

    private ControllerConfig? DecodeMirror()
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
        ProgressText = null;
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
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(ShowFooter));
        OnPropertyChanged(nameof(BasedOnText));
        OnPropertyChanged(nameof(ShowBehaviour));
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void AddSlider()
    {
        if (!CanEdit) return;
        _draft.Sliders.Add(new Slider());
        Sliders.Add(new HwSliderRowViewModel(this, _draft.Sliders.Count - 1, _draft.Sliders[^1]));
        OnDraftEdited();
    }

    [RelayCommand]
    private void AddButton()
    {
        if (!CanEdit) return;
        _draft.Buttons.Add(new Button());
        Buttons.Add(new HwButtonRowViewModel(this, _draft.Buttons.Count - 1, _draft.Buttons[^1]));
        OnDraftEdited();
    }

    internal void RequestRemoveSlider(int index)
    {
        if (!CanEdit || index >= _draft.Sliders.Count) return;
        void Remove()
        {
            if (!CanEdit || index >= _draft.Sliders.Count) return;
            _draft.Sliders.RemoveAt(index);
            RebuildRows();
            OnDraftEdited(structural: true);
        }
        if (index < _draft.Sliders.Count - 1)
            Ask("Sliders after it are renumbered, and their actions move with the number.", "Remove", Remove);
        else Remove();
    }

    internal void RequestRemoveButton(int index)
    {
        if (!CanEdit || index >= _draft.Buttons.Count) return;
        void Remove()
        {
            if (!CanEdit || index >= _draft.Buttons.Count) return;
            _draft.Buttons.RemoveAt(index);
            RebuildRows();
            OnDraftEdited(structural: true);
        }
        if (index < _draft.Buttons.Count - 1)
            Ask("Buttons after it are renumbered, and their actions move with the number.", "Remove", Remove);
        else Remove();
    }

    // ── Errors and hints ─────────────────────────────────────────────────────────────────────

    private void ClearErrors()
    {
        foreach (var r in AllRows()) r.ErrorText = null;
        BehaviourError = null;
        WifiError = null;
        InputError = null;
        PageError = null;
    }

    private IEnumerable<HardwareRowViewModel> AllRows()
        => Sliders.Cast<HardwareRowViewModel>().Concat(Buttons).Concat(Buses);

    private void ShowLocalErrors()
    {
        ClearErrors();
        var v = ControllerConfigValidator.Validate(_draft, ChipInfo.Esp32);
        if (!v.IsOk) ShowError(v.Item, v.Field);
        else if (_remoteError is { } r) ShowError(r.Item, r.Field);
    }

    /// <summary>Puts "&lt;field&gt; isn't valid" on the row (item, field) points at.</summary>
    private void ShowError(byte item, byte field)
    {
        var text = $"Check {ControllerDraft.FieldDescription((ConfigField)field)}.";
        HardwareRowViewModel? row = (ConfigField)field switch
        {
            ConfigField.SliderPin => Sliders.ElementAtOrDefault(item),
            ConfigField.ButtonPin or ConfigField.LedPin or ConfigField.PairPinA or ConfigField.PairPinB
                or ConfigField.ScreenBus or ConfigField.ScreenAddress => Buttons.ElementAtOrDefault(item),
            ConfigField.I2cMissing or ConfigField.I2cPins or ConfigField.I2cClock => Buses.FirstOrDefault(b => b.Index == item),
            _ => null,
        };
        var described = $"Check {ControllerDraft.Describe(item, field)}.";
        if (row is not null) row.ErrorText = text;
        else if ((ConfigField)field is ConfigField.UdpSsid or ConfigField.UdpPassword or ConfigField.UdpPcIp or ConfigField.UdpPort)
        {
            WifiError = text;
            IsAdvancedOpen = true;   // the error must be visible
        }
        else if ((ConfigField)field == ConfigField.InputValue)
        {
            InputError = described;
            IsAdvancedOpen = true;
        }
        else if ((ConfigField)field == ConfigField.ScreenValue && ShowBehaviour) BehaviourError = described;
        else PageError = described;   // counts, a row that isn't there, screen values with no screen shown
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
    partial void OnProgressTextChanged(string? value) => OnPropertyChanged(nameof(ShowFooter));
    partial void OnIsSavingChanged(bool value) => OnPropertyChanged(nameof(ShowFooter));

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (!CanSave) return;
        var v = ControllerConfigValidator.Validate(_draft, ChipInfo.Esp32);
        if (!v.IsOk)
        {
            ShowLocalErrors();
            ProgressText = $"Fix {ControllerDraft.Describe(v.Item, v.Field)} first.";
            return;
        }
        bool udpChanged = !UdpBytes(_draft).AsSpan().SequenceEqual(UdpBytes(BaselineConfig()));
        if (udpChanged && _config.Config.Connection.Type == ConnectionType.Udp)
            Ask("Changing the Wi-Fi settings may disconnect the controller. If it doesn't come back, connect it with USB.",
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
            ProgressText = $"This setup can't be saved: {ex.Message}";
            return;
        }
        SaveBytes(blob, udpChanged);
    }

    private ControllerConfig BaselineConfig()
    {
        try { return _baseline.Length > 0 ? ControllerConfig.Parser.ParseFrom(_baseline) : new ControllerConfig(); }
        catch (InvalidProtocolBufferException) { return new ControllerConfig(); }
    }

    private static byte[] UdpBytes(ControllerConfig c) => c.Udp?.ToByteArray() ?? Array.Empty<byte>();

    private void SaveBytes(byte[] blob, bool udpChanged)
    {
        _udpChangedInSave = udpChanged;
        IsSaving = true;
        ProgressText = "Sending…";
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
        ProgressText = r.Status switch
        {
            SaveStatus.Confirmed => "Saved. The controller restarted with the new setup.",
            SaveStatus.Rejected when r.Code == ConfigResultCode.Invalid
                => $"The controller rejected the setup: {ControllerDraft.Describe(r.Item, r.Field)}.",
            SaveStatus.Rejected when r.Code == ConfigResultCode.StorageFailed
                => "The controller couldn't save the setup. Its previous setup is unchanged.",
            SaveStatus.Rejected => "The controller couldn't read the setup. Update the firmware and try again.",
            SaveStatus.NotRunningNewSetup => "The controller restarted but isn't running the new setup.",
            SaveStatus.DidNotComeBack => _udpChangedInSave
                ? "The controller didn't come back after restarting. If you changed the Wi-Fi settings, connect with USB to check them."
                : "The controller didn't come back after restarting.",
            SaveStatus.NoAnswer => "The controller didn't answer.",
            SaveStatus.ConnectionLost => "Connection lost while saving. Reconnect to see which setup the controller has.",
            SaveStatus.Busy => "Another operation is running. Try again in a moment.",
            _ => null,
        };
        if (r.Status == SaveStatus.Rejected && r.Code == ConfigResultCode.Invalid) _remoteError = (r.Item, r.Field);
        if (r.Status == SaveStatus.Confirmed) _fixIt = false;
        RefreshAll();
    }

    [RelayCommand]
    private void Revert()
    {
        Notice = null;
        ProgressText = null;
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

    // ── Behaviour (ScreenBehaviour) ──────────────────────────────────────────────────────────

    private ScreenBehaviour S => _draft.EffectiveScreens();
    private void EditS(Action<ScreenBehaviour> edit, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!CanEdit) { OnPropertyChanged(name); return; }
        _draft.Screens ??= new ScreenBehaviour();
        edit(_draft.Screens);
        OnPropertyChanged(name);
        OnDraftEdited();
    }

    public decimal Brightness { get => S.Brightness; set => EditS(s => s.Brightness = (uint)value); }
    public bool DimEnabled { get => S.DimEnabled; set => EditS(s => s.DimEnabled = value); }
    public decimal DimAfterS { get => S.DimAfterS; set => EditS(s => s.DimAfterS = (uint)value); }
    public decimal DimBrightness { get => S.DimBrightness; set => EditS(s => s.DimBrightness = (uint)value); }
    public bool OffEnabled { get => S.OffEnabled; set => EditS(s => s.OffEnabled = value); }
    public decimal OffAfterS { get => S.OffAfterS; set => EditS(s => s.OffAfterS = (uint)value); }
    public bool PixelShiftEnabled { get => S.PixelShiftEnabled; set => EditS(s => s.PixelShiftEnabled = value); }
    public decimal PixelShiftEveryS { get => S.PixelShiftEveryS; set => EditS(s => s.PixelShiftEveryS = (uint)value); }
    public bool WakeSwallowsPress { get => S.WakeSwallowsPress; set => EditS(s => s.WakeSwallowsPress = value); }
    public decimal ScrollHoldMs { get => S.ScrollHoldMs; set => EditS(s => s.ScrollHoldMs = (uint)value); }
    public decimal ScrollSpeed { get => S.ScrollSpeedPxS; set => EditS(s => s.ScrollSpeedPxS = (uint)value); }
    public decimal ScrollEndHoldMs { get => S.ScrollEndHoldMs; set => EditS(s => s.ScrollEndHoldMs = (uint)value); }
    public decimal TransitionMs { get => S.TransitionMs; set => EditS(s => s.TransitionMs = (uint)value); }

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

    public decimal DebounceMs { get => In.DebounceMs; set => EditIn(i => i.DebounceMs = (uint)value); }
    public decimal SliderIntervalMs { get => In.SliderIntervalMs; set => EditIn(i => i.SliderIntervalMs = (uint)value); }
    public decimal SmoothingPct { get => In.FilterWeightPct; set => EditIn(i => i.FilterWeightPct = (uint)value); }
    public decimal Deadband { get => In.SliderDeadband; set => EditIn(i => i.SliderDeadband = (uint)value); }

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

    public decimal PcPort { get => U.PcPort; set => EditU(u => u.PcPort = (uint)value); }
    public decimal LocalPort { get => U.LocalPort; set => EditU(u => u.LocalPort = (uint)value); }

    // ── Test (spec §8.5) ─────────────────────────────────────────────────────────────────────

    public ObservableCollection<TestSliderViewModel> TestSliders { get; } = new();
    public ObservableCollection<TestButtonViewModel> TestButtons { get; } = new();

    [ObservableProperty] private bool _isTesting;

    public bool CanStartTest => !IsTesting && !IsFirmwareRunning && _runtime.ControllerState == ControllerState.Ready
                                && _service.Operation == ConfigOperation.Idle;

    partial void OnIsTestingChanged(bool value) => RefreshAll();

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
        foreach (var b in TestButtons) b.ResetIndicator();
    }

    /// <summary>The page was left or the window hidden: the test ends (spec §8.5).</summary>
    public void OnPageLeft()
    {
        if (_testHandle is not null || IsTesting) StopTest();
        Confirmation = null;
    }

    private void RebuildTest()
    {
        var info = _config.Config.Controller;
        TestSliders.Clear();
        for (int i = 0; i < (info?.Sliders ?? 0); i++) TestSliders.Add(new TestSliderViewModel(i));
        TestButtons.Clear();
        var kinds = info?.Buttons ?? new List<IndicatorKind>();
        for (int i = 0; i < kinds.Count; i++) TestButtons.Add(new TestButtonViewModel(i, kinds[i], _runtime.SetTestIndicator));
    }

    // ── Runtime and service events (any thread → posted) ─────────────────────────────────────

    private void OnServiceChanged(object? sender, EventArgs e) => _ui.Post(() =>
    {
        if (_disposed) return;
        var h = _service.LastHello;
        if (_hasPermission && (h is null ? _permission is not null : _permission != (h.Version, h.ConfigState, h.ConfigCrc)))
            _hasPermission = false;   // a different Hello ends a "new draft" permission
        if (_service.Progress is { Phase: SavePhase.Confirming } && IsSaving) ProgressText = "Restarting…";
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
        if (!_disposed) RefreshAll();
    });

    private void OnControllerSilentChanged(object? sender, bool silent) => _ui.Post(() =>
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
        foreach (var b in TestButtons) b.ResetIndicator();
    });

    private void OnRawInput(object? sender, RawInputEventArgs e) => _ui.Post(() =>
    {
        if (_disposed || !IsTesting) return;
        if (e.Kind == RawInputKind.Slider && TestSliders.ElementAtOrDefault(e.Index) is { } s) s.Value = e.Value;
        if (e.Kind == RawInputKind.Button && TestButtons.ElementAtOrDefault(e.Index) is { } b) b.IsPressed = e.Value != 0;
    });

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(FirmwareText));
        OnPropertyChanged(nameof(Banner));
        OnPropertyChanged(nameof(BannerText));
        OnPropertyChanged(nameof(HasBanner));
        foreach (var n in new[] { nameof(ShowUseControllers), nameof(ShowApplyMine), nameof(ShowRetry), nameof(ShowSetUpFromScratch),
                                  nameof(ShowApplySaved), nameof(ShowSetUp), nameof(ShowFixIt), nameof(FirmwareUpdateAvailable),
                                  nameof(ShowCheckFirmware), nameof(IsWaiting), nameof(ShowEditor), nameof(EditorPlaceholder) })
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
        _runtime.ControllerSilentChanged -= OnControllerSilentChanged;
        _runtime.ControllerInfoChanged -= OnControllerInfoChanged;
        _runtime.InTestModeChanged -= OnInTestModeChanged;
        _runtime.RawInput -= OnRawInput;
    }
}
