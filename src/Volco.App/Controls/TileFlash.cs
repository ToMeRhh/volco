using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Volco.App.ViewModels;

namespace Volco.App.Controls;

/// <summary>
/// A tile's flash when the controller reports its control (spec §4.1): the "flash" class on the
/// tile's root while the animation runs, then off again. It plays only on a report made while the
/// tile is on screen. A tile that appears (the Controller page opened again) never flashes for a
/// report made earlier, which a bound class would replay, because the item stays active.
/// </summary>
internal sealed class TileFlash
{
    /// <summary>The animation's length (VolcoStyles, <c>Panel.TileRoot.flash</c>).</summary>
    public static TimeSpan Duration { get; } = TimeSpan.FromMilliseconds(600);

    private readonly Control _tile;
    private readonly Panel _root;
    private INotifyPropertyChanged? _item;
    private IDisposable? _off;

    private TileFlash(Control tile, Panel root)
    {
        _tile = tile;
        _root = root;
        // Followed only while on screen, so a tile the page dropped holds no handler on its item.
        tile.DataContextChanged += (_, _) => Follow(Attached ? tile.DataContext as INotifyPropertyChanged : null);
        tile.AttachedToVisualTree += (_, _) => Follow(tile.DataContext as INotifyPropertyChanged);
        tile.DetachedFromVisualTree += (_, _) => Follow(null);
        Follow(Attached ? tile.DataContext as INotifyPropertyChanged : null);
    }

    /// <summary>Flashes <paramref name="root"/> when <paramref name="tile"/>'s item
    /// (<see cref="SliderItemViewModel"/> or <see cref="ButtonItemViewModel"/>) is pulsed.</summary>
    public static void Attach(Control tile, Panel root) => _ = new TileFlash(tile, root);

    private bool Attached => TopLevel.GetTopLevel(_tile) is not null;

    private void Follow(INotifyPropertyChanged? item)
    {
        if (_item is not null) _item.PropertyChanged -= OnItemChanged;
        _item = item;
        if (item is not null) item.PropertyChanged += OnItemChanged;
        Stop();
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != "IsActive" || !IsActive(sender) || !Attached) return;
        // Off and on again: a report during a flash starts it over.
        _off?.Dispose();
        _root.Classes.Remove("flash");
        _root.Classes.Add("flash");
        _off = DispatcherTimer.RunOnce(Stop, Duration);
    }

    private static bool IsActive(object? item)
        => item is SliderItemViewModel { IsActive: true } or ButtonItemViewModel { IsActive: true };

    private void Stop()
    {
        _off?.Dispose();
        _off = null;
        _root.Classes.Remove("flash");
    }
}
