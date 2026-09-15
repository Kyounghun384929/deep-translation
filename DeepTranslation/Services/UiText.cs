using System.Windows;
using System.Windows.Controls;

namespace DeepTranslation.Services;

/// <summary>설정 창 UI 문자열 (en/ko). XAML 요소의 Tag에 키를 적어 두면 Apply가 일괄 치환한다.</summary>
public static class UiText
{
    public static string Lang { get; set; } = "en";

    public static string Get(string key) =>
        Table.TryGetValue(key, out var v) ? (Lang == "ko" ? v.ko : v.en) : key;

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>Tag="key"인 요소의 Text/Content/Header를 치환한다. "key.tip"이 있으면 ToolTip도 설정.</summary>
    public static void Apply(DependencyObject root)
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            if (d is FrameworkElement { Tag: string key })
            {
                switch (d)
                {
                    case TextBlock tb: tb.Text = Get(key); break;
                    case HeaderedContentControl h: h.Header = Get(key); break;
                    case ContentControl c: c.Content = Get(key); break;
                }
                if (Table.ContainsKey(key + ".tip")) ((FrameworkElement)d).ToolTip = Get(key + ".tip");
            }
            Apply(d);
        }
    }

    private static readonly Dictionary<string, (string en, string ko)> Table = new()
    {
        ["title"] = ("Deep Translation Settings", "Deep Translation 설정"),
        ["save"] = ("Save", "저장"),
        ["cancel"] = ("Cancel", "취소"),

        // 탭
        ["tab.engine"] = ("Engine", "번역 엔진"),
        ["tab.translation"] = ("Translation", "번역"),
        ["tab.general"] = ("General", "일반"),

        // 엔진
        ["engine.embedded"] = ("Built-in model (recommended)", "내장 모델 (권장)"),
        ["engine.external"] = ("External server (OpenAI-compatible)", "외부 서버 (OpenAI 호환)"),
        ["engine.model"] = ("Model", "모델"),
        ["engine.download"] = ("Download", "다운로드"),
        ["engine.delete"] = ("Delete", "삭제"),
        ["engine.binary"] = ("Engine", "엔진"),
        ["engine.binary.download"] = ("Download engine", "엔진 다운로드"),
        ["engine.idle"] = ("Unload when idle:", "유휴 시 메모리 회수:"),
        ["engine.idle.tip"] = ("Unloads the model after no translation for this long. Reloads automatically on the next request.",
                               "번역이 없으면 모델을 메모리에서 내립니다. 다음 번역 때 자동으로 다시 켜집니다."),
        ["idle.never"] = ("Never", "사용 안 함"),
        ["idle.min"] = ("After {0} min", "{0}분 후"),
        ["engine.auto.llama"] = ("Engine: auto — llama.cpp", "엔진: 자동 — llama.cpp"),
        ["engine.auto.ollama"] = ("Engine: auto — Ollama (Smart App Control detected)", "엔진: 자동 — Ollama (스마트 앱 컨트롤 감지됨)"),
        ["engine.installed"] = ("Engine downloaded ✓", "엔진 다운로드됨 ✓"),
        ["engine.missing.llama"] = ("Not downloaded yet (~33 MB, automatic on first translation)", "미다운로드 (약 33MB, 첫 번역 시 자동)"),
        ["engine.missing.ollama"] = ("Not downloaded yet (~1.4 GB download, 119 MB kept, automatic on first translation)", "미다운로드 (약 1.4GB 다운로드 후 119MB 저장, 첫 번역 시 자동)"),
        ["model.downloaded"] = ("downloaded ✓", "다운로드됨 ✓"),
        ["model.ready"] = ("Downloaded — ready to use.", "다운로드 완료 — 바로 사용 가능"),
        ["model.required"] = ("Download required for built-in translation ({0}, once).", "내장 번역에 필요 ({0}, 1회)"),
        ["download.cancelled"] = ("Download stopped. Restart to resume.", "다운로드 중단 — 다시 시작하면 이어받음"),
        ["download.failed"] = ("Download failed: {0}", "다운로드 실패: {0}"),
        ["delete.failed"] = ("Delete failed: {0}", "삭제 실패: {0}"),
        ["delete.confirm"] = ("Delete model file '{0}' ({1})?", "'{0}' 모델 파일({1})을 삭제할까요?"),
        ["model.tag.recommended"] = ("recommended", "권장"),
        ["model.tag.mt"] = ("translation-specialized", "번역 특화"),
        ["model.tag.korean"] = ("Korean-specialized", "한국어 특화"),
        ["model.tag.light"] = ("lightweight", "경량"),
        ["license.apache"] = ("Apache-2.0 · commercial use allowed", "Apache-2.0 · 상업 이용 가능"),
        ["license.nc"] = ("Non-commercial (research) license", "비상업(연구) 용도 한정"),
        ["license.clova"] = ("Commercial use allowed (≤10M MAU)", "상업 이용 가능 (월 1천만 MAU 이하)"),

        // 외부 서버
        ["server.url"] = ("Server address", "서버 주소"),
        ["server.test"] = ("Test", "연결 테스트"),
        ["server.hint"] = ("Any OpenAI-compatible server. Presets: LM Studio :1234, Ollama :11434, llama-server :8080, vLLM :8000", "OpenAI 호환 서버 아무거나. 프리셋: LM Studio :1234, Ollama :11434, llama-server :8080, vLLM :8000"),
        ["server.ok"] = ("Connected — {0} model(s) available", "연결 성공 — 사용 가능한 모델 {0}개"),
        ["server.fail"] = ("Connection failed — check the server is running and the address/API key.", "연결 실패 — 서버 실행 여부와 주소·API 키 확인"),
        ["server.apikey"] = ("API key (optional)", "API 키 (선택)"),
        ["server.apikey.hint"] = ("Only if the server requires one. Sent to this server only.", "서버가 요구할 때만 입력. 위 서버로만 전송"),
        ["server.model"] = ("Model", "모델"),
        ["server.model.auto"] = ("(auto — use loaded model)", "(자동 — 로드된 모델 사용)"),
        ["server.refresh"] = ("Refresh", "새로고침"),

        // 번역
        ["target"] = ("Target language", "번역 대상 언어"),
        ["target.korean"] = ("If source is Korean", "원문이 한국어이면"),
        ["target.korean.tip"] = ("Language to translate into when the copied text is already Korean", "복사한 텍스트가 이미 한국어일 때 대신 번역할 언어"),
        ["glossary"] = ("Glossary", "용어집"),
        ["glossary.hint"] = ("One per line: source = target (e.g. LM Studio = LM Studio)", "한 줄에 하나: 원어 = 번역어 (예: LM Studio = LM Studio)"),

        // 일반
        ["ui.language"] = ("Language:", "언어:"),
        ["hotkey"] = ("Translation hotkey", "번역 단축키"),
        ["hotkey.box.tip"] = ("Click, then press the key combination", "클릭한 뒤 원하는 키 조합을 누르세요"),
        ["hotkey.double"] = ("Press twice", "두 번 누르기"),
        ["hotkey.reset"] = ("Default", "기본값"),
        ["hotkey.hint"] = ("Click the box, then press a key combination.", "입력란 클릭 후 키 조합 입력"),
        ["hotkey.invalid"] = ("Letter/number keys need Ctrl, Alt or Win. (F1–F24 can be used alone)", "문자·숫자 키는 Ctrl, Alt, Win 중 하나와 조합 (F1–F24는 단독 가능)"),
        ["hotkey.copy"] = ("Same as copy (Ctrl+C) — press twice quickly to trigger.", "복사 단축키(Ctrl+C)와 동일 — 빠르게 두 번 눌러야 실행"),
        ["hotkey.double.desc"] = ("Press {0} twice quickly to copy the selection and translate.", "{0} 두 번 → 선택 텍스트 복사 후 번역"),
        ["hotkey.single.desc"] = ("Press {0} to copy the selection and translate.", "{0} → 선택 텍스트 복사 후 번역"),
        ["hotkey.enabled"] = ("Enable hotkey", "단축키 사용"),
        ["popup"] = ("Translation window", "번역 창"),
        ["popup.theme"] = ("Theme:", "테마:"),
        ["popup.theme.tip"] = ("System follows the Windows light/dark setting", "시스템 기본은 Windows의 라이트/다크 설정을 따름"),
        ["theme.system"] = ("System", "시스템 기본"),
        ["theme.light"] = ("Light", "라이트"),
        ["theme.dark"] = ("Dark", "다크"),
        ["popup.cursor"] = ("Show near mouse cursor", "마우스 커서 근처에 표시"),
        ["popup.focus"] = ("Close when clicking outside", "창 밖 클릭 시 닫기"),
        ["startup"] = ("Startup", "시작"),
        ["startup.run"] = ("Run at Windows startup", "Windows 시작 시 자동 실행"),
        ["startup.update"] = ("Check for updates daily", "새 버전 자동 확인 (하루 1회)"),
        ["startup.update.tip"] = ("Checks GitHub Releases and notifies from the tray", "GitHub Releases 확인 후 트레이 알림"),

        ["save.nomodel"] = ("Model '{0}' is not downloaded yet.\nDownload it in Settings → Engine to translate.",
                            "선택한 모델('{0}')이 아직 다운로드되지 않았습니다.\n설정 → 번역 엔진에서 다운로드하세요."),
    };
}
