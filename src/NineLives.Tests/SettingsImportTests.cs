using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The Settings screen's import, minus the file dialog (#277). The screen that runs the import
/// is the same screen holding the webhook list, and that list rewrites the WHOLE config on any
/// row edit - so the import must refresh it, or the next click erases what was just imported.
/// </summary>
public class SettingsImportTests
{
    [Fact]
    public void ImportedWebhooksSurviveTheNextRowEdit()
    {
        var store = new FakeCredentialStore();
        store.Config.Webhooks.Add(new WebhookEndpoint { Id = "w-local", Name = "Existing" });

        var vm = new SettingsViewModel(store);

        // The file being imported carries a webhook this machine has never seen.
        var source = new AppConfig();
        source.Webhooks.Add(new WebhookEndpoint { Id = "w-imported", Name = "Imported" });
        var exported = ConfigPortability.Read(ConfigPortability.Export(source))!;

        var summary = vm.ImportFrom(exported);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.WebhooksAdded);

        // The next row edit rewrites the whole list from screen state - the erasing click.
        vm.AddWebhookCommand.Execute(null);

        var names = store.LoadConfig().Webhooks.Select(w => w.Name).ToList();
        Assert.Contains("Existing", names);
        Assert.Contains("Imported", names);
        Assert.Equal(3, names.Count);
    }
}
