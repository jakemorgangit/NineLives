using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Flashes the taskbar button when a long operation finishes behind another window (#204).
///
/// The polite form of a completion signal: no toast, no sound, no window stealing focus - the
/// same amber flash Windows itself uses, which stops the moment the window is clicked. Anybody
/// who alt-tabbed away from a 40-minute restore gets told it finished without being interrupted
/// in whatever they moved on to.
/// </summary>
public static class WindowAttention
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    private const uint FLASHW_TRAY = 0x00000002;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    /// <summary>
    /// Flashes the app's taskbar button if no window of ours is foreground. A no-op when the user
    /// is already looking at the app - flashing at somebody mid-click is noise - and headless,
    /// where there is nothing to flash.
    /// </summary>
    public static void FlashIfInBackground()
    {
        // The Windows collection and IsActive are thread-affine, and this is called from the
        // execution's finally - a worker thread. Marshalled rather than touched directly, and
        // fire-and-forget: a completion signal must never be able to fail the run it signals.
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                var app = Application.Current;
                var window = app?.MainWindow;
                if (app == null || window == null) return;

                // Any of our windows being active counts as "watching" - the execution window is
                // its own top-level window and is exactly where somebody would be looking.
                foreach (Window w in app.Windows)
                    if (w.IsActive) return;

                var handle = new WindowInteropHelper(window).Handle;
                if (handle == IntPtr.Zero) return;

                var info = new FLASHWINFO
                {
                    cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                    hwnd = handle,
                    dwFlags = FLASHW_TRAY | FLASHW_TIMERNOFG,
                    uCount = uint.MaxValue,
                    dwTimeout = 0
                };

                FlashWindowEx(ref info);
            }
            catch
            {
                // Nothing about a missed flash is worth surfacing.
            }
        });
    }
}
