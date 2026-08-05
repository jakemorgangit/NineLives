using System.Windows;
using System.Windows.Media;
using Blackcat.NineLives.Services;
using Blackcat.NineLives.ViewModels;
using Blackcat.NineLives.Views;
using Xunit;

namespace Blackcat.NineLives.Tests;

/// <summary>
/// The palettes and the theme switch.
///
/// The failure this guards against is silent: a brush referenced with DynamicResource that a
/// palette does not define does NOT throw. WPF resolves it to nothing and the element renders
/// with no brush at all - white text on white, or an invisible border. Nothing in the build or
/// the XAML load tests catches it, which is why the key sets are compared directly.
/// </summary>
[Collection(WpfCollection.Name)]
public class ThemeTests(WpfFixture wpf)
{
    private static HashSet<string> KeysOf(ResourceDictionary dictionary)
        => dictionary.Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();

    [Theory]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.HighContrast)]
    public void EveryPaletteDefinesExactlyTheSameKeys(AppTheme theme)
    {
        HashSet<string> dark = [], other = [];
        wpf.Invoke(() =>
        {
            dark = KeysOf(ThemeManager.Load(AppTheme.Dark));
            other = KeysOf(ThemeManager.Load(theme));
        });

        var missing = dark.Except(other).OrderBy(k => k).ToList();
        var extra = other.Except(dark).OrderBy(k => k).ToList();

        Assert.True(missing.Count == 0,
            $"{theme} is missing: {string.Join(", ", missing)}. Anything using these renders with " +
            "no brush at all - white on white, or an invisible border.");
        Assert.True(extra.Count == 0,
            $"{theme} defines keys the dark palette does not: {string.Join(", ", extra)}. " +
            "Switching back to Dark would leave them unresolved.");
    }

    [Fact]
    public void APaletteDefinesEnoughToBeAPalette()
    {
        // Guards the comparison above: two empty dictionaries also have identical key sets.
        wpf.Invoke(() => Assert.True(KeysOf(ThemeManager.Load(AppTheme.Dark)).Count > 50));
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.HighContrast)]
    public void EveryThemeSuppliesTheBrushesTheAppAsksForByName(AppTheme theme)
    {
        // A spot check of the keys used in the most places, resolved as brushes rather than just
        // present as keys - a Color where a Brush is expected would bind to nothing.
        string[] required =
        [
            "WindowBackgroundBrush", "SidebarBackgroundBrush", "CardBackgroundBrush",
            "CardBorderBrush", "InputBackgroundBrush", "AccentBrush", "AccentForegroundBrush",
            "PrimaryTextBrush", "SecondaryTextBrush", "DisabledTextBrush",
            "SuccessBrush", "ErrorBrush", "WarningBrush",
            "SuccessPanelBackgroundBrush", "WarningPanelBorderBrush", "DangerPanelBorderBrush",
            "ConsoleBackgroundBrush", "ConsoleTextBrush", "SqlKeywordBrush",
            "OverlayBrush", "AlternatingRowBackgroundBrush", "UpdateBannerBrush"
        ];

        wpf.Invoke(() =>
        {
            var palette = ThemeManager.Load(theme);
            foreach (var key in required)
                Assert.True(palette[key] is Brush, $"{theme}: {key} is not a Brush.");
        });
    }

    /// <summary>
    /// High contrast has to actually be high contrast. Checked as a ratio rather than by eye,
    /// because "looks contrasty" is exactly the judgement that lets a 3:1 pair through.
    /// </summary>
    [Fact]
    public void HighContrastPutsTextAndBackgroundAtTheExtremes()
    {
        wpf.Invoke(() =>
        {
            var palette = ThemeManager.Load(AppTheme.HighContrast);

            var background = ((SolidColorBrush)palette["WindowBackgroundBrush"]).Color;
            var text = ((SolidColorBrush)palette["PrimaryTextBrush"]).Color;

            Assert.Equal(Colors.Black, background);
            Assert.Equal(Colors.White, text);

            // Secondary text is the one usually sacrificed - it is grey in the other themes. Here
            // it has to stay readable, so it is held above 7:1 (WCAG AAA for body text).
            var secondary = ((SolidColorBrush)palette["SecondaryTextBrush"]).Color;
            Assert.True(Contrast(secondary, background) >= 7.0,
                $"Secondary text against the background is only {Contrast(secondary, background):F1}:1.");
        });
    }

    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.HighContrast)]
    public void BodyTextIsReadableAgainstItsCard(AppTheme theme)
    {
        wpf.Invoke(() =>
        {
            var palette = ThemeManager.Load(theme);
            var card = ((SolidColorBrush)palette["CardBackgroundBrush"]).Color;
            var text = ((SolidColorBrush)palette["PrimaryTextBrush"]).Color;

            Assert.True(Contrast(text, card) >= 4.5,
                $"{theme}: primary text on a card is {Contrast(text, card):F1}:1, below the 4.5:1 minimum.");
        });
    }

    /// <summary>
    /// Selected rows keep their text readable.
    ///
    /// High contrast originally used the accent yellow as the selected fill, which put white text
    /// on yellow - unreadable. Selection is now carried by an accent-coloured BORDER, so the fill
    /// can stay dark. Found by rendering the view and looking at it; pinned here so it stays fixed.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.HighContrast)]
    public void TextOnASelectedRowStaysReadable(AppTheme theme)
    {
        wpf.Invoke(() =>
        {
            var palette = ThemeManager.Load(theme);
            var selected = ((SolidColorBrush)palette["SidebarItemActiveBrush"]).Color;
            var text = ((SolidColorBrush)palette["PrimaryTextBrush"]).Color;

            Assert.True(Contrast(text, selected) >= 4.5,
                $"{theme}: text on a selected row is {Contrast(text, selected):F1}:1.");
        });
    }

    /// <summary>WCAG relative luminance contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        var la = Luminance(a);
        var lb = Luminance(b);
        var (hi, lo) = la > lb ? (la, lb) : (lb, la);
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            var s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    // ── switching ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applying a theme changes what a loaded window resolves, without rebuilding it. This is the
    /// whole reason the views use DynamicResource - with StaticResource the switch would appear to
    /// work for anything created afterwards and leave every open window in the old colours.
    /// </summary>
    [Fact]
    public void SwitchingThemeRecoloursAWindowThatIsAlreadyBuilt()
    {
        try
        {
            wpf.Invoke(() =>
            {
                ThemeManager.Apply(AppTheme.Dark);

                var view = new AboutView { DataContext = new AboutViewModel() };
                var dark = (SolidColorBrush)view.FindResource("CardBackgroundBrush");

                ThemeManager.Apply(AppTheme.Light);
                var light = (SolidColorBrush)view.FindResource("CardBackgroundBrush");

                Assert.NotEqual(dark.Color, light.Color);
                Assert.Equal(Colors.White, light.Color);
            });
        }
        finally
        {
            wpf.Invoke(() => ThemeManager.Apply(AppTheme.Dark));
        }
    }

    [Fact]
    public void TheAppliedThemeIsRemembered()
    {
        var store = new FakeCredentialStore();

        try
        {
            wpf.Invoke(() =>
            {
                var vm = new AboutViewModel(store) { SelectedTheme = AppTheme.HighContrast };
                Assert.Equal(AppTheme.HighContrast, ThemeManager.Current);
            });

            Assert.Equal(AppTheme.HighContrast, store.Config.Theme);
        }
        finally
        {
            wpf.Invoke(() => ThemeManager.Apply(AppTheme.Dark));
        }
    }

    [Fact]
    public void AConfigThatCannotBeSavedStillLeavesTheThemeApplied()
    {
        // The user can see the change on screen. Undoing it because a file would not write would
        // be a worse answer than saying so.
        var store = new FakeCredentialStore
        {
            SaveConfigThrows = new InvalidOperationException("config.json is locked")
        };

        try
        {
            wpf.Invoke(() =>
            {
                var vm = new AboutViewModel(store) { SelectedTheme = AppTheme.Light };

                Assert.Equal(AppTheme.Light, ThemeManager.Current);
                Assert.True(vm.HasError);
                Assert.Contains("could not be saved", vm.ErrorMessage);
            });
        }
        finally
        {
            wpf.Invoke(() => ThemeManager.Apply(AppTheme.Dark));
        }
    }

    /// <summary>
    /// Every view builds and binds under every theme.
    ///
    /// This is the pass that catches a {StaticResource} left pointing at a key that moved into the
    /// palettes - those DO throw - and it proves each palette actually loads and merges. It does
    /// not catch a missing DynamicResource key, which is silent; that is what the key-set
    /// comparison above is for.
    /// </summary>
    [Theory]
    [InlineData(AppTheme.Dark)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.HighContrast)]
    public void EveryViewLoadsUnderEveryTheme(AppTheme theme)
    {
        try
        {
            wpf.Invoke(() =>
            {
                Assert.True(ThemeManager.Apply(theme), $"{theme} would not apply.");

                var store = new FakeCredentialStore();
                FrameworkElement[] views =
                [
                    new AboutView { DataContext = new AboutViewModel(store) },
                    new BlobConfigView { DataContext = new BlobConfigViewModel(store, new BlobStorageService(store)) },
                    new ServerManagerView { DataContext = new ServerManagerViewModel(store, new SqlServerService(store)) },
                    new BlobBrowserView { DataContext = new BlobBrowserViewModel(new BlobStorageService(store), store) },
                    new HistoryView { DataContext = new HistoryViewModel(new FakeRestoreHistoryStore()) },
                ];

                var listener = BindingErrorListener.Attach();
                try
                {
                    foreach (var view in views)
                    {
                        view.ApplyTemplate();
                        view.Measure(new Size(1600, 1200));
                        view.Arrange(new Rect(0, 0, 1600, 1200));
                        view.UpdateLayout();
                    }

                    listener.AssertNone($"views under {theme}");
                }
                finally
                {
                    listener.Detach();
                }
            });
        }
        finally
        {
            wpf.Invoke(() => ThemeManager.Apply(AppTheme.Dark));
        }
    }

    [Fact]
    public void AStartupWithARememberedThemeAppliesIt()
    {
        var store = new FakeCredentialStore();
        store.Config.Theme = AppTheme.Light;

        try
        {
            wpf.Invoke(() =>
            {
                _ = new MainViewModel(store);
                Assert.Equal(AppTheme.Light, ThemeManager.Current);
            });
        }
        finally
        {
            wpf.Invoke(() => ThemeManager.Apply(AppTheme.Dark));
        }
    }
}
