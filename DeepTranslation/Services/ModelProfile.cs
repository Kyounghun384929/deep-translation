namespace DeepTranslation.Services;

/// <summary>
/// 모델별 번역 생성 설정 (샘플링·프롬프트 방식). 모델 이름으로 자동 선택하며 설정 창에는 노출하지 않는다.
/// 이름은 카탈로그 Id, 사용자 GGUF 파일 이름, Ollama "dt-" 이름, 외부 서버 모델명 모두 해당한다.
/// 값은 각 모델 카드의 권장값에서 출발한다. 번역 전용 모델(Hy-MT)은 카드 값을 그대로 쓰고,
/// 범용 모델은 카드의 top_k/top_p를 쓰되 온도만 번역용으로 낮춘다 — 낮은 온도가 번역 품질에 유리하다
/// (Peng et al. 2023, arXiv:2303.13780). null 항목은 요청에 넣지 않아 서버 기본값을 따른다.
/// </summary>
public sealed record ModelProfile(double Temperature, double? TopP = null, int? TopK = null, double? MinP = null,
    double? RepeatPenalty = null, bool UserTurnPrompt = false, string SystemPreamble = "")
{
    // Hy-MT2 카드(1.8B·7B): temperature 0.7, top_p 0.6, top_k 20, repetition_penalty 1.05, 기본 system_prompt 없음.
    // 지시는 사용자 턴의 공식 템플릿으로 보낸다 (HY-MT1.5도 같은 값·템플릿).
    private static readonly ModelProfile HyMt = new(0.7, 0.6, 20, 0, 1.05, UserTurnPrompt: true);

    // Qwen3.5·Qwen3-Instruct 카드(비사고 모드): top_p 0.8, top_k 20, min_p 0.
    // presence_penalty는 카드가 "언어 섞임" 위험을 경고하므로 번역에서는 쓰지 않는다.
    private static readonly ModelProfile Qwen = new(0.2, 0.8, 20, 0);

    // Gemma 4 카드: top_p 0.95, top_k 64 (온도 1.0은 범용 대화용).
    private static readonly ModelProfile Gemma = new(0.2, 0.95, 64, 0);

    // EXAONE 3.5 카드: 그리디 디코딩 예시, 제공 시스템 프롬프트를 쓰도록 강하게 권장.
    private static readonly ModelProfile Exaone = new(0, SystemPreamble: "You are EXAONE model from LG AI Research, a helpful assistant.");

    /// <summary>모델 이름에 맞는 프로필. 모르는 모델(HyperCLOVA X 포함 — 카드 비공개)은 null.</summary>
    public static ModelProfile? For(string model)
    {
        string m = model.ToLowerInvariant();
        if (m.Contains("hy-mt") || m.Contains("hunyuan-mt")) return HyMt;
        if (m.Contains("exaone")) return Exaone;
        if (m.Contains("gemma")) return Gemma;
        if (m.Contains("qwen")) return Qwen;
        return null;
    }
}
