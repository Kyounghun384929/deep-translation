using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepTranslation.Models;

public class AppSettings
{
    public string ServerUrl { get; set; } = "http://localhost:1234";

    /// <summary>비어 있으면 서버에 로드된 첫 번째 모델을 자동 사용.</summary>
    public string Model { get; set; } = "";

    public string TargetLanguage { get; set; } = "한국어";

    /// <summary>원문이 이미 한국어일 때 대신 번역할 언어.</summary>
    public string KoreanSourceTarget { get; set; } = "영어";

    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>번역 단축키 조합 (예: "Ctrl+C", "Alt+Q", "F9").</summary>
    public string HotkeyGesture { get; set; } = "Ctrl+C";

    /// <summary>true면 짧은 간격으로 두 번 눌러야 발동. Ctrl+C 조합은 항상 두 번 누르기.</summary>
    public bool HotkeyDoublePress { get; set; } = true;

    public bool RunAtStartup { get; set; } = false;
    public bool PopupNearCursor { get; set; } = true;
    public bool CloseOnFocusLoss { get; set; } = true;
    public double Temperature { get; set; } = 0.2;
    public int DoublePressWindowMs { get; set; } = 500;

    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DeepTranslation", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
            // 손상된 설정 파일은 기본값으로 대체
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 저장 실패는 치명적이지 않음
        }
    }
}
