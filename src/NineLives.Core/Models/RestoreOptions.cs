using CommunityToolkit.Mvvm.ComponentModel;

namespace Blackcat.NineLives.Models;

public enum RecoveryMode
{
    Recovery,
    NoRecovery,
    Standby
}

public class RestoreOptions
{
    /// <summary>
    /// The bucket's region for an s3:// chain (#51), emitted as RESTORE_OPTIONS JSON on
    /// every statement. Ignored - and cleared by the generator - when the chain's devices
    /// are not S3.
    /// </summary>
    public string? S3Region { get; set; }

    public string TargetDatabaseName { get; set; } = string.Empty;
    public bool WithReplace { get; set; } = true;
    public RecoveryMode RecoveryMode { get; set; } = RecoveryMode.Recovery;
    public string? StandbyFilePath { get; set; }
    public bool DisconnectSessions { get; set; } = true;
    public int StatsPercent { get; set; } = 10;
    public DateTime? StopAt { get; set; }

    /// <summary>
    /// The named transaction to stop at, instead of a clock time (#243). For planned risky work
    /// that began with BEGIN TRANSACTION ... WITH MARK, the mark IS the target - not a time
    /// reconstructed afterwards from chat messages and guilt. Mutually exclusive with StopAt;
    /// the mark wins if both are somehow set.
    /// </summary>
    public string? StopAtMark { get; set; }

    /// <summary>
    /// STOPBEFOREMARK instead of STOPATMARK: recover to just BEFORE the marked transaction -
    /// the deployment never happened - rather than to just after it began. Before is the
    /// default, because "undo the deployment" is what the mark was planted for.
    /// </summary>
    public bool StopBeforeMark { get; set; } = true;
    public bool KeepReplication { get; set; }
    public bool EnableBroker { get; set; }
    public bool NewBroker { get; set; }

    /// <summary>
    /// Verify backup checksums as each page is read. Only meaningful if the backup was taken
    /// WITH CHECKSUM - against one that was not, SQL Server fails the restore rather than
    /// skipping the check, which is why this is off by default.
    /// </summary>
    public bool WithChecksum { get; set; }

    /// <summary>
    /// Carry on restoring after a checksum or page error instead of stopping.
    ///
    /// This produces a database that SQL Server has already told you is damaged. It exists for
    /// the case where a partial recovery beats none, and is not something to leave switched on.
    /// </summary>
    public bool ContinueAfterError { get; set; }

    public List<FileMoveOption> FileMoves { get; set; } = [];

    // Deliberately no credential fields here (#42).
    //
    // CredentialName, SqlCredentialName, SasToken and StorageAccountUrl used to live on this
    // object. Every one of them was written by the ViewModel and read by nothing: the generated
    // script carries no WITH CREDENTIAL clause, because SQL Server rejects that for a SAS
    // credential and matches by URL instead (#60).
    //
    // SasToken was the one that mattered - it copied a live SAS token into an options object whose
    // whole purpose is to be handed to a text generator. Nothing was leaking it, but nothing
    // needed it either, and a secret sitting one careless AppendLine away from a script is not
    // worth keeping for a field no code reads. The credential name the app does use lives on the
    // ViewModel, which is what talks to the server about it.
}

/// <summary>
/// One logical file and where it should land.
///
/// Observable because <see cref="NewPhysicalName"/> is edited directly in a grid. As a plain POCO
/// nothing knew the target path had changed, so the generated script kept the path that was
/// fetched from the server rather than the one on screen - and the script is what gets executed.
/// </summary>
public partial class FileMoveOption : ObservableObject
{
    public string LogicalName { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// How big this file will be once restored, from RESTORE FILELISTONLY's Size column.
    ///
    /// It was being read and discarded. Summed per volume against what the target says it has free,
    /// it answers whether the restore can physically fit - which matters because a restore that
    /// runs out of disk fails part-way, after WITH REPLACE has already dropped the database it was
    /// replacing (#32).
    /// </summary>
    public long SizeBytes { get; set; }

    [ObservableProperty]
    private string _newPhysicalName = string.Empty;
}
