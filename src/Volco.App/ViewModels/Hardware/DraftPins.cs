using Volco.Core.Controller;
using Volco.Core.Controller.Proto;

namespace Volco.App.ViewModels.Hardware;

/// <summary>
/// One pin of a draft by its use (<see cref="PinOwner"/>): read it, write it, exchange two, and the
/// problem a pin field shows for it (hardware-page spec §6.3). Every pin field of the inspector goes
/// through these, so a slider's, a button's, a light's and a bus's pin all behave the same.
/// </summary>
public static class DraftPins
{
    /// <summary>The pin <paramref name="owner"/> uses, or null when it is not set or the control
    /// (or its light, or its bus) is not in the draft.</summary>
    public static uint? Get(ControllerConfig draft, PinOwner owner)
    {
        switch (owner.Kind)
        {
            case PinOwnerKind.Slider:
                return owner.Index < draft.Sliders.Count && draft.Sliders[owner.Index].HasPin ? draft.Sliders[owner.Index].Pin : null;
            case PinOwnerKind.BusSda:
            case PinOwnerKind.BusScl:
                if (ControllerDraft.Bus(draft, owner.Index) is not { } bus) return null;
                return owner.Kind == PinOwnerKind.BusSda ? bus.HasSda ? bus.Sda : null : bus.HasScl ? bus.Scl : null;
        }
        if (owner.Index >= draft.Buttons.Count) return null;
        var b = draft.Buttons[owner.Index];
        return owner.Kind switch
        {
            PinOwnerKind.Button => b.HasPin ? b.Pin : null,
            PinOwnerKind.Light => b.IndicatorCase == Button.IndicatorOneofCase.Led && b.Led.HasPin ? b.Led.Pin : null,
            PinOwnerKind.LightA => b.IndicatorCase == Button.IndicatorOneofCase.LedPair && b.LedPair.HasPinA ? b.LedPair.PinA : null,
            PinOwnerKind.LightB => b.IndicatorCase == Button.IndicatorOneofCase.LedPair && b.LedPair.HasPinB ? b.LedPair.PinB : null,
            _ => null,
        };
    }

    /// <summary>Sets (or, with null, clears) the pin of <paramref name="owner"/> in place. A light
    /// pin makes the button's light that kind; a bus pin adds the bus message. A control that is not
    /// in the draft is left alone.</summary>
    public static void Set(ControllerConfig draft, PinOwner owner, uint? pin)
    {
        switch (owner.Kind)
        {
            case PinOwnerKind.Slider:
                if (owner.Index >= draft.Sliders.Count) return;
                var s = draft.Sliders[owner.Index];
                if (pin is { } sp) s.Pin = sp; else s.ClearPin();
                return;
            case PinOwnerKind.BusSda:
            case PinOwnerKind.BusScl:
                if (owner.Index is < 0 or > 3) return;
                var bus = ControllerDraft.Bus(draft, owner.Index);
                if (bus is null)
                {
                    if (pin is null) return;
                    bus = new I2cBus();
                    ControllerDraft.SetBus(draft, owner.Index, bus);
                }
                if (owner.Kind == PinOwnerKind.BusSda) { if (pin is { } sda) bus.Sda = sda; else bus.ClearSda(); }
                else if (pin is { } scl) bus.Scl = scl; else bus.ClearScl();
                return;
        }
        if (owner.Index >= draft.Buttons.Count) return;
        var b = draft.Buttons[owner.Index];
        switch (owner.Kind)
        {
            case PinOwnerKind.Button:
                if (pin is { } bp) b.Pin = bp; else b.ClearPin();
                break;
            case PinOwnerKind.Light:
                if (b.IndicatorCase != Button.IndicatorOneofCase.Led) b.Led = new Led();
                if (pin is { } lp) b.Led.Pin = lp; else b.Led.ClearPin();
                break;
            case PinOwnerKind.LightA:
            case PinOwnerKind.LightB:
                if (b.IndicatorCase != Button.IndicatorOneofCase.LedPair) b.LedPair = new LedPair();
                if (owner.Kind == PinOwnerKind.LightA) { if (pin is { } a) b.LedPair.PinA = a; else b.LedPair.ClearPinA(); }
                else if (pin is { } pb) b.LedPair.PinB = pb; else b.LedPair.ClearPinB();
                break;
        }
    }

    /// <summary>Exchanges the pins of <paramref name="a"/> and <paramref name="b"/>.</summary>
    public static void Swap(ControllerConfig draft, PinOwner a, PinOwner b)
    {
        var pa = Get(draft, a);
        var pb = Get(draft, b);
        Set(draft, a, pb);
        Set(draft, b, pa);
    }

    /// <summary>What a pin of this use is chosen for.</summary>
    public static PinRole RoleOf(PinOwnerKind kind) => kind switch
    {
        PinOwnerKind.Slider => PinRole.SliderInput,
        PinOwnerKind.Button => PinRole.ButtonInput,
        PinOwnerKind.BusSda => PinRole.BusSda,
        PinOwnerKind.BusScl => PinRole.BusScl,
        _ => PinRole.LightOutput,
    };

    /// <summary>The field's label in the inspector (spec §6.1, §6.2, §6.4).</summary>
    public static string LabelOf(PinOwnerKind kind) => kind switch
    {
        PinOwnerKind.Slider => "Pin",
        PinOwnerKind.Button => "Button pin",
        PinOwnerKind.Light => "Light pin",
        PinOwnerKind.LightA => "Light A pin",
        PinOwnerKind.LightB => "Light B pin",
        PinOwnerKind.BusSda => "SDA",
        _ => "SCL",
    };

    /// <summary>The draft problems that are on <paramref name="owner"/>'s pin field: its
    /// <see cref="ConfigField"/> and item (a bus's SDA and SCL share the bus's I2C pin problems).</summary>
    public static IEnumerable<DraftProblem> ProblemsOf(IReadOnlyList<DraftProblem> problems, PinOwner owner)
        => problems.Where(p => p.Item == owner.Index && owner.Kind switch
        {
            PinOwnerKind.Slider => p.Field == ConfigField.SliderPin,
            PinOwnerKind.Button => p.Field == ConfigField.ButtonPin,
            PinOwnerKind.Light => p.Field == ConfigField.LedPin,
            PinOwnerKind.LightA => p.Field == ConfigField.PairPinA,
            PinOwnerKind.LightB => p.Field == ConfigField.PairPinB,
            _ => p.Field is ConfigField.I2cPins or ConfigField.I2cMissing,
        });

    /// <summary>
    /// The problem <paramref name="owner"/>'s pin field shows, and the other end when its pin is
    /// shared: the first other use of the same pin (in <see cref="PinChoices.Uses"/> order, so the
    /// owner the dropdown names) that has a problem too. A shared pin reads
    /// "Slider 1 also uses GPIO 33."; anything else is the problems' own words, one per line.
    /// Null when the field has no problem.
    /// </summary>
    public static (string? Message, PinOwner? Other) ProblemFor(ControllerConfig draft, IReadOnlyList<DraftProblem> problems, PinOwner owner)
    {
        var mine = ProblemsOf(problems, owner).Select(p => p.Message).Distinct().ToList();
        if (mine.Count == 0) return (null, null);
        if (Get(draft, owner) is { } pin)
        {
            var other = PinChoices.Uses(draft)
                .Where(u => u.Pin == pin && u.Owner != owner && ProblemsOf(problems, u.Owner).Any())
                .Select(u => u.Owner).FirstOrDefault();
            if (other is not null) return ($"{other.Label} also uses GPIO {pin}.", other);
        }
        return (string.Join("\n", mine), null);
    }
}
