using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace oshituner;

[JsonConverter(typeof(JsonStringEnumConverter<AppThemeMode>))]
internal enum AppThemeMode { Dark, Light }

[JsonConverter(typeof(JsonStringEnumConverter<ThemeBackgroundType>))]
internal enum ThemeBackgroundType { Solid, Image }

[JsonConverter(typeof(JsonStringEnumConverter<ThemeImagePlacement>))]
internal enum ThemeImagePlacement { ScaleToFit, Centered, TopLeft }

internal sealed partial record ThemeSettings(
    AppThemeMode Mode = AppThemeMode.Dark,
    string? FontFamily = null,
    ThemeBackgroundType Background = ThemeBackgroundType.Solid,
    string? Colour = null,
    string? ImageId = null,
    ThemeImagePlacement ImagePlacement = ThemeImagePlacement.ScaleToFit,
    int Opacity = 85)
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex Rgb();

    public bool IsValid() => Enum.IsDefined(Mode) && Enum.IsDefined(Background) && Enum.IsDefined(ImagePlacement)
        && Opacity is >= 0 and <= 100 && (Colour is null || Rgb().IsMatch(Colour))
        && (ImageId is null || ThemeImages.IsAssetId(ImageId))
        && (FontFamily is null || FontFamily.Length is > 0 and <= 150);

    public FontFamily ResolveFont() => FontFamily is { } name
        ? Fonts.SystemFontFamilies.FirstOrDefault(family => string.Equals(family.Source, name, StringComparison.OrdinalIgnoreCase))
            ?? SystemFonts.MessageFontFamily
        : SystemFonts.MessageFontFamily;
}

internal sealed class ThemeController : IDisposable
{
    private readonly TunerSession _session;
    private readonly DockWorkspace _workspace;
    private readonly ThemeBackground _background = new();
    private readonly Dispatcher _dispatcher;

    public ThemeController(TunerSession session, DockWorkspace workspace, InstallProfile profile)
    {
        _session = session;
        _workspace = workspace;
        _dispatcher = workspace.Dispatcher;
        Images = new(profile);
        _workspace.AddBackground(_background);
        SystemParameters.StaticPropertyChanged += OnSystemThemeChanged;
    }

    public ThemeImages Images { get; }

    public ThemeSettings Current => _session.Theme;

    public void Set(ThemeSettings theme)
    {
        if (!theme.IsValid()) throw new ArgumentException("The theme settings contain an invalid value.");
        _session.SetTheme(theme);
        Apply();
    }

    public void Apply()
    {
        var theme = Current;
        var light = theme.Mode == AppThemeMode.Light;
        var contrast = SystemParameters.HighContrast;
        var resources = Application.Current.Resources;

        void Set(string key, uint color, Color contrastColor, int alpha = 255)
        {
            var brush = new SolidColorBrush(contrast ? contrastColor :
                Color.FromArgb((byte)alpha, (byte)(color >> 16), (byte)(color >> 8), (byte)color));
            brush.Freeze();
            resources[key] = brush;
        }

        Set("WindowBackground", light ? 0xF3F3F5u : 0x0E0E10u, SystemColors.WindowColor);
        Set("WorkspaceBackground", light ? 0xFFFFFFu : 0x000000u, SystemColors.WindowColor);
        Set("PanelBackground", light ? 0xF5F5F7u : 0x18181Bu, SystemColors.WindowColor);
        Set("FloatingPanelBackground", light ? 0xF5F5F7u : 0x18181Bu, SystemColors.WindowColor);
        Set("DockPanelBackground", light ? 0xF5F5F7u : 0x18181Bu, SystemColors.WindowColor,
            (int)Math.Round(theme.Opacity * 255d / 100));
        Set("DockHeaderBackground", light ? 0xDDDDE4u : 0x252529u, SystemColors.ControlColor);
        Set("PrimaryForeground", light ? 0x000000u : 0xFFFFFFu, SystemColors.WindowTextColor);
        Set("MutedForeground", light ? 0x494950u : 0xADADB8u, SystemColors.WindowTextColor);
        Set("InputBackground", light ? 0xFFFFFFu : 0x15151Au, SystemColors.WindowColor);
        Set("BorderBrush", light ? 0xBBBCC5u : 0x41414Bu, SystemColors.WindowTextColor);
        // Rows already sit on the dock surface. Add a faint stripe tint instead of painting a second surface.
        Set("GridStripeOverlay", light ? 0x000000u : 0xFFFFFFu, SystemColors.ControlColor,
            (int)Math.Round(theme.Opacity * 24d / 100));
        Set("SelectedBackground", light ? 0xDECCF9u : 0x493376u, SystemColors.HighlightColor);
        Set("SelectedForeground", light ? 0x000000u : 0xFFFFFFu, SystemColors.HighlightTextColor);
        Set("AccentBrush", light ? 0x5E2AA3u : 0x7134BDu, SystemColors.HighlightColor);
        Set("AccentForeground", 0xFFFFFFu, SystemColors.HighlightTextColor);
        Set("DisabledForeground", light ? 0x65656Du : 0xB0B0B8u, SystemColors.GrayTextColor);
        Set("WarningForeground", light ? 0x705100u : 0xFFD766u, SystemColors.WindowTextColor);
        Set("ErrorForeground", light ? 0x9E1A16u : 0xFF8C8Cu, SystemColors.WindowTextColor);
        Set("LiveBrush", light ? 0x9E1A16u : 0xFF6666u, SystemColors.WindowTextColor);
        Set("WatchBrush", light ? 0x17682Du : 0x8DE89Du, SystemColors.WindowTextColor);
        Set("OshiWatchTabRunningBackground", light ? 0x236634u : 0x184F2Cu, SystemColors.HighlightColor);
        Set("OshiWatchTabPausedBackground", light ? 0x9B3039u : 0x7D2630u, SystemColors.HighlightColor);
        Set("OshiWatchTabForeground", 0xFFFFFFu, SystemColors.HighlightTextColor);
        Set("WatchingBrush", light ? 0x17549Cu : 0x8AC8FFu, SystemColors.WindowTextColor);
        Set("SubBrush", light ? 0x765000u : 0xFFD700u, SystemColors.WindowTextColor);
        resources["ApplicationFont"] = contrast ? SystemFonts.MessageFontFamily : theme.ResolveFont();

        _ = _background.ShowAsync(theme, Images);
    }

    private void OnSystemThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(SystemParameters.HighContrast)) _dispatcher.BeginInvoke(Apply);
    }

    public void Dispose()
    {
        SystemParameters.StaticPropertyChanged -= OnSystemThemeChanged;
        _background.Dispose();
    }
}
