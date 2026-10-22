using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Volco.App.Views;

/// <summary>Moving the focus into a modal card when it opens.</summary>
internal static class DialogFocus
{
    /// <summary>Once the card is laid out, focuses its first focusable control, else the card
    /// itself (so Escape works while nothing in it can take the focus).</summary>
    public static void MoveInto(Border card) => Dispatcher.UIThread.Post(() =>
    {
        if (!card.IsEffectivelyVisible || card.IsKeyboardFocusWithin) return;
        card.UpdateLayout();
        var first = card.GetVisualDescendants().OfType<InputElement>()
            .FirstOrDefault(c => c.Focusable && c.IsEffectivelyVisible && c.IsEffectivelyEnabled);
        if (first is not null) first.Focus(NavigationMethod.Tab);
        else card.Focus();
    });
}
