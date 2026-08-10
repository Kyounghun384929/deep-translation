using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepTranslation.Models;

public class AppSettings
{
    /// <summary>번역 엔진 모드: "Embedded"(내장 llama.cpp) 또는 "LmStudio".</summary>
    public string EngineMode { get; set; } = "Embedded";

    /// <summary>내장 엔진에서 사용할 모델의 카탈로그 Id (ModelCatalog 참조).</summary>
    public string EmbeddedModelId { get; set; } = "qwen3.5-4b";

    /// <summary>내장 엔진 유휴 언로드 대기 시간(분). 0이면 언로드하지 않음.</summary>
    public int IdleUnloadMinutes { get; set; } = 5;

    public string ServerUrl { get; set; } = "http://localhost:1234";

    /// <summary>외부 서버가 인증을 요구할 때만 사용 (Bearer 토큰). 비어 있으면 헤더를 보내지 않는다.</summary>
    public string ApiKey { get; set; } = "";

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

    /// <summary>팝업 테마: "System"(Windows 설정 따름) | "Light" | "Dark".</summary>
    public string Theme { get; set; } = "System";

    /// <summary>시작 시 하루 한 번 GitHub Releases에서 새 버전을 확인할지 여부.</summary>
    public bool AutoUpdateCheck { get; set; } = true;

    /// <summary>마지막 업데이트 확인 시각(UTC) — 과도한 API 호출을 막는다.</summary>
    public DateTime LastUpdateCheckUtc { get; set; } = DateTime.MinValue;

    public bool RunAtStartup { get; set; } = false;
    public bool PopupNearCursor { get; set; } = true;
    public bool CloseOnFocusLoss { get; set; } = true;
    public double Temperature { get; set; } = 0.2;
    public int DoublePressWindowMs { get; set; } = 500;

    /// <summary>자동 모드에서 마지막으로 성공한 모델. 재시작 직후에도 바로 이 모델부터 시도한다.</summary>
    public string LastWorkingModel { get; set; } = "";

    /// <summary>사용자 용어집. 한 줄에 하나씩 "원어 = 번역어" 형식으로 시스템 프롬프트에 주입된다.</summary>
    public string Glossary { get; set; } = "";

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
