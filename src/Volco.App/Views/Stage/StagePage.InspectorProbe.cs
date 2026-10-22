using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Volco.App.ViewModels;
using Volco.App.Views.Inspector;

namespace Volco.App.Views.Stage;

/// <summary>
/// <see cref="InspectorTopHeight"/> (controller-page spec §4.3): each drawn tile is measured on the
/// one unseen probe inspector in turn, its sub-section open, and the tallest sets the height. The
/// probe is emptied afterwards, so the page carries no copy of an inspector: a page that holds one
/// per tile takes several times longer to show again (every control's styles are applied anew when
/// it returns). A tile is measured again only when something that can change its height changes,
/// and every tile when the drawing or the side panel's width does.
/// </summary>
public partial class StagePage
{
    // What a tile's Controls or Actions section shows that can take another line or row.
    private static readonly HashSet<string> SliderLayout = new()
    {
        nameof(SliderItemViewModel.DisplayHeading), nameof(SliderItemViewModel.TargetLabel),
        nameof(SliderItemViewModel.HasAppIcon), nameof(SliderItemViewModel.IsNotSetUp),
    };
    private static readonly HashSet<string> ButtonLayout = new()
    {
        nameof(ButtonItemViewModel.DisplayHeading), nameof(ButtonItemViewModel.KindText),
        nameof(ButtonItemViewModel.HasLightRow), nameof(ButtonItemViewModel.LightRowLabel),
        nameof(ButtonItemViewModel.LightRowText), nameof(ButtonItemViewModel.IsScreen),
    };
    private static readonly HashSet<string> SlotLayout = new()
    {
        nameof(ActionSlotViewModel.RowText), nameof(ActionSlotViewModel.IsUsb),
        nameof(ActionSlotViewModel.HasProblem), nameof(ActionSlotViewModel.CanTest),
    };

    private ContentControl _probe = null!;
    private readonly Dictionary<object, double> _probeHeights = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> _probePending = new();
    // The drawn tiles and the action slots watched for a change of height, each to its tile.
    private readonly Dictionary<INotifyPropertyChanged, object> _probeWatched = new(ReferenceEqualityComparer.Instance);
    private double _probeWidth;
    private bool _probeScheduled;

    /// <summary>How many tile measurements the probe has made (tests: showing the page again makes none).</summary>
    internal int ProbeMeasurements { get; private set; }

    private void InitInspectorProbe()
    {
        _probe = this.FindControl<ContentControl>("InspectorProbe")!;
        _sidePanel.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty) OnProbeWidthChanged();
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ScheduleProbe();   // a measure that waited for the page to show
    }

    private IEnumerable<object> DrawnTiles()
        => (_sliderList.ItemsSource ?? Array.Empty<object>()).Cast<object>()
           .Concat((_buttonList.ItemsSource ?? Array.Empty<object>()).Cast<object>());

    /// <summary>The drawn tiles changed: a new one is measured, a gone one forgotten.</summary>
    private void ProbeTiles()
    {
        var tiles = DrawnTiles().ToHashSet(ReferenceEqualityComparer.Instance);
        foreach (var gone in _probeHeights.Keys.Where(t => !tiles.Contains(t)).ToList()) _probeHeights.Remove(gone);
        _probePending.RemoveAll(t => !tiles.Contains(t));
        foreach (var (source, tile) in _probeWatched.Where(w => !tiles.Contains(w.Value)).ToList())
        {
            source.PropertyChanged -= OnProbedTileChanged;
            _probeWatched.Remove(source);
        }
        foreach (var tile in tiles)
        {
            if (tile is INotifyPropertyChanged item) Watch(item, tile);
            if (tile is ButtonItemViewModel button)
                foreach (var slot in new[] { button.Press, button.DoublePress, button.Hold }) Watch(slot, tile);
            if (!_probeHeights.ContainsKey(tile)) Pend(tile);
        }
        UpdateInspectorTopHeight();

        void Watch(INotifyPropertyChanged source, object tile)
        {
            if (!_probeWatched.TryAdd(source, tile)) return;
            source.PropertyChanged += OnProbedTileChanged;
        }
    }

    private void OnProbedTileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not INotifyPropertyChanged source || !_probeWatched.TryGetValue(source, out var tile)) return;
        var layout = sender switch
        {
            SliderItemViewModel => SliderLayout,
            ButtonItemViewModel => ButtonLayout,
            _ => SlotLayout,
        };
        if (e.PropertyName is null or "" || layout.Contains(e.PropertyName)) Pend(tile);
    }

    private void OnProbeWidthChanged()
    {
        var width = _sidePanel.Bounds.Width;
        if (width <= 0 || Math.Abs(width - _probeWidth) < 0.5) return;
        _probeWidth = width;
        foreach (var tile in DrawnTiles()) Pend(tile);   // their text wraps anew
    }

    private void Pend(object tile)
    {
        if (!_probePending.Contains(tile, ReferenceEqualityComparer.Instance)) _probePending.Add(tile);
        ScheduleProbe();
    }

    private void ScheduleProbe()
    {
        if (_probeScheduled || _probePending.Count == 0) return;
        _probeScheduled = true;
        Dispatcher.UIThread.Post(RunProbe, DispatcherPriority.Background);
    }

    /// <summary>Measures the pending tiles on the probe, one after another in one go, then empties
    /// it. A page that isn't shown, or whose side panel has no width yet, waits.</summary>
    private void RunProbe()
    {
        _probeScheduled = false;
        if (_probePending.Count == 0 || _probeWidth <= 0 || !this.IsAttachedToVisualTree()) return;
        _probe.Width = _probeWidth;
        foreach (var tile in _probePending.ToList())
        {
            _probe.Content = tile;
            _probe.UpdateLayout();
            ProbeMeasurements++;
            if (_probe.GetVisualDescendants().OfType<TileInspector>().FirstOrDefault() is { } probe && ReferenceEquals(probe.DataContext, tile))
                _probeHeights[tile] = probe.TopHeight;
        }
        _probePending.Clear();
        _probe.Content = null;
        UpdateInspectorTopHeight();
    }

    private void UpdateInspectorTopHeight()
    {
        var top = _probeHeights.Values.DefaultIfEmpty(0).Max();
        if (Math.Abs(top - InspectorTopHeight) > 0.5) InspectorTopHeight = top;
    }
}
