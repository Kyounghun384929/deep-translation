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
            using var key = Registry.CurrentUser.CreateSubKey(RunKey); // 키가 �