using Blackcat.NineLives.Models;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Cli;

/// <summary>
/// The checks that run before anything destructive (#63's safety defaults) - the same four
/// safety nets the GUI fires before WITH REPLACE drops anything, composed from the same Core
/// services. Each returns a refusal message or nothing; --force downgrades the evidence-based
/// refusals to loud warnings, but never the WITH REPLACE rule - overwriting a database is
/// consented to by its own flag, not by a general-purpose override.
///
/// Best effort by the GUI's own rule: no verdict from silence. A header that cannot be read
/// produces no version or TDE refusal, because refusing on a guess blocks legal restores.
/// </summary>
internal static class Preflights
{
    internal sealed record Result(List<string> Refusals, List<string> Warnings);

    /// <summary>
    /// Everything checkable before the run: does the target database already exist without
    /// consent to overwrite, can the target read the files, does the version direction work,
    /// and is the TDE certificate where the restore will need it.
    /// </summary>
    public static async Task<Result> RunAsync(
        CliServices services, ServerConnection target, BackupChain chain,
        string targetDatabase, bool withReplace, bool force,
        IReadOnlyList<FileMoveOption>? fileMoves = null)
    {
        var refusals = new List<string>();
        var warnings = new List<string>();

        void Verdict(string message)
        {
            if (force) warnings.Add(message + " (--force: proceeding anyway)");
            else refusals.Add(message);
        }

        // 1. WITH REPLACE is its own consent - never inherited, never forced (#63).
        var existing = await services.Sql.GetDatabaseListAsync(target);
        if (!withReplace
            && existing.Contains(targetDatabase, StringComparer.OrdinalIgnoreCase))
        {
            refusals.Add(
                $"'{targetDatabase}' already exists on {target.ServerName}. Overwriting it " +
                "is said with --with-replace, deliberately - there is no other way to mean it.");
        }

        // 2. Can the target's service account actually open the files? Only disk paths have an
        // answer here; blob access fails later with its own clear error.
        var diskPaths = chain.AllSets
            .SelectMany(s => s.Files)
            .Where(f => f.IsOnDisk)
            .Select(f => f.RestoreDevice)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (diskPaths.Count > 0)
        {
            var checks = await services.Sql.CheckBackupFilesAsync(target, diskPaths);
            foreach (var check in checks.Where(c => !c.CanBeRestored))
                Verdict($"{target.ServerName} cannot read {check.Path}: " +
                        (check.ServerMessage ?? check.Problem.ToString()));
        }

        // 3 and 4 both live in the full set's header - one HEADERONLY on the target serves
        // both, device-aware since #203 so blob, shared path and ad-hoc all take this one path.
        var devices = chain.FullSet.Files
            .Select(f => f.IsOnDisk ? f.RestoreDevice : BlobUrlEncoder.Encode(f.BlobUrl))
            .ToList();

        // 0 (checked here, refused hard): can this engine speak S3 at all (#51)? A capability,
        // not evidence, so --force cannot override it: the engine would simply error after WITH
        // REPLACE had already dropped the target. Shared with the app rather than written twice,
        // and asked of the WHOLE chain - a full on Azure carried forward by logs in a bucket
        // still needs the connector.
        if (await S3CapabilityPreflight.RefusalAsync(services.Sql, target, chain) is { } s3Refusal)
            refusals.Add(s3Refusal);

        // 5. Will it physically fit (#297)? The GUI checks before WITH REPLACE drops
        // anything, and this front end is the one most often aimed at a freshly provisioned
        // VM with small disks. FILELISTONLY says how big each file lands, the target says
        // what each volume has, RestoreSpaceCheck judges - including the volume that does
        // not exist on the target at all. Best effort both ways: not being able to check is
        // not the same as not fitting.
        try
        {
            // With relocation in play the caller already knows where each file LANDS - the
            // moves are the truth to check, not the recorded paths (#299).
            var files = (IReadOnlyList<FileMoveOption>?)fileMoves
                        ?? await services.Sql.RestoreFileListOnlyAsync(target, devices);
            if (files.Count > 0)
            {
                var free = await services.Sql.GetVolumeFreeSpaceAsync(target);
                foreach (var volume in RestoreSpaceCheck.Check(files, free).Where(v => !v.Fits))
                    Verdict(volume.Describe);
            }
        }
        catch
        {
            // No verdict from silence.
        }

        BackupFileInfo? header;
        try
        {
            header = await services.Sql.RestoreHeaderOnlyMultiAsync(target, devices);
        }
        catch
        {
            return new Result(refusals, warnings);
        }

        if (header == null) return new Result(refusals, warnings);

        // 3. The one-directional law of RESTORE (#210): newer onto older is error 3169, caught
        // here rather than after the target database is already gone.
        if (header.SoftwareVersionMajor is { } backupMajor)
        {
            var targetMajor = await services.Sql.GetProductMajorVersionAsync(target);
            if (VersionCompatibility.Check(backupMajor, targetMajor)
                == VersionVerdict.Refuse)
            {
                Verdict(
                    $"This backup was taken on {VersionCompatibility.Describe(backupMajor)} " +
                    $"and {target.ServerName} is {VersionCompatibility.Describe(targetMajor!.Value)} - " +
                    "a newer version's backup can never restore onto an older server " +
                    "(error 3169). Restore onto a newer target instead.");
            }
        }

        // 4. The certificate question, asked before the restore rather than answered by error
        // 33111 halfway through the worst morning (#222).
        foreach (var (thumbprint, what) in new[]
                 {
                     (header.TdeThumbprint, "TDE certificate"),
                     (header.EncryptorThumbprint, "backup-encryption certificate")
                 })
        {
            if (thumbprint == null) continue;

            string? found = null;
            try
            {
                found = await services.Sql.FindCertificateByThumbprintAsync(target, thumbprint);
            }
            catch
            {
                continue;
            }

            if (found == null)
                Verdict(
                    $"The {what} with thumbprint 0x{Convert.ToHexString(thumbprint)} is not on " +
                    $"{target.ServerName}. Restore its certificate there first (error 33111 " +
                    "otherwise) - the app's encryption panel spells out the export/import route.");
        }

        return new Result(refusals, warnings);
    }
}
