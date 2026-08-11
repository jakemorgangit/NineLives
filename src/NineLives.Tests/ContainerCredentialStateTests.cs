using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.Models;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The dot beside a container says something (#407).
///
/// It was `Fill="{DynamicResource SuccessBrush}"` unconditionally: a green light beside every
/// container whether or not it had a credential, whether or not it had ever been tested. No
/// tooltip, no legend, so the only reading available was "fine".
///
/// The case that makes it matter is one the app creates itself. A config export carries no
/// secrets - its own message says credentials are re-entered on the importing machine - so the
/// first thing somebody sees after importing on a new machine was a list of green dots on
/// containers that could not reach anything.
///
/// The SQL Servers screen had this right all along: its green dot sits behind IsConnected.
/// </summary>
public class ContainerCredentialStateTests
{
    private static (BlobConfigViewModel Vm, FakeCredentialStore Store) Screen(
        params (string Name, string Url)[] containers)
    {
        var store = new FakeCredentialStore();
        foreach (var (name, url) in containers)
        {
            store.Config.BlobContainers.Add(new BlobContainerConfig
            {
                Id = BlobContainerConfig.NewId(),
                Name = name,
                ContainerUrl = url
            });
        }

        return (new BlobConfigViewModel(store, new FakeBlobStorageService()), store);
    }

    [Fact]
    public void AContainerWithNoStoredSecretSaysSo()
    {
        var (vm, _) = Screen(("prod", "https://acct.blob.core.windows.net/prod"));

        var container = vm.Containers.Single();

        Assert.Equal(ContainerCredentialState.Missing, container.CredentialState);
        Assert.True(container.CredentialIsMissing);
        Assert.Contains("No SAS token is stored", container.CredentialStateNote);
    }

    [Fact]
    public void AContainerWithAStoredSecretIsPresent()
    {
        var store = new FakeCredentialStore();
        var config = new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "prod",
            ContainerUrl = "https://acct.blob.core.windows.net/prod"
        };
        store.Config.BlobContainers.Add(config);
        store.SaveSasToken(config, "sv=2022-11-02&sig=abc");

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());

        var container = vm.Containers.Single();
        Assert.Equal(ContainerCredentialState.Present, container.CredentialState);
        Assert.False(container.CredentialIsMissing);
    }

    /// <summary>
    /// The case that would have gone stale. The list is bound live and Save deliberately does not
    /// reload it, so without recomputing on save the dot stayed red on a container whose
    /// credential had just been fixed - which would have been a worse bug than the one being
    /// fixed, since it would teach people to ignore the marker.
    /// </summary>
    [Fact]
    public void FixingTheCredentialTurnsTheMarkerOff()
    {
        var (vm, _) = Screen(("prod", "https://acct.blob.core.windows.net/prod"));

        var container = vm.Containers.Single();
        Assert.True(container.CredentialIsMissing);

        var raised = new List<string>();
        container.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");

        vm.SelectedContainer = container;
        vm.EditCommand.Execute(null);
        vm.EditSasToken = "sv=2022-11-02&sig=abc";
        vm.SaveCommand.Execute(null);

        Assert.False(vm.Containers.Single().CredentialIsMissing);

        // And the list is told, or the dot would not repaint until the screen was rebuilt.
        Assert.Contains(nameof(BlobContainerConfig.CredentialIsMissing), raised);
    }

    /// <summary>
    /// Entra has no stored secret to be missing - which is the entire reason an organisation
    /// switches to it - so it must not be reported as a gap.
    /// </summary>
    [Fact]
    public void AnEntraContainerIsNotReportedAsMissingACredential()
    {
        var store = new FakeCredentialStore();
        store.Config.BlobContainers.Add(new BlobContainerConfig
        {
            Id = BlobContainerConfig.NewId(),
            Name = "prod",
            ContainerUrl = "https://acct.blob.core.windows.net/prod",
            AuthMode = BlobAuthMode.EntraInteractive
        });

        var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
        var container = vm.Containers.Single();

        Assert.Equal(ContainerCredentialState.Present, container.CredentialState);
        Assert.Contains("no stored secret", container.CredentialStateNote);
    }

    /// <summary>An S3 bucket's missing secret is named for what it is.</summary>
    [Fact]
    public void AnS3BucketNamesTheKeyPair()
    {
        var (vm, _) = Screen(("dr", "s3://s3.eu-west-2.amazonaws.com/dr"));

        var container = vm.Containers.Single();
        Assert.True(container.CredentialIsMissing);
        Assert.Contains("access key pair", container.CredentialStateNote);
    }

    /// <summary>
    /// In words, not only in colour. The selected-row style in this app already carries a note
    /// that high contrast makes a tinted fill nearly invisible, so meaning must not ride on
    /// colour alone - and an 8px dot is the worst case of that.
    /// </summary>
    [Collection(WpfCollection.Name)]
    public class Rendered(WpfFixture wpf)
    {
        [Fact]
        public void TheListSaysItInWordsAsWellAsInColour()
        {
            wpf.Invoke(() =>
            {
                var store = new FakeCredentialStore();
                store.Config.BlobContainers.Add(new BlobContainerConfig
                {
                    Id = BlobContainerConfig.NewId(),
                    Name = "imported",
                    ContainerUrl = "https://acct.blob.core.windows.net/imported"
                });

                var vm = new BlobConfigViewModel(store, new FakeBlobStorageService());
                var view = new BlobConfigView { DataContext = vm };

                view.Measure(new Size(1280, 900));
                view.Arrange(new Rect(0, 0, 1280, 900));
                view.UpdateLayout();

                var shown = FindAll<TextBlock>(view)
                    .Where(t => t.Visibility == Visibility.Visible)
                    .Select(t => t.Text)
                    .ToList();

                Assert.Contains("no credential stored", shown);
            });
        }

        private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var node = VisualTreeHelper.GetChild(root, i);
                if (node is T match) yield return match;
                foreach (var descendant in FindAll<T>(node)) yield return descendant;
            }
        }
    }
}
