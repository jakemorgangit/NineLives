namespace Blackcat.NineLives.Models;

/// <summary>
/// One place that turns a byte count into something readable (#42).
///
/// The same while-loop was copied into four SizeDisplay properties across three files. Nothing had
/// gone wrong with it yet, but four copies is four chances for them to drift and start disagreeing
/// about the size of the same backup on different screens.
/// </summary>
public static class ByteSize
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB"];

    /// <summary>e.g. 1536 becomes "1.5 KB". Binary units, matching what SSMS and Explorer show.</summary>
    public static string Format(long bytes)
    {
        // Negative sizes are not physically meaningful, but a subtraction bug upstream should show
        // as an odd number rather than looping forever or printing "-0.0 B".
        if (bytes < 0) return $"{bytes} B";

        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < Units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:F1} {Units[unit]}";
    }
}
