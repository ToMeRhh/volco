using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace Volco.App.Views.Stage;

/// <summary>
/// A <see cref="TransitioningContentControl"/> for the stage's crossfaded parts (the title, the
/// title-row buttons, the side panel, the setup chooser; spec §3.3) that survives the stage being
/// taken out of the window and put back (General has no stage, so going there and back does that).
/// </summary>
/// <remarks>
/// Avalonia's control keeps its content in one of two presenters and crossfades between them. A
/// fade-out ends by setting the old presenter's <see cref="Visual.Opacity"/> to 0 as a local value
/// (the animation's forward fill). When the control is attached again it moves its content to the
/// other presenter without a transition, and that presenter is the one last faded out: the content
/// is there but drawn at opacity 0, until a later crossfade fades it in. A presenter that shows its
/// content without a transition must be opaque, so after the move the shown one's opacity is reset.
/// </remarks>
public class StageContentHost : TransitioningContentControl
{
    private ContentPresenter? _second;

    protected override Type StyleKeyOverride => typeof(TransitioningContentControl);

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);
        _second = e.NameScope.Find<ContentPresenter>("PART_ContentPresenter2");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);   // moves the content to the other presenter, with no transition
        foreach (var presenter in new[] { Presenter, _second })
            if (presenter is { IsVisible: true }) presenter.ClearValue(OpacityProperty);
    }
}
