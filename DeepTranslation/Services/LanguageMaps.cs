using DeepTranslation.Models;

namespace DeepTranslation.Services;

public static class LanguageMaps
{
    public static readonly string[] TargetChoices =
    {
        "한국어", "영어", "일본어", "중국어(간체)", "중국어(번체)",
        "스페인어", "프랑스어", "독일어", "베트남어", "러시아어"
    };

    public static readonly string[] KoreanSourceChoices =
    {
        "영어", "일본어", "중국어(간체)", "프랑스어", "독일어"
    };

    private static readonly Dictionary<string, string> English = new()
    {
        ["한국어"] = "Korean",
        ["영어"] = "English",
        ["일본어"] = "Japanese",
        ["중국어(간체)"] = "Simplified Chinese",
        ["중국어(번체)"] = "Traditional Chinese",
        ["스페인어"] = "Spanish",
        ["프랑스어"] = "French",
        ["독일어"] = "German",
        ["베트남어"] = "Vietnamese",
        ["러시아어"] = "Russian",
    };

    /// <summary>표시명 → 영어명 (미등록이면 "Korean").</summary>
    public static string ToEnglish(string display) =>
        English.TryGetValue(display, out var e) ? e : "Korean";

    // 영어명 → 표시명 역매핑 (마커 파싱 결과를 표시명으로 되돌릴 때 사용)
    private static readonly Dictionary<string, string> Display =
        English.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>영어명 → 표시명. 등록되지 않은 언어이면 null.</summary>
    public static string? ToDisplay(string english) =>
        english != null && Display.TryGetValue(english, out var d) ? d : null;

    /// <summary>
    /// 번역 대상 언어를 결정한다. 대상이 한국어인데 원문이 이미 한국어이면
    /// 설정된 보조 언어(기본: 영어)로 번역한다.
    /// </summary>
    public static (string Display, string EnglishName) ResolveTarget(AppSettings settings, string text)
    {
        string target = settings.TargetLanguage;
        if (ToEnglish(target) == "Korean" && IsMostlyKorean(text))
            target = string.IsNullOrWhiteSpace(settings.KoreanSourceTarget) ? "영어" : settings.KoreanSourceTarget;
        return (target, ToEnglish(target));
    }

    public static bool IsMostlyKorean(string text)
    {
        int letters = 0, hangul = 0;
        foreach (var ch in text)
        {
            if (!char.IsLetter(ch)) continue;
            letters++;
            // 한글 음절(U+AC00-D7A3), 자모(U+1100-11FF), 호환 자모(U+3130-318F)
            if (ch is (>= (char)0xAC00 and <= (char)0xD7A3)
                   or (>= (char)0x1100 and <= (char)0x11FF)
                   or (>= (char)0x3130 and <= (char)0x318F))
                hangul++;
        }
        return letters > 0 && (double)hangul / letters >= 0.3;
    }

    /// <summary>
    /// 번역 시스템 프롬프트를 조립한다. 첫 줄에 �