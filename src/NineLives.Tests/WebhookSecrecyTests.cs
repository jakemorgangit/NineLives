using System.Net;
using System.Net.Http;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Webhook URLs get the secret treatment (#317) and deliveries get a route (#316). The URL
/// commits on an explicit Save, moves to the vault, and is never displayed again; deliveries
/// hydrate it at send time on clones, through a handler built from the configured proxy -
/// System, Direct, or Custom with vault-held credentials.
/// </summary>
public class WebhookSecrecyTests
{
    private static WebhookEndpoint Endpoint() => new()
    { Id = "w1", Name = "Ops", NotifyProblems = true };

    // ── the URL as a secret (#317) ──────────────────────────────────────────────

    [Fact]
    public void SavingAUrlMovesItToTheVaultAndOutOfTheFile()
    {
        var store = new FakeCredentialStore();
        var endpoint = Endpoint();
        endpoint.Url = "https://legacy.example/hook";

        WebhookTransport.SaveUrl(endpoint, store, "https://new.example/hook ");

        Assert.Equal(string.Empty, endpoint.Url);
        Assert.Equal("https://new.example/hook",
            WebhookTransport.ResolveUrl(endpoint, store));
    }

    /// <summary>Configs that predate the vault keep delivering from their in-file URL.</summary>
    [Fact]
    public void ALegacyInFileUrlStillResolves()
    {
        var store = new FakeCredentialStore();
        var endpoint = Endpoint();
        endpoint.Url = "https://legacy.example/hook";

        Assert.Equal("https://legacy.example/hook",
            WebhookTransport.ResolveUrl(endpoint, store));
        Assert.True(WebhookTransport.HasUrl(endpoint, store));
    }

    [Fact]
    public void DeliveriesHydrateClonesAndTheConfigObjectStaysBlank()
    {
        var store = new FakeCredentialStore();
        var endpoint = Endpoint();
        WebhookTransport.SaveUrl(endpoint, store, "https://vaulted.example/hook");

        var hydrated = WebhookTransport.HydrateUsable([endpoint], store);

        Assert.Equal("https://vaulted.example/hook", hydrated.Single().Url);
        Assert.Equal(string.Empty, endpoint.Url);
        Assert.True(hydrated.Single().NotifyProblems);
    }

    [Fact]
    public void AnEndpointWithNoUrlAnywhereIsNotUsable()
    {
        var store = new FakeCredentialStore();

        Assert.Empty(WebhookTransport.HydrateUsable([Endpoint()], store));
    }

    /// <summary>The row's whole contract: explicit save, masked after, never read back.</summary>
    [Fact]
    public void TheRowCommitsOnSaveAndMasksAfter()
    {
        var store = new FakeCredentialStore();
        var saves = 0;
        var row = new WebhookEndpointViewModel(
            Endpoint(), store, () => saves++, _ => Task.CompletedTask);

        Assert.False(row.HasStoredUrl);
        Assert.Contains("No URL saved", row.UrlDisplay);
        Assert.False(row.SaveUrlCommand.CanExecute(null));

        row.UrlInput = "https://hooks.example/T000/B000/secret";
        Assert.True(row.SaveUrlCommand.CanExecute(null));
        row.SaveUrlCommand.Execute(null);

        Assert.True(row.HasStoredUrl);
        Assert.Equal(string.Empty, row.UrlInput);
        Assert.Equal(string.Empty, row.Model.Url);
        Assert.DoesNotContain("secret", row.UrlDisplay);
        Assert.Equal(1, saves);
    }

    // ── the delivery route (#316) ───────────────────────────────────────────────

    [Fact]
    public void TheDefaultRouteIsTheSystemsOwn()
    {
        using var handler = (HttpClientHandler)WebhookTransport.BuildHandler(
            null, new FakeCredentialStore());

        Assert.True(handler.UseProxy);
        Assert.Null(handler.Proxy);
    }

    [Fact]
    public void DirectBypassesAnyProxy()
    {
        using var handler = (HttpClientHandler)WebhookTransport.BuildHandler(
            new WebhookProxySettings { Mode = WebhookProxyMode.Direct },
            new FakeCredentialStore());

        Assert.False(handler.UseProxy);
    }

    [Fact]
    public void ACustomProxyCarriesItsAddressAndVaultedCredentials()
    {
        var store = new FakeCredentialStore();
        store.SaveSecret(WebhookTransport.ProxyCredentialKey, "svc_proxy", "proxy-pass");

        using var handler = (HttpClientHandler)WebhookTransport.BuildHandler(
            new WebhookProxySettings
            {
                Mode = WebhookProxyMode.Custom,
                Url = "http://proxy.internal:8080",
                Username = "svc_proxy"
            }, store);

        Assert.True(handler.UseProxy);
        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Equal("http://proxy.internal:8080/", proxy.Address!.ToString());
        var credential = Assert.IsType<NetworkCredential>(proxy.Credentials);
        Assert.Equal("svc_proxy", credential.UserName);
        Assert.Equal("proxy-pass", credential.Password);
    }

    [Fact]
    public void ACustomProxyWithoutAUsernameSendsNoCredentials()
    {
        using var handler = (HttpClientHandler)WebhookTransport.BuildHandler(
            new WebhookProxySettings
            { Mode = WebhookProxyMode.Custom, Url = "http://proxy.internal:8080" },
            new FakeCredentialStore());

        var proxy = Assert.IsType<WebProxy>(handler.Proxy);
        Assert.Null(proxy.Credentials);
    }

    // ── what travels (#316's addressing rule) ───────────────────────────────────

    [Fact]
    public void TheProxyPasswordNeverReachesAnExport()
    {
        var store = new FakeCredentialStore();
        store.SaveSecret(WebhookTransport.ProxyCredentialKey, "svc_proxy", "NEVER-THIS");
        var config = new AppConfig
        {
            WebhookProxy = new WebhookProxySettings
            {
                Mode = WebhookProxyMode.Custom,
                Url = "http://proxy.internal:8080",
                Username = "svc_proxy"
            }
        };

        var json = ConfigPortability.Export(config);

        Assert.DoesNotContain("NEVER-THIS", json);
        Assert.Contains("proxy.internal", json);

        var fresh = new AppConfig();
        ConfigPortability.Merge(fresh, ConfigPortability.Read(json)!);
        Assert.Equal(WebhookProxyMode.Custom, fresh.WebhookProxy?.Mode);
    }
}
