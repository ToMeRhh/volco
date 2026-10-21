using CommunityToolkit.Mvvm.Input;
using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels;

/// <summary>Onboarding: a controller with no setup stored on it (or one that can't be used) gets a
/// new draft from Volco's saved copy, a preset or nothing (the Controller page's setup chooser, spec
/// docs/design/hardware-page.md §7.1). Nothing is sent before Save.</summary>
public sealed partial class ControllerViewModel
{
    /// <summary>The controller has no setup stored (spec §8.3 "isn't set up" rows).</summary>
    public bool IsNotSetUp => Banner.Kind is BannerKind.NotSetUp or BannerKind.NotSetUpHasMirror;

    /// <summary>A setup is being made for a controller that doesn't hold it yet: whatever the draft
    /// is, it still has to be saved to the controller, even when it equals Volco's saved copy.</summary>
    public bool IsSettingUp => _hasPermission && !InSync;

    /// <summary>The draft was started by a "new draft" action (a chooser card, Set up, Fix it) and
    /// the Hello it was started under still holds.</summary>
    internal bool HasNewDraft => _hasPermission;

    /// <summary>The presets a setup can start from: the available ones, Blank left out (Start empty).</summary>
    public IReadOnlyList<ControllerPreset> SetupPresets { get; } =
        ControllerPresets.All.Where(p => p.IsAvailable && p.Id != ControllerPresets.BlankId).ToList();

    /// <summary>Volco's saved copy as a new draft, sent by Save like any other.</summary>
    [RelayCommand]
    private void SetupApplySaved()
    {
        if (DecodeMirror() is { } mirror) StartNewDraft(mirror, fixIt: false);
    }

    /// <summary>A new draft of <paramref name="preset"/>'s wiring.</summary>
    [RelayCommand]
    private void StartFromPreset(ControllerPreset? preset)
    {
        if (preset is not { IsAvailable: true }) return;
        StartNewDraft(ControllerPresets.ApplyTo(new ControllerConfig { Preset = ControllerPresets.BlankId }, preset), fixIt: false);
    }

    /// <summary>A new, empty draft.</summary>
    [RelayCommand]
    private void StartFromScratch() => StartNewDraft(new ControllerConfig { Preset = ControllerPresets.BlankId }, fixIt: false);
}
