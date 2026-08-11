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
    /// How the front end answers "which window should the prompt sit in front of?" - asked at
    /// the moment a sign-in is needed, never captured. The WPF app assigns its window resolver
    /// at startup; the default answers "no window", which is what a headless process truthfully
    /// has - the broker then reports window_handle_required for Interactive instead of hanging,
    /// while Integrated and Default carry on fine without one (#63).
    /// </summary>
    internal static Func<nint> PromptParent { get; set; } = static () => nint.Zero;

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

}
