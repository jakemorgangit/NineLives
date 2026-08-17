using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// Carrying a login across from the instance the backup came from (#459).
///
/// The honest gap in the unmappable-orphan advice: it says create the login first, with a password
/// set by whoever owns the account, and leaves somebody holding a database whose users cannot log
/// in. Inventing a password would be wrong - but there was a third option it never offered, which
/// is to go and fetch the real one.
///
/// Run on the source, the script emits a CREATE LOGIN carrying the original password hash AND the
/// original SID. The hash means applications keep the password they already have; the SID means
/// the restored user is not orphaned at all, so there is no ALTER USER to run afterwards and no
/// window where its permissions are wrong.
/// </summary>
public class CarryTheLoginAcrossTests
{
    [Fact]
    public void TheScriptCarriesBothTheHashAndTheSid()
    {
        var sql = PostRestoreAdvice.BuildLoginCaptureScript("app_user");

        // The hash, so nobody has to be told a new password.
        Assert.Contains("LOGINPROPERTY(sl.name, 'PasswordHash')", sql);
        Assert.Contains("HASHED", sql);

        // The SID, which is what stops the user being orphaned in the first place.
        Assert.Contains("SID = ", sql);
        Assert.Contains("sl.sid", sql);
    }

    /// <summary>
    /// The properties that make the recreated login behave like the original rather than merely
    /// authenticate like it.
    /// </summary>
    [Fact]
    public void ItCarriesTheLoginsOwnSettingsToo()
    {
        var sql = PostRestoreAdvice.BuildLoginCaptureScript("app_user");

        Assert.Contains("DEFAULT_DATABASE", sql);
        Assert.Contains("DEFAULT_LANGUAGE", sql);
        Assert.Contains("CHECK_POLICY", sql);
        Assert.Contains("CHECK_EXPIRATION", sql);

        // A login disabled on the source must not arrive enabled on the target.
        Assert.Contains("is_disabled", sql);
        Assert.Contains("ALTER LOGIN ", sql);
        Assert.Contains("DISABLE", sql);
    }

    /// <summary>
    /// The name is compared against sys.sql_logins, so it is a LITERAL - and an apostrophe in it
    /// would otherwise close the string and turn the rest into commands. QUOTENAME inside the
    /// script does the identifier quoting for the statement it emits, on the far side.
    /// </summary>
    [Fact]
    public void AnApostropheInTheLoginNameIsEscapedAsALiteral()
    {
        var sql = PostRestoreAdvice.BuildLoginCaptureScript("o'brien");

        Assert.Contains("N'o''brien'", sql);
        Assert.Contains("QUOTENAME(sl.name)", sql);
    }

    /// <summary>
    /// A Windows login has no password here to carry and takes its SID from Active Directory, so
    /// finding nothing is the expected answer for one - said out loud rather than returning an
    /// empty result somebody has to interpret.
    /// </summary>
    [Fact]
    public void AWindowsLoginIsExplainedRatherThanSilentlyFindingNothing()
    {
        var sql = PostRestoreAdvice.BuildLoginCaptureScript(@"DOMAIN\service");

        Assert.Contains("THROW", sql);
        Assert.Contains("FROM WINDOWS", sql);
    }

    /// <summary>It reads; it does not write. Run on a production source, that is the whole point.</summary>
    [Fact]
    public void ItChangesNothingOnTheInstanceItRunsOn()
    {
        var sql = PostRestoreAdvice.BuildLoginCaptureScript("app_user");

        Assert.DoesNotContain("CREATE LOGIN [", sql);   // it BUILDS the text, never executes it
        Assert.DoesNotContain("EXEC(", sql);
        Assert.DoesNotContain("sp_executesql", sql);
        Assert.Contains("SELECT @Command AS [RunThisOnTheTarget]", sql);
    }

    // ── how it is offered ───────────────────────────────────────────────────────

    [Fact]
    public void TheActionSaysWhereToRunItAndIsNotRunnableHere()
    {
        var action = PostRestoreAdvice.CaptureLoginFromSource("app_user");

        Assert.Contains("app_user", action.Title);
        Assert.Contains("came FROM", action.Caution);
        Assert.False(action.Runnable);
    }

    /// <summary>
    /// And it says why this beats creating a login by hand: not just the password, but that the
    /// matching SID removes the orphan rather than papering over it.
    /// </summary>
    [Fact]
    public void ItExplainsWhyTheSidMatters()
    {
        var action = PostRestoreAdvice.CaptureLoginFromSource("app_user");

        Assert.Contains("SID", action.Caution);
        Assert.Contains("orphaned", action.Caution);
    }
}
