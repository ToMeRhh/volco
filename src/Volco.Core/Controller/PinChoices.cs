using Volco.Core.Controller.Proto;

namespace Volco.Core.Controller;

/// <summary>What a pin is being chosen for.</summary>
public enum PinRole { SliderInput, ButtonInput, LightOutput, BusSda, BusScl }

/// <summary>Whether a pin can be picked: <c>Free</c>, <c>Taken</c> by another use in the draft, or
/// <c>Unusable</c> on this chip for the role.</summary>
public enum PinChoiceState { Free, Taken, Unusable }

/// <summary>What a pin is used for in a draft. <c>Index</c> is the slider or button index, or the bus number;
/// <c>Part</c> is unused for now.</summary>
public enum PinOwnerKind { Slider, Button, Light, LightA, LightB, BusSda, BusScl }

/// <summary>One use of a pin in the draft.</summary>
public sealed record PinOwner(PinOwnerKind Kind, int Index, int Part = 0)
{
    /// <summary>The use as a person reads it, numbered from 1 ("Button 2 light A", "I2C bus 0 SDA").</summary>
    public string Label => Kind switch
    {
        PinOwnerKind.Slider => $"Slider {Index + 1}",
        PinOwnerKind.Button => $"Button {Index + 1}",
        PinOwnerKind.Light => $"Button {Index + 1} light",
        PinOwnerKind.LightA => $"Button {Index + 1} light A",
        PinOwnerKind.LightB => $"Button {Index + 1} light B",
        PinOwnerKind.BusSda => $"I2C bus {Index} SDA",
        _ => $"I2C bus {Index} SCL",
    };
}

/// <summary>One row of the pin dropdown: the pin, its state, a short note and, when taken, who has it.</summary>
public sealed record PinChoice(uint Pin, PinChoiceState State, string Note, PinOwner? Owner);

/// <summary>Every pin of a chip with its state for one role in a draft (the pin dropdown's rows).</summary>
public static class PinChoices
{
    /// <summary>All pins the chip has, in order. <paramref name="self"/> is the use being edited: its own pin is free.
    /// Unusable wins over taken.</summary>
    public static IReadOnlyList<PinChoice> For(ControllerConfig draft, ChipInfo chip, PinRole role, PinOwner self)
    {
        var owners = OwnersOf(draft);
        var wifi = draft.EffectiveUdp().Enabled;
        var list = new List<PinChoice>();
        for (uint pin = 0; pin < 64; pin++)
        {
            if (!chip.Exists(pin)) continue;
            if (chip.ReservedReason(pin) is { } reserved)
                list.Add(new PinChoice(pin, PinChoiceState.Unusable, reserved, null));
            else if (role == PinRole.SliderInput && !chip.AdcCapable(pin))
                list.Add(new PinChoice(pin, PinChoiceState.Unusable, "no analog input", null));
            else if (role is PinRole.LightOutput or PinRole.BusSda or PinRole.BusScl && !chip.OutputCapable(pin))
                list.Add(new PinChoice(pin, PinChoiceState.Unusable, "input only", null));
            else if (owners.TryGetValue(pin, out var owner) && owner != self)
                list.Add(new PinChoice(pin, PinChoiceState.Taken, owner.Label, owner));
            else
            {
                var note = owner == self ? ""
                    : role == PinRole.ButtonInput && !chip.OutputCapable(pin) ? "no pull-up"
                    : role == PinRole.SliderInput && wifi && chip.IsAdc2(pin) ? "not with Wi-Fi"
                    : "free";
                list.Add(new PinChoice(pin, PinChoiceState.Free, note, null));
            }
        }
        return list;
    }

    private static Dictionary<uint, PinOwner> OwnersOf(ControllerConfig c)
    {
        var map = new Dictionary<uint, PinOwner>();
        void Add(bool has, uint pin, PinOwner owner)
        {
            if (has) map.TryAdd(pin, owner);
        }
        for (var i = 0; i < c.Sliders.Count; i++)
            Add(c.Sliders[i].HasPin, c.Sliders[i].Pin, new PinOwner(PinOwnerKind.Slider, i));
        for (var i = 0; i < c.Buttons.Count; i++)
        {
            var b = c.Buttons[i];
            Add(b.HasPin, b.Pin, new PinOwner(PinOwnerKind.Button, i));
            switch (b.IndicatorCase)
            {
                case Button.IndicatorOneofCase.Led:
                    Add(b.Led.HasPin, b.Led.Pin, new PinOwner(PinOwnerKind.Light, i));
                    break;
                case Button.IndicatorOneofCase.LedPair:
                    Add(b.LedPair.HasPinA, b.LedPair.PinA, new PinOwner(PinOwnerKind.LightA, i));
                    Add(b.LedPair.HasPinB, b.LedPair.PinB, new PinOwner(PinOwnerKind.LightB, i));
                    break;
            }
        }
        for (var bus = 0; bus < 4; bus++)
            if (ControllerDraft.Bus(c, bus) is { } i2c)
            {
                Add(i2c.HasSda, i2c.Sda, new PinOwner(PinOwnerKind.BusSda, bus));
                Add(i2c.HasScl, i2c.Scl, new PinOwner(PinOwnerKind.BusScl, bus));
            }
        return map;
    }
}
