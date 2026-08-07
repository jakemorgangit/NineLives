using System.IO;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// A log that writes somewhere disposable.
///
/// Exists because reaching for <c>App.Log</c> from a viewmodel means a TEST run appends to the log
/// file in the profile of whoever ran it. That is not hypothetical: "[headeronly] fake: 1
/// statement(s)" turned up in a real user's log, written by a test - which is the same class of
/// side effect #41 was about, and the reason every viewmodel that logs takes one.
///
/// One obvious helper, so nobody has to invent a temp path at each call site and nobody is tempted
/// to leave the parameter off.
/// </summary>
public static class TestLogs
{
    public static OperationLog Temp() =>
        new(Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n")));
}

/// <summary>
/// An audit cache that lives somewhere disposable.
///
/// Same reason as <see cref="TestLogs"/>, and found the same way: left to find its own, the
/// inventory finds the one in the user's profile. A test run then read and WROTE the real
/// audit-cache.json.
///
/// Worse than the log leak, because the cache decides whether a header is re-read at all - so an
/// entry written by one test silently decided the answer in another, and a test failed for a reason
/// that had nothing to do with the code under test. It littered a file AND changed a result.
/// </summary>
public static class TestAuditStores
{
    public static BackupAuditStore Temp() =>
        new(Path.Combine(Path.GetTempPath(), "ninelives-tests", Guid.NewGuid().ToString("n")));
}
