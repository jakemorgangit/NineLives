using System.Windows;
using System.Windows.Media.Animation;

namespace Blackcat.NineLives.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Fades the splash out and closes it. Awaitable so startup can hand over to the main window
    /// only once this has finished, rather than the two overlapping.
    /// </summary>
    public Task FadeOutAsync()
    {
        var completion = new TaskCompletionSource();

        var fade = new DoubleAnimation
        {
            From = 1.0,
            To = 0.0,
            Duration = TimeSpan.FromMilliseconds(220)
        };

        fade.Completed += (_, _) =>
        {
            Close();
            completion.TrySetResult();
        };

        BeginAnimation(OpacityProperty, fade);
        return completion.Task;
    }
}
