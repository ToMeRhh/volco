using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Volco.Core.Communication;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels;

/// <summary>The Controller page's onboarding: a controller with no setup stored on it gets the
/// setup dialog (apply Volco's saved setup, start from a preset or from scratch), which hands over
/// to the editor; until the new setup is saved, the editor says what is left to do.</summary>
public sealed partial class ControllerViewModel
{
    /// <summary>The controller has no setup stored (spec §8.3 "isn't set up" rows).</summary>
    public bool IsNotSetUp => Banner.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror;

    /// <summary>A setup is being made for a controller that doesn't hold it yet: whatever the draft
    /// is, it still has to be saved to the controller, even when it equals Volco's saved copy.</summary>
    public bool IsSettingUp => _hasPermission && !InSync;

    [ObservableProperty] private bool _isSetupDialogOpen;

    /// <summary>The (version, state, crc) of the Hello the dialog last opened for: it opens once per
    /// not-set-up controller, and "Set up…" on the banner brings it back.</summary>
    private (byte, ControllerConfigState, uint)? _setupOfferedFor;

    /// <summary>Firmware was just installed: the dialog says that setting up is the next step.</summary>
    private bool _justInstalled;

    /// <summary>Volco has a saved setup it can apply.</summary>
    public bool SetupHasSavedSetup => Banner.Actions.HasFlag(BannerActions.ApplySaved);

    public string SetupIntro => (_justInstalled ? "The firmware is installed. " : "")
        + (SetupHasSavedSetup
            ? "This controller has no setup stored on it yet. Volco still has the setup you saved before: apply it, or start a new one."
            : "This controller has no setup stored on it yet. Tell Volco how it's wired: start from a preset that matches your build, or from scratch.");

    public IReadOnlyList<ControllerPreset> SetupPresets { get; } =
        ControllerPresets.All.Where(p => p.IsAvailable && p.Id != ControllerPresets.BlankId).ToList();

    [ObservableProperty] private ControllerPreset? _setupPreset;

    public bool ShowOpenSetup => IsNotSetUp && !IsSetupDialogOpen && !_hasPermission && !Busy;

    public string SetupGuideText => "Setting up this controller: set each pin to match your wiring, then Save to controller. "
                                    + "After that, try it with Test controller.";

    partial void OnIsSetupDialogOpenChanged(bool value)
    {
        OnPropertyChanged(nameof(HasDialog));
        OnPropertyChanged(nameof(ShowOpenSetup));
    }

    [RelayCommand]
    private void OpenSetup()
    {
        if (!IsNotSetUp || Busy) return;
        SetupPreset ??= SetupPresets.FirstOrDefault();
        OnPropertyChanged(nameof(SetupIntro));
        OnPropertyChanged(nameof(SetupHasSavedSetup));
        IsSetupDialogOpen = true;
    }

    [RelayCommand]
    private void CloseSetup()
    {
        IsSetupDialogOpen = false;
        _justInstalled = false;
    }

    [RelayCommand]
    private void SetupApplySaved()
    {
        CloseSetup();
        ApplySaved();
    }

    [RelayCommand]
    private void StartFromPreset()
    {
        if (SetupPreset is not { IsAvailable: true } preset) return;
        CloseSetup();
        StartNewDraft(ControllerPresets.ApplyTo(new ControllerConfig { Preset = ControllerPresets.BlankId }, preset), fixIt: false);
    }

    [RelayCommand]
    private void StartFromScratch()
    {
        CloseSetup();
        SetUp();
    }

    /// <summary>After every refresh: opens the dialog for a controller that newly turned out not to
    /// be set up, and closes it once that no longer holds or something else is running.</summary>
    private void UpdateSetupDialog()
    {
        var h = _service.LastHello;
        if (IsSetupDialogOpen && (!IsNotSetUp || Busy)) IsSetupDialogOpen = false;
        if (h is null)
        {
            _setupOfferedFor = null;   // unplugged or replaced: a controller that comes back is asked again
            return;
        }
        var key = (h.Version, h.ConfigState, h.ConfigCrc);
        if (IsNotSetUp && !Busy && !_hasPermission && _setupOfferedFor != key)
        {
            _setupOfferedFor = key;
            OpenSetup();
        }
    }

    /// <summary>An Install succeeded: the controller comes back with no setup, and the dialog opens
    /// for it even if it was dismissed for the same (empty) state before.</summary>
    private void OnInstalled()
    {
        _justInstalled = true;
        _setupOfferedFor = null;
        OnPropertyChanged(nameof(SetupIntro));
    }
}
