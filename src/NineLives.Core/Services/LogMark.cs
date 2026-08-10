namespace Blackcat.NineLives.Services;

/// <summary>
/// One marked transaction, as msdb.dbo.logmarkhistory recorded it (#243).
///
/// A mark is planted deliberately - BEGIN TRANSACTION deploy_v2 WITH MARK - before risky work,
/// and afterwards the restore target is the TRANSACTION, not a clock time reconstructed from
/// chat messages. Almost no tooling surfaces logmarkhistory, which is why almost nobody uses
/// the sharpest point-in-time tool SQL Server has.
/// </summary>
/// <param name="Name">The mark's name - what STOPATMARK/STOPBEFOREMARK take.</param>
/// <param name="Description">The free-text description, when one was given.</param>
/// <param name="MarkedAt">When the marked transaction began.</param>
public sealed record LogMark(string Name, string? Description, DateTime MarkedAt)
{
    public string Display =>
        $"{Name} — {MarkedAt:yyyy-MM-dd HH:mm:ss}" +
        (string.IsNullOrWhiteSpace(Description) ? "" : $" ({Description})");
}
