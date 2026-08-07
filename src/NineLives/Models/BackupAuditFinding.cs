namespace Blackcat.NineLives.Models;

/// <summary>What an audit found when it compared a backup's header with what its path claimed.</summary>
public enum BackupAuditVerdict
{
    /// <summary>The header agreed with the path. Most rows, and the point of the exercise.</summary>
    Agrees,

    /// <summary>
    /// The header says this is a different KIND of backup.
    ///
    /// The one that breaks chains outright. A log misread as a full becomes a chain root and every
    /// earlier log is dropped (#44); a full misread as a differential never enters the fulls
    /// collection at all, and if it is the only full the database gets no restore points
    /// whatsoever (#45).
    /// </summary>
    WrongType,

    /// <summary>
    /// The header says this belongs to a different database.
    ///
    /// Quieter and arguably worse: the backup is filed under the wrong database, so it is offered
    /// on a timeline it does not belong to and missing from the one it does.
    /// </summary>
    WrongDatabase,

    /// <summary>The header could not be read - which is itself worth knowing about a backup.</summary>
    Unreadable
}

/// <summary>
/// One backup set, as the path described it and as its header actually reads (#130).
///
/// The audit exists because path-and-filename inference is right most of the time and wrong in ways
/// that are invisible until a restore is needed. A finding is a disagreement worth showing to a
/// person - not something to fix silently, because a container full of them usually means the path
/// pattern is wrong, and correcting the symptoms one at a time hides that.
/// </summary>
/// <param name="SetId">The set as the app knows it.</param>
/// <param name="FileName">A file from the set, so the row can be recognised.</param>
/// <param name="Verdict">What the comparison found.</param>
/// <param name="PathSaid">What the path and filename claimed.</param>
/// <param name="HeaderSaid">What SQL Server read out of the backup itself.</param>
public sealed record BackupAuditFinding(
    string SetId,
    string FileName,
    BackupAuditVerdict Verdict,
    string PathSaid,
    string HeaderSaid)
{
    public bool IsDisagreement => Verdict != BackupAuditVerdict.Agrees;

    /// <summary>
    /// A property rather than a method, because a binding cannot call one - WPF renders nothing and
    /// says nothing, which is how this shipped as an empty amber box in the first render.
    /// </summary>
    public string Description => Verdict switch
    {
        BackupAuditVerdict.Agrees => $"{FileName}: the header agrees.",

        BackupAuditVerdict.WrongType =>
            $"{FileName} was read as {PathSaid} from its path, but its header says {HeaderSaid}.",

        BackupAuditVerdict.WrongDatabase =>
            $"{FileName} was filed under {PathSaid}, but its header says it belongs to {HeaderSaid}.",

        BackupAuditVerdict.Unreadable =>
            $"{FileName} could not be read: {HeaderSaid}",

        _ => FileName
    };
}
