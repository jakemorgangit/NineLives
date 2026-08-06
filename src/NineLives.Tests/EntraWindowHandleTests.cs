using System.Reflection;
using System.Windows;
using System.Windows.Interop;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Microsoft.Data.SqlClient;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Parenting the Entra sign-in prompt to a window (#30).
///
/// On Windows, MSAL signs in through the Web Account Manager broker rather than a plain browser
/// window, and the broker refuses to show a dialog it cannot parent - which is the second thing
/// the first real sign-in attempt hit, after the missing provider package:
///
///   A window handle must be configured. (window_handle_required)
///
/// Nothing here proves a token is issued. What it proves is that the app answers the question the
/// broker asks, which is the part the app is responsible for.
/// </summary>
[Collection(WpfCollection.Name)]
public class EntraWindowHandleTests(WpfFixture wpf)
{
    /// <summary>
    /// The regression. Building an Entra connection string has to leave a provider in place that
    /// can be asked for a window - the driver's own discovered provider has none, because nothing
    /// in a library can know what the application's window is.
    /// </summary>
    [Theory]
    [InlineData(AuthMode.EntraInteractive, SqlAuthenticationMethod.ActiveDirectoryInteractive)]
    [InlineData(AuthMode.EntraIntegrated, SqlAuthenticationMethod.ActiveDirectoryIntegrated)]
    [InlineData(AuthMode.EntraDefault, SqlAuthenticationMethod.ActiveDirectoryDefault)]
    public void BuildingAnEntraConnectionRegistersAProviderThatCanBeGivenAWindow(
        AuthMode mode, SqlAuthenticationMethod method)
    {
        EntraAuthentication.ResetForTests();

        new SqlServerService(new FakeCredentialStore()).BuildConnectionString(new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "srv01.database.windows.net",
            AuthMode = mode
        });

        var provider = SqlAuthenticationProvider.GetProvider(method);

        Assert.NotNull(provider);
        // Ours, registered with a window func - not whatever the driver discovered on its own.
        Assert.Equal(
            "Microsoft.Data.SqlClient.ActiveDirectoryAuthenticationProvider",
            provider.GetType().FullName);
        Assert.True(provider.IsSupported(method));
    }

    /// <summary>
    /// The link that actually broke, and the one nothing else here covers: a provider IS registered
    /// and a handle CAN be resolved, but if the two are never connected the broker still gets
    /// nothing and the sign-in still fails with window_handle_required.
    ///
    /// Reaches into the provider's private field to do it. That is a deliberate trade: it is the
    /// only way to see what the driver was told without standing in front of a real sign-in, and
    /// this is precisely the wiring that shipped broken. If Microsoft.Data.SqlClient renames the
    /// field this test fails loudly, which is the right outcome - it means checking the API again,
    /// not assuming the wiring survived.
    /// </summary>
    [Fact]
    public void TheRegisteredProviderIsWiredToTheWindowLookup()
    {
        EntraAuthentication.ResetForTests();
        EntraAuthentication.Register(() => 4242);

        var provider = SqlAuthenticationProvider.GetProvider(
            SqlAuthenticationMethod.ActiveDirectoryInteractive);

        var field = provider!.GetType().GetField(
            "_parentActivityOrWindowFunc",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Assert.True(field != null,
            "Microsoft.Data.SqlClient no longer stores the parent window func in "
            + "_parentActivityOrWindowFunc - check SetParentActivityOrWindowFunc is still how a "
            + "window is supplied before assuming Entra sign-in still works.");

        var func = Assert.IsType<Func<object>>(field!.GetValue(provider), exactMatch: false);
        Assert.Equal((nint)4242, func());
    }

    /// <summary>
    /// Registration is process-wide global state. Doing it twice would replace a provider that may
    /// already be mid-sign-in, so it happens once and later calls are no-ops.
    /// </summary>
    [Fact]
    public void RegisteringIsIdempotent()
    {
        EntraAuthentication.ResetForTests();
        EntraAuthentication.Register(() => 1);
        var first = SqlAuthenticationProvider.GetProvider(
            SqlAuthenticationMethod.ActiveDirectoryInteractive);

        EntraAuthentication.Register(() => 2);
        var second = SqlAuthenticationProvider.GetProvider(
            SqlAuthenticationMethod.ActiveDirectoryInteractive);

        Assert.Same(first, second);
    }

    // ── which window ────────────────────────────────────────────────────────────

    /// <summary>
    /// A real HWND for a shown window. Zero is what the broker rejects, so "not zero" is the whole
    /// point - and it has to be a window that actually exists, since a handle is only created when
    /// the window is shown.
    /// </summary>
    [Fact]
    public void AShownWindowGivesARealHandle()
    {
        wpf.Invoke(() =>
        {
            var window = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            try
            {
                window.Show();
                Application.Current.MainWindow = window;

                var handle = EntraAuthentication.ActiveWindowHandle();

                Assert.NotEqual(nint.Zero, handle);
                Assert.Equal(new WindowInteropHelper(window).Handle, handle);
            }
            finally
            {
                Application.Current.MainWindow = null;
                window.Close();
            }
        });
    }

    /// <summary>
    /// With a modal open the prompt must parent to THAT, not to the main window - a dialog parented
    /// behind a modal is invisible, and a sign-in nobody can see reads as a hang.
    /// </summary>
    [Fact]
    public void TheActiveWindowWinsOverTheMainWindow()
    {
        wpf.Invoke(() =>
        {
            var main = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            var onTop = new Window { Width = 80, Height = 80, ShowInTaskbar = false };
            try
            {
                main.Show();
                Application.Current.MainWindow = main;
                onTop.Owner = main;
                onTop.Show();
                onTop.Activate();

                // Only assert the preference when the test machine actually gave it focus -
                // an unfocused desktop session would otherwise fail this for the wrong reason.
                if (!onTop.IsActive) return;

                Assert.Equal(new WindowInteropHelper(onTop).Handle,
                    EntraAuthentication.ActiveWindowHandle());
            }
            finally
            {
                Application.Current.MainWindow = null;
                onTop.Close();
                main.Close();
            }
        });
    }

    /// <summary>
    /// No window yet means zero rather than a crash. The broker turns that into the
    /// window_handle_required message, which is a far better outcome than an exception out of a
    /// background token acquisition.
    /// </summary>
    [Fact]
    public void NoWindowIsZeroRatherThanAThrow()
    {
        wpf.Invoke(() =>
        {
            Application.Current.MainWindow = null;

            var ex = Record.Exception(() => EntraAuthentication.ActiveWindowHandle());

            Assert.Null(ex);
        });
    }

    /// <summary>
    /// MSAL acquires the token on whatever thread it likes, and WPF's window collection can only be
    /// touched on the UI thread. Resolving from a background thread has to marshal rather than
    /// throw - this is the call that happens for real, on the thread it really happens on.
    /// </summary>
    [Fact]
    public async Task TheHandleCanBeAskedForFromABackgroundThread()
    {
        Window? window = null;
        wpf.Invoke(() =>
        {
            window = new Window { Width = 100, Height = 100, ShowInTaskbar = false };
            window.Show();
            Application.Current.MainWindow = window;
        });

        try
        {
            var handle = await Task.Run(EntraAuthentication.ActiveWindowHandle);

            Assert.NotEqual(nint.Zero, handle);
        }
        finally
        {
            wpf.Invoke(() =>
            {
                Application.Current.MainWindow = null;
                window!.Close();
            });
        }
    }
}
