using Volco.Core.Config;
using Volco.Core.Media;

namespace Volco.Core.Communication;

/// <summary>
/// What a button shows for a moment after a press (custom-actions spec §11):
/// <see cref="ControllerDisplay.FeedbackSteps"/> steps of <see cref="ControllerDisplay.FeedbackStep"/>.
/// </summary>
/// <param name="Action">The action the press ran (one slot of the button's binding).</param>
/// <param name="Secondary">Not the action the button shows anyway: a screen shows it, an LED
/// blinks twice (<see cref="ControllerDisplay.ShowsWhenPressed"/>).</param>
/// <param name="Error">Why the press did nothing (<see cref="ControllerDisplay.NotRunning"/> and
/// the like), or null: a screen shows the error icon over it, an LED flashes fast.</param>
/// <param name="Step">The step since the press, from 0.</param>
/// <param name="ExpectFrom">Play/pause, until Windows reports the change: while the app still
/// plays (or is paused) as before the press, show <paramref name="Expect"/> instead.</param>
public sealed record PressFeedback(ButtonActionConfig Action, bool Secondary, string? Error, int Step,
                                   MediaPlayback? ExpectFrom = null, MediaPlayback? Expect = null);
