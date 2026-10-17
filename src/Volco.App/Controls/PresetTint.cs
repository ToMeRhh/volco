using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;

namespace Volco.App.Controls;

/// <summary>
/// A preset's colour on the Buttons page. Setting <c>PresetTint.Index</c> on a control puts two
/// resources in it, <c>PresetFill</c> and <c>PresetEdge</c>: the theme's <c>PresetFill{n}Brush</c> and
/// <c>PresetEdge{n}Brush</c> (n = index mod <see cref="Count"/>), kept current when the theme changes.
/// The control, its template and its content use them as <c>{DynamicResource PresetFill}</c>.
/// </summary>
public static class PresetTint
{
    /// <summary>How many tints there are (Colors.axaml); presets past the last reuse them in turn.</summary>
    public const int Count = 6;

    public const string FillKey = "PresetFill";
    public const string EdgeKey = "PresetEdge";

    public static readonly AttachedProperty<int> IndexProperty =
        AvaloniaProperty.RegisterAttached<Control, int>("Index", typeof(PresetTint), -1);

    private static readonly ConditionalWeakTable<Control, IDisposable[]> Subscriptions = new();

    static PresetTint()
    {
        IndexProperty.Changed.AddClassHandler<Control>((control, _) => Apply(control));
    }

    public static int GetIndex(Control control) => control.GetValue(IndexProperty);
    public static void SetIndex(Control control, int value) => control.SetValue(IndexProperty, value);

    private static void Apply(Control control)
    {
        if (Subscriptions.TryGetValue(control, out var old))
        {
            foreach (var subscription in old) subscription.Dispose();
            Subscriptions.Remove(control);
        }
        var index = GetIndex(control);
        if (index < 0)
        {
            control.Resources.Remove(FillKey);
            control.Resources.Remove(EdgeKey);
            return;
        }
        var n = index % Count;
        Subscriptions.Add(control, new[]
        {
            Mirror(control, $"PresetFill{n}Brush", FillKey),
            Mirror(control, $"PresetEdge{n}Brush", EdgeKey),
        });
    }

    /// <summary>Copies the theme resource <paramref name="source"/> into the control's own
    /// <paramref name="target"/> each time it resolves differently.</summary>
    private static IDisposable Mirror(Control control, string source, string target)
        => control.GetResourceObservable(source).Subscribe(new Observer(value =>
        {
            // Writing our own resources makes the observable look again; stop when nothing changed.
            if (control.Resources.TryGetValue(target, out var current) && ReferenceEquals(current, value)) return;
            if (value is null) control.Resources.Remove(target);
            else control.Resources[target] = value;
        }));

    private sealed class Observer(Action<object?> next) : IObserver<object?>
    {
        public void OnNext(object? value) => next(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
