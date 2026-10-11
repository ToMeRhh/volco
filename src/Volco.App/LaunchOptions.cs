namespace Volco.App;

/// <summary>
/// How this process was started. Opened by the user (a double-click, the Start menu) Volco shows
/// its settings window; started by the OS at sign-in, it stays in the tray. The OS can't tell the
/// two apart, so the sign-in registration (<see cref="Platform.IStartWithSystem"/>) adds
/// <see cref="BackgroundFlag"/> to its command line.
/// </summary>
public sealed record LaunchOptions(bool Background)
{
    /// <summary>Start in the tray without showing the settings window.</summary>
    public const string BackgroundFlag = "--background";

    public static LaunchOptions Parse(IEnumerable<string> args)
        => new(args.Any(a => string.Equals(a, BackgroundFlag, StringComparison.OrdinalIgnoreCase)));
}
