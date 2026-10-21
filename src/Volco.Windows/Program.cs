using System.Runtime.InteropServices;
using Avalonia;
using Volco.App;
using Volco.App.Views.Stage;
using Volco.Core.Diagnostics;
using Volco.Windows.Firmware;
using Volco.Windows.Media;
using Volco.Windows.Platform;
using Volco.Windows.Programs;

namespace Volco.Windows;

internal static class Program
{
    private const int ASFW_ANY = -1;
    private const uint SPI_GETCLIENTAREAANIMATION = 0x1042;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam,
                                                    [MarshalAs(UnmanagedType.Bool)] out bool pvParam, uint fWinIni);

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

        // Settings › Accessibility › Visual effects › Animation effects: off, the settings window's
        // crossfades swap at once (hardware-page spec §3.3).
        if (SystemParametersInfo(SPI_GETCLIENTAREAANIMATION, 0, out var animate, 0)) StageTransitions.Enabled = animate;

        return VolcoAppHost.Run(args, UseWindowsBackends, () =>
        {
            var engine = new Volco.Windows.Audio.AudioEngine();
            bool ready = engine.Start();
            var startWithSystem = Environment.ProcessPath is { } exe ? new RunKeyStartWithSystem(exe) : null;
            return new PlatformServices(engine, ready, new Win32WindowEffects(),
                                        new EspDotNetFirmwareFlasher(), new EmbeddedFirmwareSource(),
                                        new MediaSessions(), new ProgramLauncher(), startWithSystem,
                                        new ShellAppIcons());
        });
    }

    // What Avalonia.Desktop's UsePlatformDetect picks on Windows, without referencing the Linux
    // and macOS backends it also carries. Rendering is Skia on the CPU: the GPU path needs ANGLE
    // (av_libglesv2.dll, 5 MB), which the publish leaves out (Volco.Windows.csproj, DropAngle).
    private static AppBuilder UseWindowsBackends(AppBuilder builder) =>
        builder.UseHarfBuzz().UseWin32().UseSkia()
               .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.Software] });
}
