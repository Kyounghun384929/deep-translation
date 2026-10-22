using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using DeepTranslation.Models;
using Microsoft.Win32;

namespace DeepTranslation.Services;

/// <summary>
/// 라이트/다크 테마 관리. Application.Resources의 팔레트 브러시를 새 인스턴스로 교체한다.
/// (BAML로 컴파일된 브러시는 Style seal 시 freeze되어 Color 변경이 불가 —
///  소비자는 전부 DynamicResource로 참조하므로 교체만으로 전체 UI가 갱신된다.)
/// </summary>
public static class ThemeManager
{
    // (리소스 키, 다크 색, 라이트 색) — App.xaml에 정의된 팔레트 브러시 전체.
    // Toss 계열 회색·파랑 팔레트. 흰 글자·작은 강조 글자가 WCAG AA(4.5:1)를 넘도록 파랑을 한 단계 어둡게 잡았다.
    private static readonly (string Key, Color Dark, Color Light)[] Palette =
    {
        ("BgBrush",              C("#17171C"),   C("#F2F4F6")),
        ("PanelBrush",           C("#202027"),   C("#FFFFFF")),
        ("LineBrush",            C("#33333D"),   C("#E5E8EB")),
        ("FieldBrush",           C("#2C2C35"),   C("#F2F4F6")),
        ("TextBrush",            C("#ECECEF"),   C("#191F28")),
        ("SubTextBrush",         C("#9E9EA7"),   C("#636E7B")),
        ("AccentBrush",          C("#1E6EE8"),   C("#1E6EE8")),
        ("AccentHoverBrush",     C("#3A82F0"),   C("#1A5FD0")),
        ("AccentTextBrush",      C("#6EA8FE"),   C("#1B64DA")),
        ("ErrorBrush",           C("#FF6B76"),   C("#D9363E")),
        ("SuccessBrush",         C("#2BC48A"),   C("#087F4F")),
        ("HoverOverlayBrush",    C("#14FFFFFF"), C("#0D000000")),
        ("AccentSoftBrush",      C("#1A3157"),   C("#E8F3FF")),
        ("AccentHighlightBrush", C("#2C2C35"),   C("#F2F4F6")),
        ("AccentSelectedBrush",  C("#1A3157"),   C("#E8F3FF")),
    };

    private static bool _subscribed;

    /// <summary>마지막으로 적용된 테마가 다크인지 — 창 제목 표시줄 색 맞춤에 쓴다.</summary>
    public static bool IsDark { get; private set; } = true;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>표준 제목 표시줄을 현재 테마(라이트/다크)에 맞춘다. SourceInitialized 이후 호출. 미지원 OS에서는 무시된다.</summary>
    public static void ApplyTitleBar(Window window)
    {
        int dark = IsDark ? 1 : 0;
        // DWMWA_USE_IMMERSIVE_DARK_MODE = 20 (Windows 10 20H1+ / 11)
        DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
    }

    private static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    /// <summary>설정의 테마("System"|"Light"|"Dark")를 해석해 앱 리소스 브러시에 적용한다. UI 스레드에서 호출.</summary>
    public static void Apply(AppSettings settings)
    {
        bool dark = settings.Theme switch
        {
            "Dark" => true,
            "Light" => false,
            _ => !IsSystemLightTheme(), // "System"
        };
        IsDark = dark;

        var resources = Application.Current.Resources;
        foreach (var (key, darkColor, lightColor) in Palette)
        {
            var brush = new SolidColorBrush(dark ? darkColor : lightColor);
            brush.Freeze(); // 공유 인스턴스 — 이후 변경하지 않으므로 freeze
            resources[key] = brush;
        }

        // System 모드에서 OS 테마 변경을 실시간 반영하기 위한 구독 (1회만)
        if (!_subscribed)
        {
            _subscribed = true;
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
    }

    /// <summary>앱 종료 시 시스템 이벤트 구독을 해제한다.</summary>
    public static void Shutdown()
    {
        if (!_subscribed) return;
        _subscribed = false;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    /// <summary>Windows 개인 설정의 앱 테마가 라이트인지 확인한다 (값이 없으면 라이트 간주).</summary>
    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v != 0;
        }
        catch
        {
            return true;
        }
    }

    private static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // OS 라이트/다크 전환은 General 카테고리로 통지된다
        if (e.Category != UserPreferenceCategory.General) return;
        if (App.Settings.Theme != "System") return;
        // SystemEvents는 별도 스레드에서 호출되므로 UI 스레드로 넘긴다
        _ = Application.Current?.Dispatcher.InvokeAsync(() => Apply(App.Settings));
    }
}
