using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// The T-SQL that writes a server-side blob credential (#147).
///
/// Pulled out as pure text because it is the part that can actually be proven without Azure. What
/// this issue cannot prove locally is that a restore then AUTHENTICATES with a managed identity -
/// that needs an Azure VM, an Arc-enabled instance or SQL MI - so the statement itself is where the
/// testable value is.
///
/// The two identities differ in exactly one way that matters: a managed-identity credential has no
/// SECRET. Not an empty one - none at all. Writing <c>SECRET = ''</c> would be a different thing.
/// </summary>
public static class BlobCredentialStatement
{
    /// <summary>
    /// The statement to run.
    /// </summary>
    /// <param name="credentialName">Validated and quoted by the caller's rules, not concatenated raw.</param>
    /// <param name="identity">Which identity the credential should hold.</param>
    /// <param name="sasToken">The token, for a SAS credential. Ignored for a managed identity.</param>
    /// <param name="exists">
    /// Whether a credential of this name is already there.
    ///
    /// ALTER rather than DROP and CREATE, because a credential is server-scoped shared state:
    /// dropping it even for the moment between two statements breaks anything else relying on it at
    /// that instant - a backup job writing to the same container, most obviously.
    /// </param>
    public static string Build(
        string credentialName, BlobCredentialIdentity identity, string? sasToken, bool exists)
    {
        TSql.ValidateIdentifier(credentialName, nameof(credentialName));

        var verb = exists ? "ALTER" : "CREATE";
        var quotedName = TSql.QuoteName(credentialName);

        return identity switch
        {
            // No SECRET clause at all.
            //
            // On an existing SAS credential this ALTER is what CONVERTS it: SQL Server takes the
            // new identity and nulls the stored secret. That is the intended behaviour and it is
            // destructive, which is why nothing reaches this except somebody asking for it - the
            // execute path stops and reports an identity it does not recognise rather than
            // "fixing" one that may be working perfectly well (#145).
            BlobCredentialIdentity.ManagedIdentity =>
                $"{verb} CREDENTIAL {quotedName} WITH IDENTITY = 'Managed Identity'",

            BlobCredentialIdentity.SharedAccessSignature =>
                $"{verb} CREDENTIAL {quotedName}{Environment.NewLine}" +
                $"WITH IDENTITY = 'SHARED ACCESS SIGNATURE',{Environment.NewLine}" +
                $"SECRET = '{TSql.EscapeLiteral((sasToken ?? string.Empty).TrimStart('?'))}'",

            _ => throw new ArgumentOutOfRangeException(
                nameof(identity),
                identity,
                "Only a SAS or managed-identity credential can be written. Anything else on the " +
                "server was put there by something that is not this app, and overwriting it blind " +
                "would replace an identity that may be authenticating perfectly well.")
        };
    }

    /// <summary>
    /// Whether an instance can authenticate to blob storage with a managed identity at all.
    ///
    /// The gate matters more than it looks. <c>CREATE CREDENTIAL</c> takes its identity as free
    /// TEXT on every version, so an ungated app writes a credential that looks perfectly fine, sits
    /// in sys.credentials, reads back correctly - and fails at restore time. That is the worst kind
    /// of deferred failure: everything says yes until the moment it matters.
    /// </summary>
    /// <param name="productMajorVersion">SERVERPROPERTY('ProductMajorVersion'). 16 is SQL Server 2022.</param>
    /// <param name="engineEdition">SERVERPROPERTY('EngineEdition'). 8 is Azure SQL Managed Instance.</param>
    public static bool SupportsManagedIdentity(int? productMajorVersion, int? engineEdition) =>
        engineEdition == AzureSqlManagedInstance || productMajorVersion >= FirstVersionWithManagedIdentity;

    /// <summary>SQL Server 2022. Earlier versions accept the text and cannot use it.</summary>
    public const int FirstVersionWithManagedIdentity = 16;

    /// <summary>EngineEdition 8. Has had managed identity support regardless of version.</summary>
    public const int AzureSqlManagedInstance = 8;

    /// <summary>
    /// Why this instance cannot use one, in terms of what to do instead.
    ///
    /// Named rather than a bare "not supported": somebody who has just been told their organisation
    /// forbids SAS tokens, and is now told the alternative is unavailable, deserves to know which
    /// of the two things to go and change.
    /// </summary>
    public static string ExplainUnsupported(int? productMajorVersion) =>
        productMajorVersion is { } version
            ? $"This instance is SQL Server major version {version}. Authenticating to blob storage " +
              "with a managed identity needs SQL Server 2022 (version 16) or later, or Azure SQL " +
              "Managed Instance. On an earlier version the credential can be created and will look " +
              "correct, but the restore fails when it tries to use it - so it is not offered here."
            : "This instance did not report its version, so whether it can authenticate with a " +
              "managed identity is unknown. Managed identity needs SQL Server 2022 or later, or " +
              "Azure SQL Managed Instance.";

    /// <summary>
    /// What the credential alone does NOT buy.
    ///
    /// The statement succeeds whether or not the instance actually has a managed identity, and
    /// whether or not that identity can read the container. Both failures then surface as a 403
    /// from Azure at restore time, which says nothing about which identity was refused - the same
    /// trap #144 addressed on the browsing side.
    /// </summary>
    public const string WhatItStillNeeds =
        "Creating the credential does not grant anything. The instance itself needs a managed " +
        "identity - an Azure VM with a system or user-assigned identity, an Arc-enabled instance, " +
        "or SQL MI - and that identity needs the Storage Blob Data Reader role on this container " +
        "to restore from it, or Storage Blob Data Contributor to back up to it. Owner and " +
        "Contributor on the storage account are NOT enough: they grant management of the account, " +
        "not access to the data inside it.";
}
