using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Writing a managed-identity credential (#147).
///
/// #145 taught the app to RECOGNISE one and leave it alone. This is creating one, which closes the
/// SAS-free path #29 opened: an organisation that forbids long-lived SAS tokens could browse a
/// container without one and then still not restore without one, because the credential this app
/// wrote was always a SAS.
///
/// What can be proven here is the statement text and the version gate. What cannot - on-prem, with
/// no Azure VM, Arc-enabled instance or SQL MI - is that a restore then AUTHENTICATES with it.
/// </summary>
public class ManagedIdentityCredentialTests
{
    private const string Name = "https://acct.blob.core.windows.net/backups";

    // ── the statement ───────────────────────────────────────────────────────────

    /// <summary>
    /// No SECRET clause at all - not an empty one. <c>SECRET = ''</c> would be a different thing,
    /// and SQL Server would take it.
    /// </summary>
    [Fact]
    public void AManagedIdentityCredentialCarriesNoSecret()
    {
        var sql = BlobCredentialStatement.Build(
            Name, BlobCredentialIdentity.ManagedIdentity, "sv=2022&sig=abc", exists: false);

        Assert.Contains("WITH IDENTITY = 'Managed Identity'", sql);
        Assert.DoesNotContain("SECRET", sql);
        Assert.DoesNotContain("sig=abc", sql);
    }

    /// <summary>A token that happens to be lying around must not end up in the statement.</summary>
    [Fact]
    public void ATokenIsIgnoredEntirelyForAManagedIdentity()
    {
        var sql = BlobCredentialStatement.Build(
            Name, BlobCredentialIdentity.ManagedIdentity, "sv=2022&sig=SHOULDNOTAPPEAR", exists: true);

        Assert.DoesNotContain("SHOULDNOTAPPEAR", sql);
    }

    [Fact]
    public void ASasCredentialStillCarriesItsToken()
    {
        var sql = BlobCredentialStatement.Build(
            Name, BlobCredentialIdentity.SharedAccessSignature, "?sv=2022&sig=abc", exists: false);

        Assert.Contains("WITH IDENTITY = 'SHARED ACCESS SIGNATURE'", sql);
        Assert.Contains("SECRET = 'sv=2022&sig=abc'", sql);
    }

    /// <summary>The leading '?' is part of a URL query, not of the token.</summary>
    [Fact]
    public void ALeadingQuestionMarkIsStrippedFromTheToken()
    {
        var sql = BlobCredentialStatement.Build(
            Name, BlobCredentialIdentity.SharedAccessSignature, "?sv=2022", exists: false);

        Assert.DoesNotContain("'?sv", sql);
    }

    /// <summary>
    /// ALTER rather than DROP and CREATE. A credential is server-scoped shared state: dropping it
    /// even for the moment between two statements breaks anything else relying on it at that
    /// instant - a backup job writing to the same container, most obviously.
    /// </summary>
    [Theory]
    [InlineData(BlobCredentialIdentity.ManagedIdentity)]
    [InlineData(BlobCredentialIdentity.SharedAccessSignature)]
    public void AnExistingCredentialIsAlteredRatherThanReplaced(BlobCredentialIdentity identity)
    {
        var sql = BlobCredentialStatement.Build(Name, identity, "sv=2022", exists: true);

        Assert.StartsWith("ALTER CREDENTIAL", sql);
        Assert.DoesNotContain("DROP CREDENTIAL", sql);
    }

    /// <summary>
    /// The conversion, which is the whole mechanism: ALTER with no SECRET is what turns a SAS
    /// credential into a managed-identity one, and nulls the stored secret as it goes.
    /// </summary>
    [Fact]
    public void AlteringToAManagedIdentityIsWhatConvertsASasCredential()
    {
        var sql = BlobCredentialStatement.Build(
            Name, BlobCredentialIdentity.ManagedIdentity, "sv=2022", exists: true);

        Assert.Equal(
            $"ALTER CREDENTIAL [{Name}] WITH IDENTITY = 'Managed Identity'", sql);
    }

    /// <summary>
    /// The name is an identifier, so it is quoted rather than concatenated - and an embedded ']'
    /// is doubled, or the brackets do not delimit anything. A name is auto-populated from a URL in
    /// a plain-text config file and is editable free text, so it is attacker-reachable.
    /// </summary>
    [Fact]
    public void ABracketInTheNameCannotBreakOutOfTheIdentifier()
    {
        var sql = BlobCredentialStatement.Build(
            "https://[fe80::1]:10000/devstore", BlobCredentialIdentity.ManagedIdentity, null, false);

        Assert.Contains("[https://[fe80::1]]:10000/devstore]", sql);
    }

    /// <summary>
    /// Anything else on the server was put there by something that is not this app, and
    /// overwriting it blind would replace an identity that may be authenticating perfectly well.
    /// </summary>
    [Theory]
    [InlineData(BlobCredentialIdentity.Other)]
    [InlineData(BlobCredentialIdentity.Missing)]
    public void AnIdentityThisAppDoesNotWriteIsRefusedRatherThanGuessedAt(BlobCredentialIdentity identity)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => BlobCredentialStatement.Build(Name, identity, null, false));

    // ── the version gate ────────────────────────────────────────────────────────

    /// <summary>
    /// The gate matters more than it looks. CREATE CREDENTIAL takes its identity as free TEXT on
    /// every version, so an ungated app writes a credential that looks perfectly fine, sits in
    /// sys.credentials, reads back correctly - and fails at restore time.
    /// </summary>
    [Theory]
    [InlineData(16)]  // SQL Server 2022
    [InlineData(17)]
    public void ManagedIdentityIsOfferedOnSqlServer2022AndLater(int version)
        => Assert.True(BlobCredentialStatement.SupportsManagedIdentity(version, engineEdition: 3));

    [Theory]
    [InlineData(13)]  // 2016
    [InlineData(14)]  // 2017
    [InlineData(15)]  // 2019
    public void ManagedIdentityIsNotOfferedBefore2022(int version)
        => Assert.False(BlobCredentialStatement.SupportsManagedIdentity(version, engineEdition: 3));

    /// <summary>Azure SQL Managed Instance, whatever it reports as a version.</summary>
    [Fact]
    public void AzureSqlManagedInstanceIsOfferedItRegardlessOfVersion()
        => Assert.True(BlobCredentialStatement.SupportsManagedIdentity(
            productMajorVersion: 12, engineEdition: BlobCredentialStatement.AzureSqlManagedInstance));

    /// <summary>An instance that said nothing is not assumed capable.</summary>
    [Fact]
    public void AnInstanceThatDidNotSayIsNotAssumedCapable()
        => Assert.False(BlobCredentialStatement.SupportsManagedIdentity(null, null));

    /// <summary>
    /// Named rather than a bare "not supported". Somebody who has just been told their organisation
    /// forbids SAS tokens, and is now told the alternative is unavailable, deserves to know which
    /// of the two things to go and change.
    /// </summary>
    [Fact]
    public void TheRefusalNamesTheVersionAndWhatIsNeeded()
    {
        var text = BlobCredentialStatement.ExplainUnsupported(15);

        Assert.Contains("version 15", text);
        Assert.Contains("SQL Server 2022", text);
        Assert.Contains("Managed Instance", text);
    }

    [Fact]
    public void ARefusalWithNoVersionSaysThatToo()
        => Assert.Contains("did not report its version", BlobCredentialStatement.ExplainUnsupported(null));

    // ── what the credential alone does not buy ──────────────────────────────────

    /// <summary>
    /// The statement succeeds whether or not the instance has an identity at all, and whether or
    /// not it can read the container. Both then surface as a 403 at restore time, which says
    /// nothing about which identity was refused - the trap #144 addressed for browsing.
    /// </summary>
    [Fact]
    public void TheCaveatNamesTheRoleAndSaysWhatIsNotEnough()
    {
        var text = BlobCredentialStatement.WhatItStillNeeds;

        Assert.Contains("Storage Blob Data Reader", text);
        Assert.Contains("Contributor", text);
        Assert.Contains("NOT enough", text);
    }

    // ── the record that carries the answer ──────────────────────────────────────

    [Fact]
    public void ASupportedInstanceExplainsNothingBecauseThereIsNothingToExplain()
        => Assert.Equal(string.Empty, new ManagedIdentitySupport(true, 16, 3).Explain());

    [Fact]
    public void AnUnsupportedInstanceExplainsItselfWithItsOwnVersion()
        => Assert.Contains("version 15", new ManagedIdentitySupport(false, 15, 3).Explain());
}
