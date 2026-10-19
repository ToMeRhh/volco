using Volco.Core.Communication;

namespace Volco.Core.Runtime;

/// <summary>
/// What a button shows for a moment that its config alone does not say: the feedback of a press
/// just made (an error flash, the pressed action, a Play/pause's expected state) and a mode
/// change's announce. The controller and the settings UI's previews both read it, through
/// <c>ControllerDisplay.For</c> on a binding.
/// </summary>
public interface IButtonFeedback
{
    /// <summary>The feedback <paramref name="button"/> shows now, or null.</summary>
    PressFeedback? FeedbackFor(int button);

    /// <summary>The announce step since the last mode change, or null when none is announced.</summary>
    int? AnnounceStep { get; }

    /// <summary>A button's feedback started, stepped or ended (its index), or an announce step
    /// passed (-1: every button). Any thread.</summary>
    event EventHandler<int>? FeedbackChanged;
}
