namespace Blackcat.NineLives.Cli;

/// <summary>
/// Plain aligned columns. No box-drawing, no colour: this output gets piped, grepped and
/// pasted into tickets, and decoration survives none of those. Nothing is truncated either -
/// a cut-off database name is a wrong database name.
/// </summary>
internal static class TableWriter
{
    public static void Write(TextWriter output, string[] headers, IReadOnlyList<string[]> rows)
    {
        var widths = new int[headers.Length];
        for (var c = 0; c < headers.Length; c++)
        {
            widths[c] = headers[c].Length;
            foreach (var row in rows)
                widths[c] = Math.Max(widths[c], row[c].Length);
        }

        WriteRow(output, headers, widths);
        WriteRow(output, headers.Select(h => new string('-', h.Length)).ToArray(), widths);
        foreach (var row in rows)
            WriteRow(output, row, widths);
    }

    private static void WriteRow(TextWriter output, string[] cells, int[] widths)
    {
        for (var c = 0; c < cells.Length; c++)
        {
            // The last column never pads: trailing spaces are invisible until a diff or a
            // pipeline makes them somebody's problem.
            output.Write(c == cells.Length - 1 ? cells[c] : cells[c].PadRight(widths[c] + 2));
        }

        output.WriteLine();
    }
}
