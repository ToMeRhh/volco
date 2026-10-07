using System.Runtime.InteropServices;
using Avalonia.Controls;
using Volco.App.Platform;
using Volco.Core.Diagnostics;

namespace Volco.Windows.Platform;

/// <summary>
/// Win32 implementation of <see cref="IWindowEffects"/>. The only UI P/Invoke in the repo; the
/// style combination is the one proven to work on Windows.
/// </summary>
internal sealed class Win32WindowEffects : IWindowEffects
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_TOOLWINDOW = 0x00000080;
    private const long WS_EX_LAYERED = 0x00080000;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const uint LWA_ALPHA = 0x2;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint hWnd, uint crKey, byte bAlpha, uint dwFlags);

    public void MakeClickThrough(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null)
        {
            AppLog.Info("MakeClickThrough: window has no platform handle; skipped");
            return;
        }

        var hwnd = handle.Handle;
        var style = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        var updated = style | (nint)(WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, updated);

        // Fully opaque, but it commits the layered state; testing showed corners stay
        // transparent and click-through works only with this call.
        var ok = SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);
        AppLog.Info($"MakeClickThrough: exstyle 0x{style:X} -> 0x{updated:X}, SetLayeredWindowAttributes={ok}");
    }
}
