namespace Blackcat.NineLives.Models;

/// <summary>
/// One pill to render: its text, and whether it was typed by a user or derived by the app.
///
/// Manual and automatic tags must share a SINGLE ItemsControl rather than sitting in two
/// side-by-side ones. A WrapPanel measures each child with infinite width in the wrapping
/// direction, so a nested ItemsControl never learns it should wrap - it lays its pills out on one
/// endless line and the row simply clips them. Combining both kinds into one collection gives one
/// WrapPanel that wraps where it should, with the template switching on <see cref="IsAutomatic"/>.
/// </summary>
public sealed record TagChip(string Text, bool IsAutomatic)
{
    public static TagChip Manual(string text) => new(text, false);
    public static TagChip Automatic(string text) => new(text, true);
}
