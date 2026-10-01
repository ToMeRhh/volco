using Volco.App;
using Volco.Core.Diagnostics;
using Volco.Windows.Platform;

namespace Volco.Windows;

internal static class Program
{
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

        return VolcoAppHost.Run(args, () =>
        {
            var engine = new Volco.Windows.Audio.AudioEngine();
            bool ready = engine.Start();
            return new PlatformServices(engine, ready, new Win32WindowEffects());
        });
    }
}
