using Microsoft.Win32;

namespace DeepTranslation.Services;

/// <summary>Windows 시작 시 자동 실행(HKCU Run 키) 관리.</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "DeepTranslation";

    /// <summary>레지스트리에 자동 실행 항목이 등록되어 있는지 확인한다 (설치 프로그램 등록 포함).</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey); // 키가 없으면 생성
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\" --autostart"); // 실행 파일이 이동했어도 경로 갱신
            else if (!enabled)
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch
        {
            // 레지스트리 접근 실패는 무시 (기능적 필수 아님)
        }
    }
}
