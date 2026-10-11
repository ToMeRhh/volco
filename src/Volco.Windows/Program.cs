using System.Runtime.InteropServices;
using Volco.App;
using Volco.Core.Diagnostics;
using Volco.Windows.Firmware;
using Volco.Windows.Media;
using Volco.Windows.Platform;
using Volco.Windows.Programs;

namespace Volco.Windows;

internal static class Program
{
    private const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [STAThread]
    private static int Main(string[] args)
    {
        // Process-wide last-resort logging; the UI-thread hook lives in VolcoApplication.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error($"Unhandled exception (terminating={e.IsTerminating}): {e.ExceptionObject}");
            AppLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.Error($"Unobserved task exception: {e.Exception}");

        // A second start (a double-click) has the foreground; pass that right on, so the running
        // Volco it asks to show its window may come to the front instead of flashing in the taskbar.
        AllowSetForegroundWindow(ASFW_ANY);

        return VolcoAppHost.Run(args, () =>
        {
            var engine = new Volco.Windows.Audio.AudioEngine();
            bool ready = engine.Start();
            var startWithSystem = Environment.ProcessPath is { } exe ? new RunKeyStartWithSystem(exe) : null;
            return new PlatformServices(engine, ready, new Win32WindowEffects(),
                                        new EspDotNetFirmwareFlasher(), new EmbeddedFirmwareSource(),
                                        new MediaSessions(), new ProgramLauncher(), startWithSystem);
        });
    }
}
