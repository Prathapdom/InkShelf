using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace InkShelf;

public sealed partial class MainWindow
{
    private RenderTargetBitmap? _curlSnapshot;
    private Rect _curlBounds;

    private async Task CapturePageCurlAsync()
    {
        _curlSnapshot = null;
        PageCurlOverlay.Children.Clear();
        var width = ReaderPagesPanel.ActualWidth;
        var height = ReaderPagesPanel.ActualHeight;
        if (width < 1 || height < 1) return;
        var origin = ReaderPagesPanel.TransformToVisual(PageCurlOverlay).TransformPoint(new Point());
        var scale = ReaderScroll.ZoomFactor;
        _curlBounds = new Rect(origin.X, origin.Y, width * scale, height * scale);
        var snapshot = new RenderTargetBitmap();
        try { await snapshot.RenderAsync(ReaderPagesPanel); }
        catch { return; } // A closing/unloaded reader can no longer be captured.
        _curlSnapshot = snapshot;
        PageCurlOverlay.Clip = new RectangleGeometry { Rect = new Rect(0, 0, ReaderScroll.ActualWidth, ReaderScroll.ActualHeight) };
        var still = new Image { Source = snapshot, Width = _curlBounds.Width, Height = _curlBounds.Height, Stretch = Stretch.Fill };
        Canvas.SetLeft(still, origin.X);
        Canvas.SetTop(still, origin.Y);
        PageCurlOverlay.Children.Add(still);
        PageCurlOverlay.Visibility = Visibility.Visible;
    }

    private async Task PlayPageCurlAsync(int direction)
    {
        if (_curlSnapshot is null) return;
        // Decode the underlying page before exposing it, avoiding a blank flash on large scans.
        await Task.WhenAll(ReaderPagesPanel.Children.OfType<Image>().Select(WaitForPageAsync));
        ReaderPagesPanel.UpdateLayout();
        PageCurlOverlay.Children.Clear();
        var width = _curlBounds.Width;
        var height = _curlBounds.Height;
        const int strips = 72;
        const int frames = 40;
        const double duration = 560;
        var stripWidth = width / strips;
        var storyboard = new Storyboard();
        for (var i = 0; i < strips; i++)
        {
            var sourceX = i * stripWidth;
            var transform = new CompositeTransform();
            var strip = new Grid
            {
                Width = stripWidth + 0.6, Height = height,
                Clip = new RectangleGeometry { Rect = new Rect(0, 0, stripWidth + 0.6, height) },
                RenderTransform = transform
            };
            var picture = new Image { Source = _curlSnapshot, Width = width, Height = height, Stretch = Stretch.Fill,
                HorizontalAlignment = HorizontalAlignment.Left, RenderTransform = new TranslateTransform { X = -sourceX } };
            strip.Children.Add(picture);
            var paper = new Border { Background = new SolidColorBrush(Colors.FloralWhite), Opacity = 0 };
            var shade = new Border { Background = new SolidColorBrush(Colors.Black), Opacity = 0 };
            strip.Children.Add(paper);
            strip.Children.Add(shade);
            Canvas.SetLeft(strip, _curlBounds.X);
            Canvas.SetTop(strip, _curlBounds.Y);
            PageCurlOverlay.Children.Add(strip);
            var xTrack = Track(storyboard, transform, "TranslateX");
            var scaleTrack = Track(storyboard, transform, "ScaleX");
            var paperTrack = Track(storyboard, paper, "Opacity");
            var shadeTrack = Track(storyboard, shade, "Opacity");
            for (var frame = 0; frame <= frames; frame++)
            {
                var t = frame / (double)frames;
                var progress = t * t * (3 - 2 * t);
                var radius = width * 0.12;
                var fold = width - progress * (width * 2 + Math.PI * radius);
                (double X, double Angle) Bend(double x)
                {
                    var distance = x - fold;
                    if (distance <= 0) return (x, 0);
                    var angle = Math.Min(Math.PI, distance / radius);
                    return (fold + radius * Math.Sin(angle) - Math.Max(0, distance - Math.PI * radius), angle);
                }
                var a = Bend(direction > 0 ? sourceX : width - sourceX);
                var b = Bend(direction > 0 ? sourceX + stripWidth : width - sourceX - stripWidth);
                var x = direction > 0 ? a.X : width - a.X;
                var endX = direction > 0 ? b.X : width - b.X;
                var keyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * t));
                xTrack.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = x });
                scaleTrack.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = (endX - x) / stripWidth });
                paperTrack.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = a.Angle > Math.PI / 2 ? 0.92 : 0 });
                shadeTrack.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = keyTime, Value = 0.28 * Math.Sin(a.Angle) });
            }
        }
        var done = new TaskCompletionSource();
        storyboard.Completed += (_, _) => done.TrySetResult();
        try { storyboard.Begin(); await done.Task; }
        finally { storyboard.Stop(); _curlSnapshot = null; }
    }

    private static DoubleAnimationUsingKeyFrames Track(Storyboard storyboard, DependencyObject target, string property)
    {
        var track = new DoubleAnimationUsingKeyFrames();
        Storyboard.SetTarget(track, target);
        Storyboard.SetTargetProperty(track, property);
        storyboard.Children.Add(track);
        return track;
    }

    private static async Task WaitForPageAsync(Image image)
    {
        if (image.Source is BitmapImage bitmap && bitmap.PixelWidth > 0) return;
        var ready = new TaskCompletionSource();
        void Opened(object sender, RoutedEventArgs args) => ready.TrySetResult();
        void Failed(object sender, ExceptionRoutedEventArgs args) => ready.TrySetResult();
        image.ImageOpened += Opened;
        image.ImageFailed += Failed;
        try { await Task.WhenAny(ready.Task, Task.Delay(2000)); }
        finally { image.ImageOpened -= Opened; image.ImageFailed -= Failed; }
    }
}
