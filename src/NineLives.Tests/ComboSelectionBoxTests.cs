using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Blackcat.NineLives.Services;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The combo's CLOSED box, not its open list (#269).
///
/// DisplayMemberPath works through the items control's template SELECTOR, and a combo template
/// must forward that selector to the selection-box presenter the way the stock template does.
/// Without it the open list shows the right member while the closed box falls back to
/// ToString() - "LogMark { Name = deploy_v2, ... }" rendered straight at the user - a failure
/// the load tests cannot see, because the binding that breaks is one WPF never even creates.
/// </summary>
[Collection(WpfCollection.Name)]
public class ComboSelectionBoxTests(WpfFixture wpf)
{
    [Fact]
    public void TheClosedBoxShowsTheDisplayMemberNotToString()
    {
        wpf.Invoke(() =>
        {
            var combo = new ComboBox
            {
                ItemsSource = new[]
                {
                    new LogMark("deploy_v2", "v2 schema deployment", new DateTime(2026, 8, 5, 21, 30, 0))
                },
                DisplayMemberPath = nameof(LogMark.Display),
                SelectedIndex = 0,
                Style = (Style)Application.Current.FindResource("DarkComboBox")
            };

            combo.ApplyTemplate();
            combo.Measure(new Size(800, 100));
            combo.Arrange(new Rect(0, 0, 800, 100));
            combo.UpdateLayout();

            var texts = TextsUnder(combo);

            Assert.Contains(texts, t => t.Contains("deploy_v2 — 2026-08-05 21:30:00"));
            Assert.DoesNotContain(texts, t => t.Contains("LogMark {"));
        });
    }

    /// <summary>Every rendered TextBlock's text. The closed popup realises nothing, so what this
    /// finds is the selection box (and the collapsed placeholder) - exactly the surface under test.</summary>
    private static List<string> TextsUnder(DependencyObject root)
    {
        var texts = new List<string>();
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock block) texts.Add(block.Text);
            texts.AddRange(TextsUnder(child));
        }

        return texts;
    }
}
