using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace oshituner;

internal sealed class ColourWheel : FrameworkElement
{
    private const int Pixels = 144;
    private static readonly BitmapSource Wheel = CreateWheel();
    public double Brightness { get; set; } = 1;
    public event Action<Color>? Selected;

    protected override void OnRender(DrawingContext drawingContext) =>
        drawingContext.DrawImage(Wheel, new Rect(0, 0, ActualWidth, ActualHeight));

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs args)
    {
        base.OnMouseLeftButtonDown(args);
        var point = args.GetPosition(this);
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var x = 2 * point.X / ActualWidth - 1;
        var y = 2 * point.Y / ActualHeight - 1;
        var saturation = Math.Sqrt(x * x + y * y);
        if (saturation <= 1) Selected?.Invoke(Rgb(x, y, saturation, Brightness));
    }

    private static BitmapSource CreateWheel()
    {
        var data = new byte[Pixels * Pixels * 4];
        for (var row = 0; row < Pixels; row++)
        for (var col = 0; col < Pixels; col++)
        {
            var x = 2d * (col + .5) / Pixels - 1;
            var y = 2d * (row + .5) / Pixels - 1;
            var saturation = Math.Sqrt(x * x + y * y);
            if (saturation > 1) continue;
            var color = Rgb(x, y, saturation, 1);
            var offset = (row * Pixels + col) * 4;
            data[offset] = color.B;
            data[offset + 1] = color.G;
            data[offset + 2] = color.R;
            data[offset + 3] = 255;
        }
        var source = BitmapSource.Create(Pixels, Pixels, 96, 96, PixelFormats.Bgra32, null, data, Pixels * 4);
        source.Freeze();
        return source;
    }

    private static Color Rgb(double x, double y, double saturation, double value)
    {
        var hue = ((Math.Atan2(y, x) * 3 / Math.PI) + 6) % 6;
        var chroma = value * saturation;
        var intermediate = chroma * (1 - Math.Abs(hue % 2 - 1));
        var (r, g, b) = (int)hue switch
        {
            0 => (chroma, intermediate, 0d), 1 => (intermediate, chroma, 0d), 2 => (0d, chroma, intermediate),
            3 => (0d, intermediate, chroma), 4 => (intermediate, 0d, chroma), _ => (chroma, 0d, intermediate)
        };
        var minimum = value - chroma;
        byte Channel(double channel) => (byte)Math.Round(255 * (channel + minimum));
        return Color.FromRgb(Channel(r), Channel(g), Channel(b));
    }
}
