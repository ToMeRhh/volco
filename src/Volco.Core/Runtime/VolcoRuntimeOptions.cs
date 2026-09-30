namespace Volco.Core.Runtime;

/// <summary>
/// Tunables for <see cref="VolcoRuntime"/>. Exists so tests can inject short delays; the app
/// always uses <see cref="Default"/>.
/// </summary>
/// <param name="PostConnectRepushDelays">One forced controller-state re-push is scheduled at
/// each of these delays after every transition to Connected (connect-race fix, mitigation (a) -
/// see <see cref="VolcoRuntime"/>). An empty list disables the timed re-pushes.</param>
public sealed record VolcoRuntimeOptions(IReadOnlyList<TimeSpan> PostConnectRepushDelays)
{
    public static VolcoRuntimeOptions Default { get; } =
        new(new[] { TimeSpan.FromMilliseconds(750), TimeSpan.FromMilliseconds(2500) });
}
