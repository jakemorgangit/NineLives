using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Blackcat.NineLives.Services;

namespace Blackcat.NineLives.Views;

/// <summary>
/// A read-only TextBlock that renders T-SQL with syntax colouring.
///
/// A TextBlock of Runs rather than a RichTextBox: the script is display-only, and a RichTextBox
/// brings an editing surface, its own scrolling model and a FlowDocument to keep in sync for no
/// benefit here. Selection is not needed either - Copy to Clipboard already exists.
/// </summary>
public sealed class SqlTextBlock : TextBlock
{
    public static readonly DependencyProperty SqlProperty = DependencyProperty.Register(
        nameof(Sql), typeof(string), typeof(SqlTextBlock),
        new PropertyMetadata(string.Empty, OnSqlChanged));

    public string Sql
    {
        get => (string)GetValue(SqlProperty);
        set => SetValue(SqlProperty, value);
    }

    private static void OnSqlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SqlTextBlock)d).Rebuild();

    private void Rebuild()
    {
        Inlines.Clear();

        foreach (var token in SqlSyntaxHighlighter.Tokenise(Sql))
        {
            var run = new Run(token.Text);
            var brush = BrushFor(token.Kind);
            if (brush != null) run.Foreground = brush;
            if (token.Kind == SqlTokenKind.Keyword) run.FontWeight = FontWeights.SemiBold;
            Inlines.Add(run);
        }
    }

    /// <summary>
    /// Resolved from the theme rather than hardcoded, so the script pane and the console stay in
    /// the same palette. Falls back to inheriting the foreground if a key is missing.
    /// </summary>
    private Brush? BrushFor(SqlTokenKind kind)
    {
        var key = kind switch
        {
            SqlTokenKind.Keyword => "SqlKeywordBrush",
            SqlTokenKind.Literal => "SqlLiteralBrush",
            SqlTokenKind.Comment => "SqlCommentBrush",
            SqlTokenKind.Number => "SqlNumberBrush",
            SqlTokenKind.Identifier => "SqlIdentifierBrush",
            _ => null
        };

        return key != null ? TryFindResource(key) as Brush : null;
    }
}
