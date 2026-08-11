using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// WITH MOVE, for the verbs that generate a restore (#299, #370).
///
/// Extracted from <c>restore</c> so <c>script</c> can relocate too. The workflow that needed it
/// is the one where the two are used together: generate the script here, hand it to a DBA for the
/// change window, and have them run it on a freshly provisioned machine that does not have the
/// source server's drive layout. Without MOVE clauses that restore fails at run time with a
/// directory-not-found - after WITH REPLACE has already dropped the target - and <c>script</c>
/// silently dropped them while <c>restore</c>, doing the same job a different way, did not.
///
/// Relocation cannot be done offline. The logical file NAMES come from RESTORE FILELISTONLY, and
/// the default directories come from the instance itself, so both need a server to ask. That is
/// why <c>script</c> - which otherwise touches nothing - takes a <c>--target</c> purely to answer
/// these two questions, and refuses the relocation flags without one rather than emitting a
/// script that quietly has no MOVE in it.
/// </summary>
internal static class Relocation
{
    /// <summary>The flags that mean "relocate", on any verb that offers them.</summary>
    internal static readonly string[] ValuedOptions = ["data-path", "log-path"];

    internal static bool WasAskedFor(CliArguments args) =>
        args.Has("relocate") || args.Get("data-path") != null || args.Get("log-path") != null;

    /// <summary>
    /// Resolves the MOVE clauses, or explains why it could not.
    ///
    /// Returns (null, null) when relocation was not asked for at all - the caller carries on with
    /// no MOVE, which is the correct restore for a target whose layout matches the source.
    /// </summary>
    internal static async Task<(List<FileMoveOption>? Moves, string? Error)> ResolveAsync(
        CliArguments args,
        CliServices services,
        ServerConnection target,
        BackupChain chain,
        Action<string> report,
        CancellationToken ct)
    {
        if (!WasAskedFor(args)) return (null, null);

        var devices = chain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();

        List<FileMoveOption> logicalFiles;
        try
        {
            logicalFiles = await services.Sql.RestoreFileListOnlyAsync(target, devices, ct);
        }
        catch (Exception ex)
        {
            return (null, $"Could not read the backup's file list from {target.ServerName}, " +
                          $"which relocation needs: {ex.Message}");
        }

        var dataDir = args.Get("data-path");
        var logDir = args.Get("log-path");

        // Explicit directories win; either side not given falls back to the target's own
        // defaults, so --data-path alone is a complete instruction rather than half of one.
        if (dataDir == null || logDir == null)
        {
            try
            {
                var (defaultData, defaultLog) = await services.Sql.GetDefaultPathsAsync(target, ct);
                dataDir ??= defaultData;
                logDir ??= defaultLog;
            }
            catch (Exception ex)
            {
                return (null, $"Could not read the default data and log directories from " +
                              $"{target.ServerName}, which relocation needs when a directory is " +
                              $"not given: {ex.Message}");
            }
        }

        var moves = RestoreRelocation.ToDirectories(logicalFiles, dataDir, logDir);
        report($"Relocating {moves.Count} file(s): data to {dataDir}, log to {logDir}.");
        return (moves, null);
    }

    /// <summary>
    /// The refusal for a verb that was given relocation flags and no instance to ask.
    ///
    /// Named rather than silent: emitting a script with no MOVE in it, when MOVE is exactly what
    /// was asked for, produces a file that looks right and fails in the change window.
    /// </summary>
    internal static string NeedsATarget =>
        "Relocation needs an instance to ask. The logical file names come from RESTORE " +
        "FILELISTONLY and the default directories come from the instance itself, so neither can " +
        "be worked out offline. Add --target NAME (the instance the script will eventually run " +
        "on), or drop --relocate, --data-path and --log-path.";
}
