using System.Windows;
using System.Windows.Interop;
using Microsoft.Data.SqlClient;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Tells Microsoft.Data.SqlClient which window to parent the Entra sign-in prompt to (#30).
///
/// On Windows, MSAL signs in through the Web Account Manager broker rather than a plain browser
/// window, and the broker refuses to show a dialog it cannot parent:
///
///   Failed to acquire access token for ActiveDirectoryInteractive:
///   A window handle must be configured. (0x... window_handle_required)
///
/// The driver's own discovered provider has no handle to offer, because nothing in a library can
/// know what the application's window is. Registering the provider ourselves is the only way to
/// supply one - which is what makes the interactive mode work at all on a desktop app.
///
/// The handle is resolved lazily, per sign-in, rather than captured once. At the point registration
/// happens the main window may not exist yet, its handle changes if it is ever recreated, and if a
/// modal is up the prompt should parent to THAT rather than to whatever was frontmost at startup.
/// </summary>
internal static class EntraAuthentication
{
    private static readonly Lock Gate = new();
    private static bool _registered;

    /// <summary>
    /// Registers a provider that parents its prompt to whatever <paramref name="parentWindow"/>
    /// returns at the moment a sign-in is needed.
    ///
    /// Process-wide and done once; calling it again is a no-op rather than a second registration.
    /// </summary>
    internal static void Register(Func<nint> parentWindow)
    {
        lock (Gate)
        {
            if (_registered) return;

            var provider = new ActiveDirectoryAuthenticationProvider();
            provider.SetParentActivityOrWindowFunc(() => parentWindow());

            // One provider serves all three: Default can fall through to an interactive prompt, and
            // Integrated can be asked to fall back, so all of them may need a window.
            SqlAuthenticationProvider.SetProvider(
                SqlAuthenticationMethod.ActiveDirectoryInteractive, provider);
            SqlAuthenticationProvider.SetProvider(
                SqlAuthenticationMethod.ActiveDirectoryIntegrated, provider);
            SqlAuthenticationProvider.SetProvider(
                SqlAuthenticationMethod.ActiveDirectoryDefault, provider);

            _registered = true;
        }
    }

    /// <summary>
    /// Test hook: forgets that registration happened, so a test can register its own window source.
    ///
    /// Needed because registration is deliberately once-per-process - without this the first test
    /// to run would decide what every later one sees, and which test that is depends on the
    /// runner's ordering.
    /// </summary>
    internal static void ResetForTests()
    {
        lock (Gate) _registered = false;
    }

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
