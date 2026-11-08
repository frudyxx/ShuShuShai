using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ShuShuShai.App.Views;

internal partial class StartupWindow : Window
{
    private readonly TaskCompletionSource firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public StartupWindow() : this(SystemParameters.ClientAreaAnimation) { }

    internal StartupWindow(bool animate)
    {
        InitializeComponent();
        if (animate)
        {
            UpperOffset.X = -10;
            UpperOffset.Y = -6;
            LowerOffset.X = 10;
            LowerOffset.Y = 6;
            Wordmark.Opacity = 0;
        }
        ContentRendered += (_, _) =>
        {
            if (!firstFrame.TrySetResult() || !animate) return;
            Merge(UpperOffset);
            Merge(LowerOffset);
            Wordmark.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(280))
            { BeginTime = TimeSpan.FromMilliseconds(120) });
        };
    }

    internal async Task PlayAsync(Task homeReady)
    {
        await firstFrame.Task;
        await Task.Delay(1200);
        if (!homeReady.IsCompleted) LoadingStatus.Visibility = Visibility.Visible;
    }

    private static void Merge(TranslateTransform offset)
    {
        var slide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(400))
        { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
        offset.BeginAnimation(TranslateTransform.XProperty, slide);
        offset.BeginAnimation(TranslateTransform.YProperty, slide);
    }
}
