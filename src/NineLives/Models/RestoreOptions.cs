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
