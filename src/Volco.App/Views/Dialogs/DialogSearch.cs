using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Volco.App.ViewModels.Search;

namespace Volco.App.Views.Dialogs;

/// <summary>The search box shared by the Controls page's dialogs: it has the focus on open, and in
/// it Down and Up move through the list and Enter picks (an <see cref="ISearchList"/> DataContext).</summary>
internal static class DialogSearch
{
    public static void Attach(UserControl dialog, TextBox search)
    {
        search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (dialog.DataContext is not ISearchList list) return;
            switch (e.Key)
            {
                case Key.Down:
                    list.MoveSelection(+1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    list.MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    list.PickCurrent();
                    e.Handled = true;
                    break;
            }
        }, RoutingStrategies.Tunnel);
        dialog.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => search.Focus(NavigationMethod.Tab));
    }
}
