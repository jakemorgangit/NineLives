using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// What Save output on the execution console actually writes (#370).
///
/// The button's tooltip promises "a header naming the server, the target database and the
/// outcome". The method that builds exactly that - and runs it through <see cref="LogRedactor"/>,
/// for the same reason the operation log is redacted: this file gets attached to tickets - existed,
/// was documented, and was called from nowhere. What the button wrote was the raw console: no
/// context, and no redaction.
/// </summary>
public class SavedExecutionLogTests
{
    private static RestoreViewModel Screen()
    {
        var store = new FakeCredentialStore();
        store.Config.Servers.Add(new ServerConnection
        {
            Id = ServerConnection.NewId(),
            Name = "SRV01",
            ServerName = "SRV01"
        });

        return new RestoreViewModel(
            new FakeBlobStorageService(), new FakeSqlServerService(),
            new BackupChainBuilder(), new RestoreScriptGenerator(), store,
            new OperationLog(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "ninelives-saved-log", Guid.NewGuid().ToString("n"))),
            new FakeOperationHistoryStore());
    }

    [Fact]
    public void TheSaveButtonHasSomethingToWriteBesidesTheRawConsole()
    {
        var vm = Screen();

        // The wiring itself is the fix: without it the command falls back to Console.Text.
        Assert.NotNull(vm.Execution.BuildSavedDocument);
    }

    [Fact]
    public void TheSavedDocumentCarriesTheContextTheConsoleDoesNot()
    {
        var vm = Screen();
        vm.TargetDatabaseName = "Sales_Restored";
        vm.Execution.Console.Append("RESTORE DATABASE ... 10 percent processed.");

        // Append BUFFERS - it drains on a 60ms dispatcher timer, and only flushes inline when the
        // calling thread has no dispatcher at all. Which thread xUnit hands this test therefore
        // decided whether it passed, and it passed here and failed on CI. Flush, then read, is
        // what ConsoleBuffer.Flush's own comment tells callers to do.
        vm.Execution.Console.Flush();

        var saved = vm.Execution.BuildSavedDocument!();

        Assert.Contains("Nine Lives - restore execution log", saved);
        Assert.Contains("Sales_Restored", saved);
        Assert.Contains("Outcome:", saved);

        // And the console is still in it - the header is added, not substituted.
        Assert.Contains("10 percent processed", saved);
    }

    /// <summary>
    /// The one that matters. A SAS signature reaching a file somebody attaches to a ticket is the
    /// failure the redactor exists to prevent, and the save path was walking straight past it.
    /// </summary>
    [Fact]
    public void TheSavedDocumentIsRedacted()
    {
        var vm = Screen();
        vm.TargetDatabaseName = "Sales_Restored";
        vm.Execution.Console.Append(
            "RESTORE FROM URL = N'https://acct.blob.core.windows.net/b/x.bak?sv=2022-11-02&sig=SECRETSIGNATUREVALUE'");
        vm.Execution.Console.Flush();

        var saved = vm.Execution.BuildSavedDocument!();

        Assert.DoesNotContain("SECRETSIGNATUREVALUE", saved);
        Assert.Contains("[redacted]", saved);

        // Still readable as a diagnosis: the URL keeps its shape.
        Assert.Contains("acct.blob.core.windows.net", saved);
    }
}
