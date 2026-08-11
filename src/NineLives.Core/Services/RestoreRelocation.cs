using System.IO;
using Blackcat.NineLives.Models;

namespace Blackcat.NineLives.Services;

/// <summary>
/// Moves a restore's files into chosen directories while keeping their names (#299).
///
/// The Terraform case: a freshly provisioned VM's drive layout routinely differs from the
/// source server's, and a restore aimed at the recorded paths fails with directory-not-found
/// mid-run. The rehearsal already relocates - to scratch NAMES, because its copy is
/// disposable. A real restore keeps the original file names; only the directories change.
/// </summary>
public static class RestoreRelocation
{
    /// <summary>
    /// One MOVE per logical file: rows into <paramref name="dataPath"/>, log into
    /// <paramref name="logPath"/>, each keeping its original file name. Two source files
    /// that share a name (different source directories) are disambiguated with a numeric
    /// suffix rather than silently landing on one another.
    /// </summary>
    public static List<FileMoveOption> ToDirectories(
        IReadOnlyList<FileMoveOption> logicalFiles, string dataPath, string logPath)
    {
        var moves = new List<FileMoveOption>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in logicalFiles)
        {
            var isLog = string.Equals(file.Type, "L", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(file.Type, "LOG", StringComparison.OrdinalIgnoreCase);
            var directory = isLog ? logPath : dataPath;

            var name = Path.GetFileName(file.PhysicalName);
            if (string.IsNullOrWhiteSpace(name))
                name = file.LogicalName + (isLog ? ".ldf" : ".mdf");

            var candidate = Path.Combine(directory, name);
            for (var n = 2; !taken.Add(candidate); n++)
                candidate = Path.Combine(directory,
                    $"{Path.GetFileNameWithoutExtension(name)}_{n}{Path.GetExtension(name)}");

            moves.Add(new FileMoveOption
            {
                LogicalName = file.LogicalName,
                PhysicalName = file.PhysicalName,
                Type = file.Type,
                SizeBytes = file.SizeBytes,
                NewPhysicalName = candidate
            });
        }

        return moves;
    }
}
