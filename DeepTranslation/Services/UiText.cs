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

        // 메뉴
        ["nav.title"] = ("Settings", "설정"),
        ["tab.engine"] = ("Engine", "번역 엔진"),
        ["tab.translation"] = ("Translation", "번역"),
        ["tab.hotkey"] = ("Hotkey", "단축키"),
        ["tab.general"] = ("General", "일반"),

        // 엔진 — 모델
        ["engine.embedded"] = ("Built-in (recommended)", "내장 엔진 (권장)"),
        ["engine.external"] = ("External server", "외부 서버"),
        ["engine.model"] = ("Model", "모델"),
        ["engine.download"] = ("Download", "다운로드"),
        ["engine.delete"] = ("Delete", "삭제"),
        ["model.downloaded"] = ("Downloaded ✓", "받음 ✓"),
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
        ["license.custom"] = ("Check the model's own license", "모델 배포처의 라이선스 확인 필요"),
        ["custom.name"] = ("My GGUF file", "내 GGUF 파일"),
        ["custom.detail"] = ("Reuse a model from LM Studio etc. — no extra download", "LM Studio 등에서 받은 모델 재사용 — 추가 다운로드 없음"),
        ["custom.browse"] = ("Browse…", "찾아보기…"),
        ["custom.none"] = ("Choose a .gguf file. The LM Studio models folder opens first.", "GGUF 파일 선택 — LM Studio 모델 폴더가 먼저 열림"),
        ["custom.ready"] = ("Runs this file directly (no copy). Check the model's license yourself.", "파일을 복사 없이 직접 실행 · 모델 라이선스는 직접 확인"),
        ["custom.missing"] = ("File not found. Choose it again.", "파일을 찾을 수 없음 — 다시 선택"),

        // 엔진 — 바이너리·업데이트·메모리
        ["engine.binary"] = ("Engine", "엔진"),
        ["engine.binary.download"] = ("Download engine", "엔진 다운로드"),
        ["engine.auto.llama"] = ("Auto: llama.cpp {0} · GPU (Vulkan), CPU fallback", "자동: llama.cpp {0} · GPU(Vulkan), 실패 시 CPU"),
        ["engine.auto.ollama"] = ("Auto: Ollama {0} (Smart App Control detected)", "자동: Ollama {0} (스마트 앱 컨트롤 감지됨)"),
        ["engine.installed"] = ("Installed ✓", "설치됨 ✓"),
        ["engine.cpu"] = ("running on CPU (GPU start failed before)", "CPU로 실행 중 (이전에 GPU 시작 실패)"),
        ["engine.missing.llama"] = ("Not downloaded yet (~33 MB, automatic on first translation)", "미다운로드 (약 33MB, 첫 번역 시 자동)"),
        ["engine.missing.ollama"] = ("Not downloaded yet (~1.4 GB download, 119 MB kept, automatic on first translation)", "미다운로드 (약 1.4GB 다운로드 후 119MB 저장, 첫 번역 시 자동)"),
        ["engine.update.check"] = ("Check for update", "업데이트 확인"),
        ["engine.update.checking"] = ("Checking GitHub for a newer llama.cpp…", "GitHub에서 새 llama.cpp 확인 중…"),
        ["engine.update.latest"] = ("Up to date ({0})", "최신 버전 ({0})"),
        ["engine.update.available"] = ("{0} available — newer builds add speedups and new model support.", "{0} 사용 가능 — 속도 개선·새 모델 지원"),
        ["engine.update.install"] = ("Install {0}", "{0} 설치"),
        ["engine.update.done"] = ("Updated to {0} ✓", "{0}(으)로 업데이트됨 ✓"),
        ["engine.update.failed"] = ("Update check failed: {0}", "업데이트 확인 실패: {0}"),
        ["engine.version.reset"] = ("Revert to default version", "기본 버전으로 되돌리기"),
        ["engine.gpu.retry"] = ("Retry GPU", "GPU 다시 시도"),
        ["engine.gpu.retried"] = ("GPU will be tried again on the next translation.", "다음 번역 때 GPU를 다시 시도"),
        ["engine.context"] = ("Context length", "컨텍스트 길이"),
        ["engine.context.hint"] = ("Longer fits longer text but uses more memory.", "길수록 긴 글을 번역하지만 메모리 사용 증가"),
        ["ctx.default"] = ("default", "기본"),
        ["engine.idle"] = ("Unload when idle", "유휴 시 메모리 회수"),
        ["engine.idle.hint"] = ("Frees RAM/VRAM when unused. Reloads on the next translation.", "쓰지 않으면 RAM/VRAM 반환 · 다음 번역 때 자동 재시작"),
        ["idle.never"] = ("Never", "사용 안 함"),
        ["idle.min"] = ("After {0} min", "{0}분 후"),

        // 외부 서버
        ["server.url"] = ("Server", "서버"),
        ["server.detect"] = ("Find automatically", "자동 찾기"),
        ["server.detecting"] = ("Looking for local servers…", "로컬 서버 찾는 중…"),
        ["server.detect.none"] = ("No server on the usual ports. Start the server first (LM Studio: Developer → Start Server).", "기본 포트에 서버 없음 — 서버를 먼저 실행 (LM Studio: Developer → Start Server)"),
        ["server.test"] = ("Test", "연결 테스트"),
        ["server.hint"] = ("Pick a preset or enter any OpenAI-compatible address. A trailing /v1 is fine.", "프리셋 선택 또는 OpenAI 호환 주소 입력 — 끝의 /v1은 있어도 됨"),
        ["server.ok"] = ("Connected — {0} model(s) available", "연결 성공 — 사용 가능한 모델 {0}개"),
        ["server.fail"] = ("Connection failed — check the server is running and the address/API key.", "연결 실패 — 서버 실행 여부와 주소·API 키 확인"),
        ["server.apikey"] = ("API key (optional)", "API 키 (선택)"),
        ["server.apikey.hint"] = ("Only if the server requires one. Sent to this server only.", "서버가 요구할 때만 입력. 위 서버로만 전송"),
        ["server.model"] = ("Model", "모델"),
        ["server.model.auto"] = ("(auto — use loaded model)", "(자동 — 로드된 모델 사용)"),
        ["server.model.hint"] = ("Auto uses the model already loaded on the server.", "자동은 서버에 로드된 모델을 사용"),
        ["server.refresh"] = ("Refresh", "새로고침"),

        // 번역
        ["target"] = ("Target language", "번역 대상 언어"),
        ["target.korean"] = ("If source is Korean", "원문이 한국어이면"),
        ["target.korean.tip"] = ("Language to translate into when the copied text is already Korean", "복사한 텍스트가 이미 한국어일 때 대신 번역할 언어"),
        ["glossary"] = ("Glossary", "용어집"),
        ["glossary.hint"] = ("One per line: source = target (e.g. LM Studio = LM Studio)", "한 줄에 하나: 원어 = 번역어 (예: LM Studio = LM Studio)"),

        // 단축키
        ["hotkey"] = ("Translation hotkey", "번역 단축키"),
        ["hotkey.box.tip"] = ("Click, then press the key combination", "클릭한 뒤 원하는 키 조합을 누르세요"),
        ["hotkey.double"] = ("Press twice to trigger", "두 번 눌러 실행"),
        ["hotkey.reset"] = ("Default", "기본값"),
        ["hotkey.invalid"] = ("Letter/number keys need Ctrl, Alt or Win. (F1–F24 can be used alone)", "문자·숫자 키는 Ctrl, Alt, Win 중 하나와 조합 (F1–F24는 단독 가능)"),
        ["hotkey.copy"] = ("Same as copy (Ctrl+C) — press twice quickly to trigger.", "복사 단축키(Ctrl+C)와 동일 — 빠르게 두 번 눌러야 실행"),
        ["hotkey.double.desc"] = ("Press {0} twice quickly to copy the selection and translate.", "{0} 두 번 → 선택 텍스트 복사 후 번역"),
        ["hotkey.single.desc"] = ("Press {0} to copy the selection and translate.", "{0} → 선택 텍스트 복사 후 번역"),
        ["hotkey.enabled"] = ("Enable hotkey", "단축키 사용"),

        // 일반
        ["appearance"] = ("Appearance", "화면"),
        ["ui.language"] = ("Language", "언어"),
        ["popup.theme"] = ("Theme", "테마"),
        ["popup.theme.tip"] = ("System follows the Windows light/dark setting", "시스템 기본은 Windows의 라이트/다크 설정을 따름"),
        ["theme.system"] = ("System", "시스템 기본"),
        ["theme.light"] = ("Light", "라이트"),
        ["theme.dark"] = ("Dark", "다크"),
        ["popup.cursor"] = ("Show popup near mouse cursor", "번역 창을 마우스 커서 근처에 표시"),
        ["popup.focus"] = ("Close popup when clicking outside", "창 밖 클릭 시 번역 창 닫기"),
        ["startup"] = ("Startup", "시작"),
        ["startup.run"] = ("Run at Windows startup", "Windows 시작 시 자동 실행"),
        ["startup.update"] = ("Check for updates daily", "새 버전 자동 확인 (하루 1회)"),
        ["startup.update.tip"] = ("Checks GitHub Releases and notifies from the tray", "GitHub Releases 확인 후 트레이 알림"),

        ["save.nomodel"] = ("Model '{0}' is not downloaded yet.\nDownload it in Settings → Engine to translate.",
                            "선택한 모델('{0}')이 아직 다운로드되지 않았습니다.\n설정 → 번역 엔진에서 다운로드하세요."),
        ["save.nocustom"] = ("The selected GGUF file was not found.\nChoose a file in Settings → Engine.",
                             "선택한 GGUF 파일을 찾을 수 없습니다.\n설정 → 번역 엔진에서 파일을 선택하세요."),
    };
}
