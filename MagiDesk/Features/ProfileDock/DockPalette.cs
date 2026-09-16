using System.Windows.Media;
using MagiDesk.Config;
using Microsoft.Win32;
using Wpf.Ui.Appearance;

namespace MagiDesk.Features.ProfileDock;

/// <summary>
/// Colour set for the dock chrome, modelled on the Windows 11 taskbar: a
/// near-opaque neutral bar with a hairline edge, and low-alpha white/black
/// washes for the hover / pressed / active button states.
///
/// Resolves <see cref="AppConfig.BrowserDockTheme"/> — follow Windows, or a
/// forced light/dark — and raises <see cref="Changed"/> when the user flips the
/// system theme so open docks can restyle in place instead of rebuilding.
/// </summary>
internal sealed record DockPalette(
    Color Background,
    Color AcrylicTint,
    Color Border,
    Color Hover,
    Color Pressed,
    Color Text,
    Color Separator,
    Color GroupBackground,
    Color GroupBorder,
    Color IndicatorIdle)
{
    private static readonly DockPalette DarkPalette = new(
        Background:      Color.FromArgb(0xE8, 0x20, 0x20, 0x20),
        AcrylicTint:     Color.FromArgb(0xA6, 0x1F, 0x1F, 0x1F),
        Border:          Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF),
        Hover:           Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF),
        Pressed:         Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF),
        Text:            Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF),
        Separator:       Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF),
        GroupBackground: Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF),
        GroupBorder:     Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF),
        IndicatorIdle:   Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    private static readonly DockPalette LightPalette = new(
        Background:      Color.FromArgb(0xE8, 0xF3, 0xF3, 0xF3),
        AcrylicTint:     Color.FromArgb(0xA6, 0xF3, 0xF3, 0xF3),
        Border:          Color.FromArgb(0x1F, 0x00, 0x00, 0x00),
        Hover:           Color.FromArgb(0x14, 0x00, 0x00, 0x00),
        Pressed:         Color.FromArgb(0x0A, 0x00, 0x00, 0x00),
        Text:            Color.FromArgb(0xFF, 0x1A, 0x1A, 0x1A),
        Separator:       Color.FromArgb(0x2E, 0x00, 0x00, 0x00),
        GroupBackground: Color.FromArgb(0x0F, 0x00, 0x00, 0x00),
        GroupBorder:     Color.FromArgb(0x1A, 0x00, 0x00, 0x00),
        IndicatorIdle:   Color.FromArgb(0x99, 0x00, 0x00, 0x00));

    public static DockPalette For(AppConfig cfg) => IsDark(cfg) ? DarkPalette : LightPalette;

    public static bool IsDark(AppConfig cfg) => ResolveDark(cfg.BrowserDockTheme, SystemIsDark);

    internal static bool ResolveDark(DockTheme theme, Func<bool> readSystem) => theme switch
    {
        DockTheme.Light => false,
        DockTheme.Dark  => true,
        _               => readSystem(),
    };

    // Same source of truth the shell theme uses at startup (App.ApplyCurrentSystemTheme),
    // so the dock never disagrees with the rest of the app.
    private static bool SystemIsDark()
    {
        try
        {
            // Read the live application-mode setting; a saved Windows theme
            // name need not change when the user only switches light/dark mode.
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int light)
                return light == 0;
            return ApplicationThemeManager.GetSystemTheme()
                is SystemTheme.Dark or SystemTheme.HCBlack;
        }
        catch { return true; }   // dark matches the dock's historical look
    }

    /// <summary>The Windows accent colour — the active window's taskbar pill.</summary>
    public static Color Accent()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM");
            if (k?.GetValue("AccentColor") is int raw)
            {
                uint v = (uint)raw;                       // stored ABGR
                byte a = (byte)((v >> 24) & 0xFF);
                return Color.FromArgb(a == 0 ? (byte)0xFF : a,
                    (byte)(v & 0xFF), (byte)((v >> 8) & 0xFF), (byte)((v >> 16) & 0xFF));
            }
        }
        catch { }
        return Color.FromRgb(0x00, 0x78, 0xD4);
    }

    /// <summary>Raised when Windows changes theme or accent. May fire on a
    /// background thread — subscribers must marshal to their dispatcher.</summary>
    public static event Action? Changed;

    private static bool _watching;

    /// <summary>Start listening for Windows theme/accent changes. Idempotent.</summary>
    public static void EnsureWatching()
    {
        if (_watching) return;
        _watching = true;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            Changed?.Invoke();
        };
    }
}
