using System.ComponentModel;
using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Pins the change notification that makes edited tags appear immediately.
///
/// This bug has now been fixed twice and returned once. First the tags were a plain List that was
/// reassigned, so nothing notified. That was fixed by making them an ObservableCollection mutated
/// in place - which then broke again when the UI moved to the derived TagChips member, because a
/// CollectionChanged on Tags says nothing about TagChips.
///
/// These tests assert the contract directly rather than the mechanism, so any future refactor
/// that stops the pill list updating fails here instead of in a screenshot.
/// </summary>
public class TagNotificationTests
{
    private static List<string> CapturePropertyChanges(INotifyPropertyChanged source, Action mutate)
    {
        var seen = new List<string>();
        void Handler(object? s, PropertyChangedEventArgs e) => seen.Add(e.PropertyName ?? "");
        source.PropertyChanged += Handler;
        try { mutate(); }
        finally { source.PropertyChanged -= Handler; }
        return seen;
    }

    // ── ServerConnection ─────────────────────────────────────────────────────────

    [Fact]
    public void Server_AddingATag_NotifiesTagChips()
    {
        var server = new ServerConnection();

        var changes = CapturePropertyChanges(server, () => server.Tags.Add("prod"));

        Assert.Contains(nameof(ServerConnection.TagChips), changes);
    }

    [Fact]
    public void Server_ClearingAndRefillingTags_NotifiesTagChips()
    {
        // Exactly what saving an edited tag list does.
        var server = new ServerConnection();
        server.Tags.Add("old");

        var changes = CapturePropertyChanges(server, () =>
        {
            server.Tags.Clear();
            server.Tags.Add("new");
        });

        Assert.Contains(nameof(ServerConnection.TagChips), changes);
        Assert.Equal(["new"], server.TagChips.Select(c => c.Text));
    }

    [Fact]
    public void Server_ReplacingTheWholeCollection_StillNotifiesAndResubscribes()
    {
        // Assigning a new collection used to silently detach notification. It must both notify
        // AND keep working for subsequent in-place edits.
        var server = new ServerConnection();
        server.Tags = [new string("prod")];

        var changes = CapturePropertyChanges(server, () => server.Tags.Add("dr"));

        Assert.Contains(nameof(ServerConnection.TagChips), changes);
        Assert.Equal(2, server.TagChips.Count());
    }

    [Fact]
    public void Server_SettingDetectedVersion_NotifiesTagChips()
    {
        var server = new ServerConnection();

        var changes = CapturePropertyChanges(server, () => server.DetectedVersion = "SQL Server 2022");

        Assert.Contains(nameof(ServerConnection.TagChips), changes);
        Assert.Contains(nameof(ServerConnection.AutoTags), changes);
        Assert.Contains(nameof(ServerConnection.HasAutoTags), changes);
    }

    [Fact]
    public void Server_SettingTheSameDetectedVersion_DoesNotNotify()
    {
        var server = new ServerConnection { DetectedVersion = "SQL Server 2022" };

        var changes = CapturePropertyChanges(server, () => server.DetectedVersion = "SQL Server 2022");

        Assert.Empty(changes);
    }

    [Fact]
    public void Server_TagChips_CombinesManualThenAutomatic()
    {
        var server = new ServerConnection { DetectedVersion = "SQL Server 2022" };
        server.Tags.Add("prod");
        server.Tags.Add("eu");

        var chips = server.TagChips.ToList();

        // Manual tags alphabetical, automatic ones after them. The manual group is sorted at
        // display as well as on save, so a server stored before sorting existed still reads in
        // order without being edited first.
        Assert.Equal(["eu", "prod", "SQL Server 2022"], chips.Select(c => c.Text));
        Assert.Equal([false, false, true], chips.Select(c => c.IsAutomatic));
    }

    [Fact]
    public void Server_TagChips_AreSortedEvenWhenTheStoredOrderIsNot()
    {
        // Exactly the case in an existing config: tags saved in the order they were created.
        var server = new ServerConnection();
        server.Tags.Add("homelab");
        server.Tags.Add("blackcat");

        Assert.Equal(["blackcat", "homelab"], server.TagChips.Select(c => c.Text));
    }

    [Fact]
    public void Container_TagChips_AreSortedEvenWhenTheStoredOrderIsNot()
    {
        var container = new BlobContainerConfig();
        container.Tags.Add("uat");
        container.Tags.Add("archive");

        Assert.Equal(["archive", "uat"], container.TagChips.Select(c => c.Text));
    }

    [Fact]
    public void Server_TagChips_SortIgnoringCase()
    {
        var server = new ServerConnection();
        server.Tags.Add("Zebra");
        server.Tags.Add("apple");

        Assert.Equal(["apple", "Zebra"], server.TagChips.Select(c => c.Text));
    }

    [Fact]
    public void Server_NoDetectedVersion_YieldsOnlyManualChips()
    {
        var server = new ServerConnection();
        server.Tags.Add("test");

        Assert.All(server.TagChips, c => Assert.False(c.IsAutomatic));
        Assert.Single(server.TagChips);
    }

    [Fact]
    public void Server_IsProductionTagged_TracksTheTags()
    {
        var server = new ServerConnection();
        Assert.False(server.IsProductionTagged);

        server.Tags.Add("prod");
        Assert.True(server.IsProductionTagged);
    }

    // ── BlobContainerConfig ──────────────────────────────────────────────────────

    [Fact]
    public void Container_AddingATag_NotifiesTagChips()
    {
        var container = new BlobContainerConfig();

        var changes = CapturePropertyChanges(container, () => container.Tags.Add("prod"));

        Assert.Contains(nameof(BlobContainerConfig.TagChips), changes);
    }

    [Fact]
    public void Container_ClearingAndRefillingTags_NotifiesTagChips()
    {
        var container = new BlobContainerConfig();
        container.Tags.Add("old");

        var changes = CapturePropertyChanges(container, () =>
        {
            container.Tags.Clear();
            container.Tags.Add("new");
        });

        Assert.Contains(nameof(BlobContainerConfig.TagChips), changes);
        Assert.Equal(["new"], container.TagChips.Select(c => c.Text));
    }

    [Fact]
    public void Container_ReplacingTheWholeCollection_StillNotifiesAndResubscribes()
    {
        var container = new BlobContainerConfig { Tags = [new string("prod")] };

        var changes = CapturePropertyChanges(container, () => container.Tags.Add("archive"));

        Assert.Contains(nameof(BlobContainerConfig.TagChips), changes);
        Assert.Equal(2, container.TagChips.Count());
    }

    [Fact]
    public void Container_ChipsAreAllManual()
    {
        var container = new BlobContainerConfig();
        container.Tags.Add("prod");

        Assert.All(container.TagChips, c => Assert.False(c.IsAutomatic));
    }
}
