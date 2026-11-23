using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace oshituner;

internal sealed class ThemeBackground : Border, IDisposable
{
    private readonly Image _image = new() { SnapsToDevicePixels = true };
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private CancellationTokenSource? _load;
    private ThemeAnimation? _animation;
    private int _frame;
    private int _loops;

    public ThemeBackground()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        Child = _image;
        _timer.Tick += (_, _) => Advance();
    }

    public async Task ShowAsync(ThemeSettings theme, ThemeImages images)
    {
        Stop();
        Background = SystemParameters.HighContrast ? SystemColors.WindowBrush
            : Brush(theme.Colour ?? (theme.Mode == AppThemeMode.Light ? "#FFFFFF" : "#000000"));
        if (theme.Background != ThemeBackgroundType.Image || SystemParameters.HighContrast) return;

        var corner = theme.ImagePlacement == ThemeImagePlacement.TopLeft;
        _image.HorizontalAlignment = corner ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        _image.VerticalAlignment = corner ? VerticalAlignment.Top : VerticalAlignment.Center;
        _image.Stretch = theme.ImagePlacement == ThemeImagePlacement.ScaleToFit ? Stretch.Uniform : Stretch.None;
        var load = new CancellationTokenSource();
        _load = load;
        try
        {
            var animation = await images.LoadAsync(theme.ImageId ?? ThemeImages.BuiltIn, load.Token);
            if (load.IsCancellationRequested) return;
            _animation = animation;
            _frame = 0;
            _loops = 0;
            DisplayFrame();
        }
        catch (OperationCanceledException) when (load.IsCancellationRequested) { }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (!load.IsCancellationRequested)
                Background = Brush(theme.Mode == AppThemeMode.Light ? "#FFFFFF" : "#000000");
        }
        finally
        {
            if (ReferenceEquals(_load, load)) _load = null;
            load.Dispose();
        }
    }

    private void Advance()
    {
        _timer.Stop();
        if (_animation is not { } animation) return;
        if (_frame + 1 == animation.Frames.Length)
        {
            if (animation.Repetitions >= 0 && _loops >= animation.Repetitions) return;
            _loops++;
            _frame = 0;
        }
        else _frame++;
        DisplayFrame();
    }

    private void DisplayFrame()
    {
        if (_animation is not { Frames.Length: > 0 } animation) return;
        var frame = animation.Frames[_frame];
        _image.Source = frame.Image;
        if (animation.Frames.Length < 2) return;
        _timer.Interval = frame.Duration;
        _timer.Start();
    }

    private void Stop()
    {
        _load?.Cancel();
        _load = null;
        _timer.Stop();
        _animation = null;
        _image.Source = null;
    }

    public void Dispose() => Stop();

    private static SolidColorBrush Brush(string value) => new((Color)ColorConverter.ConvertFromString(value));
}
