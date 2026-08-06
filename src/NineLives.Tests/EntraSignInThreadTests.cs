using System.Windows.Threading;
using Azure.Core;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Entra sign-in must not happen on the UI thread (#152).
///
/// InteractiveBrowserCredential launches a browser and waits for the redirect to come back, on
/// whatever thread asked for the token. Every blob operation starts from a command on the UI
/// thread, so the window stopped painting for as long as the sign-in was open - the app asking
/// somebody to go and authenticate while appearing, to them, to have crashed.
///
/// It went unnoticed because the path that had been exercised was the FAILING one: a 403 for a
/// missing role comes back without any sign-in UI ever appearing.
/// </summary>
[Collection(WpfCollection.Name)]
public class EntraSignInThreadTests(WpfFixture wpf) : IDisposable
{
    public void Dispose() => BlobStorageService.CredentialFactoryForTests = null;

    /// <summary>
    /// Records the thread of every token request, in order.
    ///
    /// There is more than one: the warm-up asks first, then the client's own pipeline asks again
    /// for the request itself. Only the FIRST matters here - a real credential caches, so that is
    /// the one that opens a browser and waits, and every later one is a cache read.
    ///
    /// It also cancels after answering, so the test does not sit through the Azure SDK retrying a
    /// connection to a port nothing is listening on.
    /// </summary>
    private sealed class ThreadRecordingCredential(CancellationTokenSource stopAfterFirst) : TokenCredential
    {
        public List<int> CalledOnThreads { get; } = [];

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken ct)
        {
            lock (CalledOnThreads) CalledOnThreads.Add(Environment.CurrentManagedThreadId);

            // Stands in for somebody completing a sign-in in a browser. Short, but long enough that
            // a blocked dispatcher would fail the pumping check below.
            Thread.Sleep(300);
            stopAfterFirst.Cancel();

            return new AccessToken("token", DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken ct)
            => new(GetToken(requestContext, ct));
    }

    private static BlobContainerConfig EntraContainer() => new()
    {
        Id = BlobContainerConfig.NewId(),
        Name = "backups",
        // Nothing listens here, so the operation fails immediately AFTER the sign-in - which is
        // all this test needs, since the sign-in is what it is about.
        ContainerUrl = "https://127.0.0.1:1/backups",
        AuthMode = BlobAuthMode.EntraInteractive
    };

    [Fact]
    public void TheSignInDoesNotRunOnTheUiThread()
    {
        using var stop = new CancellationTokenSource();
        var credential = new ThreadRecordingCredential(stop);
        BlobStorageService.CredentialFactoryForTests = _ => credential;

        var uiThreadId = 0;
        var dispatcherKeptPumping = false;

        wpf.Invoke(() =>
        {
            uiThreadId = Environment.CurrentManagedThreadId;

            var service = new BlobStorageService(new FakeCredentialStore());
            var frame = new DispatcherFrame();

            // Queued at a lower priority than the work: it can only run if the dispatcher is still
            // processing messages, which is exactly what "the window is frozen" means it is not.
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => dispatcherKeptPumping = true));

            _ = service.VerifyConnectionAsync(EntraContainer(), stop.Token)
                .ContinueWith(_ => frame.Continue = false, TaskScheduler.FromCurrentSynchronizationContext());

            Dispatcher.PushFrame(frame);
        });

        Assert.NotEmpty(credential.CalledOnThreads);
        var signIn = credential.CalledOnThreads[0];
        Assert.NotEqual(uiThreadId, signIn);
        Assert.True(dispatcherKeptPumping, "the dispatcher stopped processing while signing in");
    }

    /// <summary>A SAS container has no sign-in to do, and must not be made to wait for one.</summary>
    [Fact]
    public async Task ASasContainerNeverAsksForAToken()
    {
        using var unused = new CancellationTokenSource();
        var credential = new ThreadRecordingCredential(unused);
        BlobStorageService.CredentialFactoryForTests = _ => credential;

        var service = new BlobStorageService(new FakeCredentialStore());
        var sas = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "backups",
            ContainerUrl = "https://127.0.0.1:1/backups",
            AuthMode = BlobAuthMode.SasToken
        };

        await Assert.ThrowsAnyAsync<Exception>(() => service.VerifyConnectionAsync(sas));

        Assert.Empty(credential.CalledOnThreads);
    }
}
