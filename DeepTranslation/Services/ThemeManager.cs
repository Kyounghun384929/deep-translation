using System.Windows;
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
    // (리소스 키, 다크 색, 라이트 색) — App.xaml에 정의된 팔레트 브러시 전체
    private static readonly (string Key, Color Dark, Color Light)[] Palette =
    {
        ("BgBrush",              C("#1E1F26"),   C("#F4F5F8")),
        ("PanelBrush",           C("#262833"),   C("#FFFFFF")),
        ("LineBrush",            C("#3A3D4A"),   C("#D8DAE2")),
        ("TextBrush",            C("#ECECF1"),   C("#1B1D23")),
        ("SubTextBrush",         C("#9AA0AE"),   C("#6B7180")),
        ("AccentBrush",          C("#5B8CFF"),   C("#3D6FE8")),
        ("AccentHoverBrush",     C("#6F9AFF"),   C("#2E5FD6")),
        ("ErrorBrush",           C("#FF7B7B"),   C("#D93034")),
        ("HoverOverlayBrush",    C("#33FFFFFF"), C("#14000000")),
        ("AccentSoftBrush",      C("#405B8CFF"), C("#2E3D6FE8")),
        ("AccentHighlightBrush", C("#335B8CFF"), C("#1F3D6FE8")),
        ("AccentSelectedBrush",  C("#505B8CFF"), C("#333D6FE8")),
    };

    private static bool _subscribed;

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
