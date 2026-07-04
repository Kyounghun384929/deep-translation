using Microsoft.Win32;

namespace DeepTranslation.Services;

/// <summary>Windows 시작 시 자동 실행(HKCU Run 키) 관리.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeepTranslation";

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --autostart");
            else if (!enabled)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 레지스트리 접근 실패는 무시 (기능적 필수 아님)
        }
    }
}
