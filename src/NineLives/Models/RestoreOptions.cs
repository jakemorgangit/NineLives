namespace Blackcat.NineLives.Models;

public enum RecoveryMode
{
    Recovery,
    NoRecovery,
    Standby
}

public class RestoreOptions
{
    public string TargetDatabaseName { get; set; } = string.Empty;
    public bool WithReplace { get; set; } = true;
    public RecoveryMode RecoveryMode { get; set; } = RecoveryMode.Recovery;
    public string? StandbyFilePath { get; set; }
    public bool DisconnectSessions { get; set; } = true;
    public int StatsPercent { get; set; } = 10;
    public DateTime? StopAt { get; set; }
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
    public string? CredentialName { get; set; }

    public List<FileMoveOption> FileMoves { get; set; } = [];

    // The SAS credential name to use in SQL Server
    public string SqlCredentialName { get; set; } = "BlobRestoreCredential";
    public string? SasToken { get; set; }
    public string? StorageAccountUrl { get; set; }
}

public class FileMoveOption
{
    public string LogicalName { get; set; } = string.Empty;
    public string PhysicalName { get; set; } = string.Empty;
    public string NewPhysicalName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
}
