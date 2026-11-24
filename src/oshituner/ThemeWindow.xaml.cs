using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace oshituner;

public partial class ThemeWindow : Window
{
    private readonly ThemeController _theme;
    private readonly ThemeBackground _preview = new();
    private readonly ColourWheel _wheel = new();
    private readonly CancellationTokenSource _lifetime = new();
    private ThemeSettings? _lastBackground;
    private string? _colour;
    private string? _imageId;
    private bool _initializing = true;

    internal ThemeWindow(ThemeController theme)
    {
        _theme = theme;
        InitializeComponent();
        Mode.ItemsSource = Enum.GetValues<AppThemeMode>();
        BackgroundChoice.ItemsSource = Enum.GetValues<ThemeBackgroundType>();
        Placement.ItemsSource = new[] { "Scale to fit", "Centered", "Top left" };
        FontChoice.ItemsSource = new[] { "Default" }.Concat(Fonts.SystemFontFamilies.Select(font => font.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name)).ToArray();
        var current = theme.Current;
        Mode.SelectedItem = current.Mode;
        FontChoice.SelectedItem = current.FontFamily is { } font && FontChoice.Items.Contains(font) ? font : "Default";
        BackgroundChoice.SelectedItem = current.Background;
        Placement.SelectedIndex = (int)current.ImagePlacement;
        SurfaceOpacity.Value = current.Opacity;
        _colour = current.Colour;
        _imageId = current.ImageId;
        WheelHost.Content = _wheel;
        PreviewHost.Content = _preview;
        _wheel.Selected += color =>
        {
            _colour = Hex(color);
            UpdateRgb();
            RefreshPreview();
        };
        _initializing = false;
        UpdateRgb();
        RefreshPreview();
    }

    private ThemeSettings Selection => new((AppThemeMode)(Mode.SelectedItem ?? AppThemeMode.Dark),
        FontChoice.SelectedItem is string font && font != "Default" ? font : null,
        (ThemeBackgroundType)(BackgroundChoice.SelectedItem ?? ThemeBackgroundType.Solid),
        _colour, _imageId, (ThemeImagePlacement)Math.Max(0, Placement.SelectedIndex), (int)SurfaceOpacity.Value);

    protected override void OnClosed(EventArgs e)
    {
        _lifetime.Cancel();
        _preview.Dispose();
        base.OnClosed(e);
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing) return;
        if (ReferenceEquals(sender, Mode) && _colour is null) UpdateRgb();
        RefreshPreview();
    }

    private void Opacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) => RefreshPreview();

    private void Brightness_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) =>
        _wheel.Brightness = e.NewValue / 100;

    private void RefreshPreview()
    {
        if (_initializing) return;
        var selection = Selection;
        OpacityLabel.Content = $"Background opacity ({selection.Opacity}%)";
        ImageName.Text = selection.ImageId is null or ThemeImages.BuiltIn ? "Vtuber 4k" : "Cached image";
        var light = selection.Mode == AppThemeMode.Light;
        PreviewTint.Background = new SolidColorBrush(light
            ? Color.FromArgb((byte)(selection.Opacity * 255 / 100), 245, 245, 247)
            : Color.FromArgb((byte)(selection.Opacity * 255 / 100), 24, 24, 27));
        PreviewText.Foreground = light ? Brushes.Black : Brushes.White;
        PreviewText.FontFamily = selection.ResolveFont();
        var background = selection with { FontFamily = null, Opacity = 85 };
        if (_lastBackground == background) return;
        _lastBackground = background;
        _ = _preview.ShowAsync(background, _theme.Images);
    }

    private void UpdateRgb()
    {
        _initializing = true;
        var fallback = Mode.SelectedItem is AppThemeMode.Light ? "#FFFFFF" : "#000000";
        var color = (Color)ColorConverter.ConvertFromString(_colour ?? fallback);
        Red.Text = color.R.ToString();
        Green.Text = color.G.ToString();
        Blue.Text = color.B.ToString();
        Swatch.Background = new SolidColorBrush(color);
        _initializing = false;
    }

    private bool ReadRgb(out Color color)
    {
        if (byte.TryParse(Red.Text, out var red) && byte.TryParse(Green.Text, out var green) && byte.TryParse(Blue.Text, out var blue))
        {
            color = Color.FromRgb(red, green, blue);
            return true;
        }

        color = default;
        return false;
    }

    private void RgbChanged(object sender, TextChangedEventArgs e)
    {
        if (_initializing || !ReadRgb(out var color)) return;
        _colour = Hex(color);
        Swatch.Background = new SolidColorBrush(color);
        RefreshPreview();
    }

    private void DefaultColour_Click(object sender, RoutedEventArgs e)
    {
        _colour = null;
        UpdateRgb();
        RefreshPreview();
    }

    private void BuiltIn_Click(object sender, RoutedEventArgs e)
    {
        _imageId = ThemeImages.BuiltIn;
        BackgroundChoice.SelectedItem = ThemeBackgroundType.Image;
        RefreshPreview();
    }

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            CheckFileExists = true, Multiselect = false,
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp"
        };
        if (dialog.ShowDialog(this) == true) await ImportAsync(dialog.FileName);
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        if (Uri.TryCreate(ImageUrl.Text.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            await ImportAsync(uri.AbsoluteUri, AllowPrivateImageUrl.IsChecked == true);
        else Feedback.Text = "Enter an HTTP or HTTPS image URL.";
    }

    private async Task ImportAsync(string source, bool allowPrivate = false)
    {
        AllowPrivateImageUrl.IsChecked = false;
        LoadButton.IsEnabled = BrowseButton.IsEnabled = BuiltInButton.IsEnabled = false;
        Feedback.Text = "Caching image…";
        try
        {
            _imageId = await _theme.Images.ImportAsync(source, _lifetime.Token, allowPrivate);
            BackgroundChoice.SelectedItem = ThemeBackgroundType.Image;
            Feedback.Text = "Image cached. Apply to use it in the workspace.";
            RefreshPreview();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Feedback.Text = ex is UnauthorizedAccessException ? "Oshi Tuner cannot access the image or its cache." : ex.Message;
        }
        finally { LoadButton.IsEnabled = BrowseButton.IsEnabled = BuiltInButton.IsEnabled = true; }
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private bool ApplySelection()
    {
        if (!ReadRgb(out _))
        {
            Feedback.Text = "Enter a value from 0 to 255 for each colour channel.";
            return false;
        }

        try
        {
            _theme.Set(Selection);
            Feedback.Text = "Theme saved.";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Feedback.Text = ex.Message;
            return false;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) => ApplySelection();
    private void Ok_Click(object sender, RoutedEventArgs e) { if (ApplySelection()) Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
