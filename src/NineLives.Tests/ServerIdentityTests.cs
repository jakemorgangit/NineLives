using Blackcat.NineLives.Models;
using Xunit;

namespace Blackcat.NineLives.Tests;

public class ServerIdentityTests
{
    // ── Format ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Format_ServerAndInstance_JoinsWithBackslash()
        => Assert.Equal(@"SQLHOST\PROD", ServerIdentity.Format("SQLHOST", "PROD"));

    [Fact]
    public void Format_ServerOnly_ReturnsBareServer()
        => Assert.Equal("SQLHOST", ServerIdentity.Format("SQLHOST", null));

    [Fact]
    public void Format_ServerWithEmptyInstance_ReturnsBareServer()
        => Assert.Equal("SQLHOST", ServerIdentity.Format("SQLHOST", "   "));

    [Fact]
    public void Format_NeitherPresent_ReturnsNull()
        => Assert.Null(ServerIdentity.Format(null, null));

    [Fact]
    public void Format_AgClusterName_PassesThroughUnchanged()
        => Assert.Equal("mycluster01$My-AG1", ServerIdentity.Format("mycluster01$My-AG1", null));

    // ── Matches: the defect this exists to prevent ───────────────────────────────

    [Fact]
    public void Matches_DifferentInstanceOnSameHost_DoesNotMatch()
    {
        // The bug: comparing only the host meant selecting SQLHOST\PROD also matched
        // SQLHOST\TEST, so both instances' backups landed in one restore timeline.
        Assert.False(ServerIdentity.Matches("SQLHOST", "TEST", @"SQLHOST\PROD"));
    }

    [Fact]
    public void Matches_SameHostAndInstance_Matches()
        => Assert.True(ServerIdentity.Matches("SQLHOST", "PROD", @"SQLHOST\PROD"));

    [Fact]
    public void Matches_IsCaseInsensitive()
        => Assert.True(ServerIdentity.Matches("sqlhost", "prod", @"SQLHOST\PROD"));

    [Fact]
    public void Matches_BareHostFilter_MatchesOnlyBackupsWithNoInstance()
    {
        // A container that mixes both shapes lists SQLHOST and SQLHOST\PROD as separate
        // dropdown entries, so a bare host must not silently swallow every instance under it.
        Assert.True(ServerIdentity.Matches("SQLHOST", null, "SQLHOST"));
        Assert.False(ServerIdentity.Matches("SQLHOST", "PROD", "SQLHOST"));
    }

    [Fact]
    public void Matches_BareHostFilter_TreatsEmptyInstanceAsNoInstance()
    {
        // Path parsing can yield either null or empty; they must behave identically.
        Assert.True(ServerIdentity.Matches("SQLHOST", "", "SQLHOST"));
        Assert.True(ServerIdentity.Matches("SQLHOST", "  ", "SQLHOST"));
    }

    [Fact]
    public void Matches_InstanceFilter_DoesNotMatchBackupWithoutInstance()
        => Assert.False(ServerIdentity.Matches("SQLHOST", null, @"SQLHOST\PROD"));

    [Fact]
    public void Matches_DifferentHost_DoesNotMatch()
        => Assert.False(ServerIdentity.Matches("OTHERHOST", "PROD", @"SQLHOST\PROD"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_EmptyFilter_MatchesEverything(string? filter)
    {
        Assert.True(ServerIdentity.Matches("SQLHOST", "PROD", filter));
        Assert.True(ServerIdentity.Matches(null, null, filter));
    }

    [Fact]
    public void Matches_RoundTripsWithFormat()
    {
        // Whatever Format produces for the dropdown must match the pair it came from -
        // producer and matcher cannot be allowed to drift.
        foreach (var (server, instance) in new[]
        {
            ("SQLHOST", "PROD"),
            ("SQLHOST", (string?)null),
            ("mycluster01$My-AG1", null)
        })
        {
            var display = ServerIdentity.Format(server, instance);
            Assert.True(ServerIdentity.Matches(server, instance, display),
                $"Format produced '{display}' which does not match its own source pair.");
        }
    }
}
