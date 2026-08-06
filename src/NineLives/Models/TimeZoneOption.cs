namespace Blackcat.NineLives.Models;

/// <summary>
/// A time zone as the container editor offers it (#102).
/// </summary>
/// <param name="Id">The system id stored in config, or null for "not known".</param>
/// <param name="Name">What the picker shows.</param>
public sealed record TimeZoneOption(string? Id, string Name)
{
    /// <summary>
    /// The default. Backup times stay on whichever clock they came from and are labelled rather
    /// than reconciled - which is honest, and exactly what the app did before this setting existed.
    /// </summary>
    public static readonly TimeZoneOption Unknown = new(null, "Not known - leave times as they are");

    /// <summary>
    /// Matches a stored id back to an offered option, falling back to "not known" for an id this
    /// machine does not recognise. A config carried to another machine, or hand-edited, must not
    /// leave the picker showing something the app is not actually using.
    /// </summary>
    public static TimeZoneOption For(string? id, IEnumerable<TimeZoneOption> options)
        => string.IsNullOrWhiteSpace(id)
            ? Unknown
            : options.FirstOrDefault(o => o.Id == id) ?? Unknown;

    /// <summary>The picker renders the selected item through a plain ContentPresenter.</summary>
    public override string ToString() => Name;
}
