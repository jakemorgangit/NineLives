using System.Windows;
using System.Windows.Interop;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The WPF answer to <see cref="EntraAuthentication.PromptParent"/>: which window the Entra
/// sign-in prompt should sit in front of (#30). This lived inside EntraAuthentication until the
/// engine moved to NineLives.Core (#63) - the registration machinery is front-end-agnostic, but
/// resolving a Window can only ever be the desktop app's business.
/// </summary>
internal static class EntraPromptWindow
{
    /// <summary>
    /// The window the sign-in prompt should sit in front of: whichever is active, falling back to
    /// the main window. Zero when there is no window yet, which the broker reports as the
    /// window_handle_required error rather than crashing.
    ///
    /// MSAL calls this from whatever thread is acquiring the token, and WPF's window collection can
    /// only be touched on the UI thread - hence the marshalling. It is a direct call when already
    /// on the dispatcher, so the common case costs nothing.
    /// </summary>
    internal static nint ActiveWindowHandle()
    {
        var app = Application.Current;
        if (app == null) return nint.Zero;

        return app.Dispatcher.CheckAccess()
            ? ResolveOnUiThread()
            : app.Dispatcher.Invoke(ResolveOnUiThread);
    }

    private static nint ResolveOnUiThread()
    {
        var app = Application.Current;
        if (app == null) return nint.Zero;

        // Active first: with a modal open, a prompt parented to the main window sits BEHIND it and
        // looks like a hang.
        var window = app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? app.MainWindow;

        return window == null ? nint.Zero : new WindowInteropHelper(window).Handle;
    }
}
