using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace MultiAIChatbot.Services;

/// <summary>
/// Light and dark color themes. The colors live in App.xaml as brushes; this swaps
/// them at runtime (everything uses DynamicResource, so the UI updates instantly),
/// darkens the window title bar, and tells the AI websites which scheme to use.
/// </summary>
public static class Theme
{
    public const string FollowWindows = "System";
    public const string LightMode = "Light";
    public const string DarkMode = "Dark";

    public static bool IsDark { get; private set; }

    /// <summary>Raised after the theme changes, so each browser column can follow it.</summary>
    public static event Action? Changed;

    private static readonly Dictionary<string, string> LightColors = new()
    {
        ["AppBackground"] = "#F4F5F8",
        ["SidebarBackground"] = "#FBFBFD",
        ["CardBackground"] = "#FFFFFF",
        ["CardBorder"] = "#E4E6EC",
        ["Divider"] = "#EDEEF2",
        ["TextPrimary"] = "#1A1C23",
        ["TextMuted"] = "#6B7080",
        ["Accent"] = "#5B4FE9",
        ["AccentHover"] = "#4B3FD8",
        ["AccentSoft"] = "#ECEAFD",
        ["AccentText"] = "#4A3FD0",
        ["OnAccent"] = "#FFFFFF",
        ["NavHover"] = "#F0F1F5",
        ["ControlBackground"] = "#FFFFFF",
        ["ControlHover"] = "#F3F4F7",
        ["ControlBorder"] = "#D7DAE1",
        ["ScrollThumb"] = "#C9CCD5",
        ["ErrorText"] = "#D23B3B",
        ["DangerHover"] = "#FDECEC",
    };

    private static readonly Dictionary<string, string> DarkColors = new()
    {
        ["AppBackground"] = "#111216",
        ["SidebarBackground"] = "#16171C",
        ["CardBackground"] = "#1C1D23",
        ["CardBorder"] = "#2A2C34",
        ["Divider"] = "#26272E",
        ["TextPrimary"] = "#ECEDF1",
        ["TextMuted"] = "#9A9EAD",
        ["Accent"] = "#7C70FF",
        ["AccentHover"] = "#8F85FF",
        ["AccentSoft"] = "#262447",
        ["AccentText"] = "#B3ACFF",
        ["OnAccent"] = "#FFFFFF",
        ["NavHover"] = "#23242B",
        ["ControlBackground"] = "#22232A",
        ["ControlHover"] = "#2A2B33",
        ["ControlBorder"] = "#363842",
        ["ScrollThumb"] = "#3D3F4A",
        ["ErrorText"] = "#FF6B6B",
        ["DangerHover"] = "#3A1F23",
    };

    /// <summary>Applies the saved theme and keeps following Windows when set to System.</summary>
    public static void Initialize()
    {
        Apply(AppState.Settings.Theme);
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General && AppState.Settings.Theme == FollowWindows)
                Application.Current?.Dispatcher.Invoke(() => Apply(FollowWindows));
        };
    }

    public static void Apply(string mode)
    {
        IsDark = mode switch
        {
            DarkMode => true,
            LightMode => false,
            _ => WindowsUsesDarkMode(),
        };

        var resources = Application.Current.Resources;
        foreach (var (key, hex) in IsDark ? DarkColors : LightColors)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            resources[key] = brush;
        }

        foreach (Window window in Application.Current.Windows)
            ApplyTitleBar(window);

        Changed?.Invoke();
    }

    /// <summary>Makes the window's title bar dark or light to match the theme (Windows 10 20H1+).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var useDark = IsDark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE; older builds used 19.
        if (DwmSetWindowAttribute(handle, 20, ref useDark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref useDark, sizeof(int));
    }

    private static bool WindowsUsesDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
