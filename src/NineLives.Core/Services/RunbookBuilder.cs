using System.Text;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>What the runbook is built from - everything the restore screen already knows (#240).</summary>
public sealed class RunbookInputs
{
    public required BackupChain Chain { get; init; }
    public required string Script { get; init; }
    public required string TargetDatabase { get; init; }

    public string? ServerName { get; init; }
    public string? ContainerName { get; init; }
    public string? SourceDatabase { get; init; }
    public DateTime? RestorePoint { get; init; }
    public IReadOnlyList<FileMoveOption> FileMoves { get; init; } = [];
    public DateTime GeneratedAt { get; init; } = DateTime.Now;
}

/// <summary>
/// Writes the printed folder for the worst day (#240).
///
/// Every DR plan says "restore the databases" and almost none says HOW: which certificate has to
/// exist first, which credential, which files, in which order, what to run when it fails. The
/// knowledge lives in one person's head, and DR day is defined by that person being on holiday.
/// The app GENERATES all of it per restore - this composes it into one self-contained Markdown
/// document: readable on a laptop with no SQL tools, printable for the change-board pack,
/// committable to the DR repo.
///
/// Pure, deliberately: it states what to verify rather than reaching out to verify it, because
/// the runbook may be generated on a quiet Tuesday and executed the night the source server no
/// longer answers.
/// </summary>
public static class RunbookBuilder
{
    public static string Build(RunbookInputs inputs)
    {
        var sb = new StringBuilder();
        var chain = inputs.Chain;

        // ── title ───────────────────────────────────────────────────────────────
        sb.AppendLine($"# Restore runbook: {inputs.TargetDatabase}");
        sb.AppendLine();
        sb.AppendLine($"Generated {inputs.GeneratedAt:yyyy-MM-dd HH:mm} by Nine Lives {AppVersion.Display}. " +
                      "Everything needed to run this restore is in this one document.");
        sb.AppendLine();
        sb.AppendLine($"| | |");
        sb.AppendLine($"|---|---|");
        if (inputs.SourceDatabase != null) sb.AppendLine($"| Source database | {inputs.SourceDatabase} |");
        sb.AppendLine($"| Restore as | {inputs.TargetDatabase} |");
        if (inputs.ServerName != null) sb.AppendLine($"| Target server | {inputs.ServerName} |");
        if (inputs.ContainerName != null) sb.AppendLine($"| Container | {inputs.ContainerName} |");
        if (inputs.RestorePoint != null) sb.AppendLine($"| Restore point | {inputs.RestorePoint:yyyy-MM-dd HH:mm:ss} |");
        sb.AppendLine($"| Chain | {chain.Summary} |");
        sb.AppendLine($"| Total to read | {ByteSize.Format(chain.AllFiles.Sum(f => f.SizeBytes))} |");
        sb.AppendLine();

        // ── the chain, file by file ─────────────────────────────────────────────
        sb.AppendLine("## 1. The backups this restore reads");
        sb.AppendLine();
        sb.AppendLine("Confirm every file below exists and is reachable from the target server " +
                      "BEFORE starting. A missing stripe fails the restore after `WITH REPLACE` " +
                      "has already dropped the target.");
        sb.AppendLine();

        void DescribeSet(BackupSet set, string label)
        {
            sb.AppendLine($"**{label}** — {set.Timestamp:yyyy-MM-dd HH:mm:ss}" +
                          (set.Position is int p ? $", file position {p}" : "") +
                          $", {set.SizeDisplay}");
            foreach (var file in set.Files)
                sb.AppendLine($"- `{file.RestoreDevice}`");
            sb.AppendLine();
        }

        DescribeSet(chain.FullSet, "Full");
        foreach (var diff in chain.DiffSets) DescribeSet(diff, "Differential");
        for (var i = 0; i < chain.LogSets.Count; i++) DescribeSet(chain.LogSets[i], $"Log {i + 1} of {chain.LogSets.Count}");

        // ── prerequisites, in order ─────────────────────────────────────────────
        sb.AppendLine("## 2. Before the restore, in this order");
        sb.AppendLine();

        var anyBlob = chain.AllFiles.Any(f => !f.IsOnDisk);
        var step = 1;

        if (anyBlob)
        {
            sb.AppendLine($"{step++}. **The server-side credential.** The target instance " +
                          "authenticates to the container itself - the app's own access does not " +
                          "travel. In `master`, confirm a credential exists whose name is the " +
                          "container URL (`SELECT name FROM sys.credentials`), and create it from " +
                          "the Restore screen's credential panel or your documented statement if " +
                          "not. Without it, every `RESTORE ... FROM URL` fails with error 3201.");
        }
        else
        {
            sb.AppendLine($"{step++}. **Readability.** The paths above are opened by the target's " +
                          "SQL Server SERVICE ACCOUNT, not by whoever runs the script. Confirm " +
                          "that account can read them - error 3201 with OS error 5 at restore " +
                          "time means it cannot.");
        }

        var tde = chain.AllFiles.FirstOrDefault(f => f.TdeThumbprint != null)?.TdeThumbprint;
        var enc = chain.AllFiles.FirstOrDefault(f => f.EncryptorThumbprint != null)?.EncryptorThumbprint;

        if (tde != null || enc != null)
        {
            var thumb = tde ?? enc!;
            sb.AppendLine($"{step++}. **The certificate.** " +
                          (tde != null
                              ? "This database is TDE-encrypted; its backups can only be restored " +
                                "where the TDE certificate exists. "
                              : "These backups were taken WITH ENCRYPTION. ") +
                          $"The target's `master` must hold the certificate with thumbprint " +
                          $"`{EncryptionGuidance.Hex(thumb)}` - " +
                          "`SELECT name FROM sys.certificates WHERE thumbprint = " +
                          $"{EncryptionGuidance.Hex(thumb)}` - or the restore fails with error " +
                          "33111. Export it from the source's master with `BACKUP CERTIFICATE`, " +
                          "create it on the target with `CREATE CERTIFICATE ... FROM FILE`. The " +
                          "certificate files and their password belong in the DR kit, not in this " +
                          "document.");
        }
        else
        {
            sb.AppendLine($"{step++}. **Encryption check.** These backups did not declare TDE or " +
                          "backup encryption when this runbook was generated. If the restore " +
                          "fails with error 33111, a certificate is needed - see the source " +
                          "server's `master.sys.certificates`.");
        }

        if (inputs.FileMoves.Count > 0)
        {
            sb.AppendLine($"{step++}. **Disk space.** The files land as follows - confirm each " +
                          "volume has room:");
            sb.AppendLine();
            foreach (var move in inputs.FileMoves.Where(m => !string.IsNullOrWhiteSpace(m.NewPhysicalName)))
                sb.AppendLine($"   - `{move.LogicalName}` → `{move.NewPhysicalName}` " +
                              (move.SizeBytes > 0 ? $"({ByteSize.Format(move.SizeBytes)})" : ""));
        }
        else
        {
            sb.AppendLine($"{step++}. **Disk space.** Sizes are listed per backup above; the " +
                          "restored files land at the paths recorded inside the backup unless the " +
                          "script says `MOVE`. Confirm the volumes have room.");
        }
        sb.AppendLine();

        // ── the script ──────────────────────────────────────────────────────────
        sb.AppendLine("## 3. The restore script");
        sb.AppendLine();
        sb.AppendLine("Exactly as generated - run it batch by batch or whole:");
        sb.AppendLine();
        sb.AppendLine("```sql");
        sb.AppendLine(inputs.Script.TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();

        // ── when it fails ───────────────────────────────────────────────────────
        sb.AppendLine("## 4. If it stops part-way");
        sb.AppendLine();
        sb.AppendLine("A chain that stops leaves the target in `RESTORING` (and `SINGLE_USER`, if " +
                      "sessions were disconnected). Nothing is lost yet - the choices are:");
        sb.AppendLine();
        sb.AppendLine($"- **Carry on**: fix the cause and re-run from the failed statement onward.");
        sb.AppendLine($"- **Give up and come online at the last restored point**: " +
                      $"`RESTORE DATABASE {TSql.QuoteName(inputs.TargetDatabase)} WITH RECOVERY;`");
        sb.AppendLine($"- **Let others back in** (if single-user): " +
                      $"`ALTER DATABASE {TSql.QuoteName(inputs.TargetDatabase)} SET MULTI_USER;`");
        sb.AppendLine();

        // ── after it works ──────────────────────────────────────────────────────
        sb.AppendLine("## 5. Finishing the job");
        sb.AppendLine();
        sb.AppendLine($"- **Prove the data**: `{PostRestoreAdvice.CheckDb(inputs.TargetDatabase).Sql}` — " +
                      "no output means nothing wrong.");
        sb.AppendLine($"- **Orphaned users**: on a different server every SQL-auth user is " +
                      $"orphaned. For each, `USE {TSql.QuoteName(inputs.TargetDatabase)}; " +
                      "ALTER USER [user] WITH LOGIN = [user];` where a same-named login exists.");
        sb.AppendLine("- **Compatibility level and recovery model travel with the backup** - " +
                      "review them if the target is a newer version.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"*Runbook for {inputs.TargetDatabase}, generated {inputs.GeneratedAt:yyyy-MM-dd HH:mm}. " +
                      "Regenerate after any change to the chain or the options - a stale runbook " +
                      "is a wrong runbook that looks right.*");

        return sb.ToString();
    }
}
