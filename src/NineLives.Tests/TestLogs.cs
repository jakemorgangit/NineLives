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
