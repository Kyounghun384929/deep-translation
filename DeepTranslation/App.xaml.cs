using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using DeepTranslation.Models;
using DeepTranslation.Services;
using DeepTranslation.Windows;
using WinForms = System.Windows.Forms;

namespace DeepTranslation;

public partial class App : Application
{
    public static AppSettings Settings { get; private set; } = new();
    public static App Instance => (App)Current;

    private Mutex? _mutex;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ToolStripMenuItem? _startupMenuItem;
    private KeyboardHookService? _hook;
    private TranslationWindow? _window;
    private HotkeyGesture _gesture = HotkeyGesture.Default;

    /// <summary>현재 단축키의 표시용 문자열 (예: "Ctrl+C 두 번", "Alt+Q").</summary>
    public static string HotkeyDisplayText { get; private set; } = "Ctrl+C 두 번";

    // 클립보드 내용이 바뀔 때마다 증가하는 시퀀스 번호. 복사 완료를 폴링으로 즉시 감지하는 데 쓴다.
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (s, ex) =>
        {
            MessageBox.Show("예기치 않은 오류가 발생했습니다:\n" + ex.Exception.Message,
                "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Error);
            ex.Handled = true;
        };

        if (e.Args.Contains("--selftest"))
        {
            _ = RunSelfTestAsync(e.Args);
            return;
        }

        _mutex = new Mutex(true, @"Local\DeepTranslation_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("Deep Translation이 이미 실행 중입니다.\n작업 표시줄 오른쪽 트레이 아이콘을 확인하세요.",
                "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        SetupTray();

        _hook = new KeyboardHookService();
        _hook.HotkeyTriggered += OnHotkey;
        ConfigureHook();
        if (Settings.HotkeyEnabled) _hook.Start();

        // 첫 팝업이 즉시 뜨도록 번역 창을 미리 생성해 둔다 (표시는 하지 않음)
        _window = new TranslationWindow();

        // 설치 프로그램의 '자동 시작' 선택(레지스트리)을 설정에 먼저 반영한다.
        // 이 단계가 없으면 첫 실행 시 기본값(false)이 설치 시 등록된 Run 값을 지워버린다.
        if (!Settings.RunAtStartup && StartupManager.IsEnabled())
        {
            Settings.RunAtStartup = true;
            Settings.Save();
        }
        StartupManager.Sync(Settings.RunAtStartup);
        UpdateTrayStartupCheck();

        if (!e.Args.Contains("--autostart"))
        {
            _tray?.ShowBalloonTip(4000, "Deep Translation 실행 중",
                $"텍스트를 선택하고 {HotkeyDisplayText} 누르면 번역 창이 열립니다.",
                WinForms.ToolTipIcon.Info);
        }
    }

    /// <summary>설정의 단축키 문자열을 훅에 반영하고 표시용 텍스트를 갱신한다.</summary>
    private void ConfigureHook()
    {
        _gesture = HotkeyGesture.TryParse(Settings.HotkeyGesture) ?? HotkeyGesture.Default;
        bool doublePress = Settings.HotkeyDoublePress || _gesture.IsCopyGesture;

        if (_hook != null)
        {
            _hook.TriggerVkCode = _gesture.VkCode;
            _hook.NeedCtrl = _gesture.Ctrl;
            _hook.NeedAlt = _gesture.Alt;
            _hook.NeedShift = _gesture.Shift;
            _hook.NeedWin = _gesture.Win;
            _hook.RequireDoublePress = doublePress;
            _hook.DoublePressWindowMs = Settings.DoublePressWindowMs;
        }

        HotkeyDisplayText = doublePress ? $"{_gesture} 두 번" : _gesture.ToString();
        if (_tray != null)
        {
            string text = $"Deep Translation — {HotkeyDisplayText}";
            _tray.Text = text.Length > 63 ? text[..63] : text;
        }
    }

    private void SetupTray()
    {
        _tray = new WinForms.NotifyIcon
        {
            Text = "Deep Translation — Ctrl+C, C 번역",
            Visible = true,
            Icon = LoadAppIcon()
        };

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("번역 창 열기", null, (s, e) => ShowTranslationWindow());
        menu.Items.Add("설정", null, (s, e) => ShowSettingsDialog());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        _startupMenuItem = new WinForms.ToolStripMenuItem("Windows 시작 시 자동 실행")
        {
            Checked = Settings.RunAtStartup
        };
        _startupMenuItem.Click += (s, e) => ToggleStartup();
        menu.Items.Add(_startupMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("종료", null, (s, e) => ExitApplication());
        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (s, e) => ShowTranslationWindow();
    }

    private static System.Drawing.Icon LoadAppIcon()
    {
        var sri = GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))
                  ?? throw new InvalidOperationException("앱 아이콘 리소스를 찾을 수 없습니다.");
        return new System.Drawing.Icon(sri.Stream);
    }

    /// <summary>단축키 감지 시 호출 — 클립보드 텍스트를 읽어 번역 창을 띄운다.</summary>
    private async void OnHotkey()
    {
        // 우리 번역 창 안에서 누른 단축키는 무시 (번역문 복사 시 재번역 방지)
        if (_window is { IsVisible: true, IsActive: true }) return;

        // 트리거 시점의 클립보드 시퀀스를 캡처해 두고, 복사로 값이 바뀌면 즉시 진행한다(고정 대기 제거).
        uint startSeq = GetClipboardSequenceNumber();

        int timeoutMs;
        if (_gesture.IsCopyGesture)
        {
            // Ctrl+C 두 번: 첫 번째 C가 이미 복사했을 수 있으므로 타임아웃 시에도 그냥 클립보드를 읽고 진행한다.
            timeoutMs = 300;
        }
        else
        {
            // Ctrl+C가 아닌 단축키는 복사가 일어나지 않았으므로 직접 복사 입력을 보낸다
            InputSimulator.SendCopy();
            timeoutMs = 500;
        }

        await WaitForClipboardChangeAsync(startSeq, timeoutMs);

        string text = await ReadClipboardTextAsync();
        ShowTranslationWindow(text, translate: true);
    }

    /// <summary>
    /// 클립보드 시퀀스 번호가 startSeq에서 바뀔 때까지 짧은 간격으로 폴링한다.
    /// 값이 바뀌면 즉시, 아니면 timeoutMs 후에 반환한다(타임아웃 시에도 호출부가 클립보드를 읽고 진행).
    /// </summary>
    private static async Task WaitForClipboardChangeAsync(uint startSeq, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (GetClipboardSequenceNumber() != startSeq) return;
            await Task.Delay(15);
        }
    }

    private static async Task<string> ReadClipboardTextAsync()
    {
        for (int i = 0; i < 6; i++)
        {
            try
            {
                return Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (ExternalException)
            {
                // 다른 프로세스가 클립보드를 점유 중 — 잠시 후 재시도
                await Task.Delay(60);
            }
        }
        return "";
    }

    public void ShowTranslationWindow(string? text = null, bool translate = false)
    {
        _window ??= new TranslationWindow();
        if (translate) _window.ShowAndTranslate(text ?? "");
        else _window.ShowManual();
    }

    public void ShowSettingsDialog(Window? owner = null)
    {
        var win = new SettingsWindow();
        if (owner is { IsVisible: true }) win.Owner = owner;
        win.ShowDialog();
    }

    /// <summary>설정 저장 후 즉시 반영 (단축키, 훅, 자동 시작).</summary>
    public void ApplySettings()
    {
        Settings.Save();
        ConfigureHook();
        if (_hook != null)
        {
            if (Settings.HotkeyEnabled && !_hook.IsRunning) _hook.Start();
            else if (!Settings.HotkeyEnabled && _hook.IsRunning) _hook.Stop();
        }
        StartupManager.Sync(Settings.RunAtStartup);
        UpdateTrayStartupCheck();
        // 엔진 모드가 바뀌었거나 내장 모델 선택이 바뀌었으면 실행 중인 llama-server를 내린다
        EmbeddedEngine.ApplySettings(Settings);
    }

    /// <summary>트레이 메뉴에서 자동 시작을 즉시 켜고 끈다.</summary>
    private void ToggleStartup()
    {
        Settings.RunAtStartup = !Settings.RunAtStartup;
        Settings.Save();
        StartupManager.Sync(Settings.RunAtStartup);
        UpdateTrayStartupCheck();
    }

    /// <summary>트레이 메뉴의 자동 시작 체크 표시를 현재 설정과 일치시킨다.</summary>
    private void UpdateTrayStartupCheck()
    {
        if (_startupMenuItem != null) _startupMenuItem.Checked = Settings.RunAtStartup;
    }

    private void ExitApplication()
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
            _tray = null;
        }
        _hook?.Dispose();
        _window?.ForceClose();
        EmbeddedEngine.Stop(); // 내장 엔진 프로세스 정리
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _hook?.Dispose();
        _mutex?.Dispose();
        EmbeddedEngine.Stop(); // 모든 종료 경로에서 llama-server가 남지 않도록 보장
        base.OnExit(e);
    }

    /// <summary>
    /// GUI 없이 LM Studio 연결과 번역을 검증하는 개발용 모드.
    /// 사용: DeepTranslation.exe --selftest "번역할 문장" [--out 결과파일]
    /// </summary>
    private async Task RunSelfTestAsync(string[] args)
    {
        int exitCode = 0;
        var log = new StringBuilder();
        void Out(string s) { log.AppendLine(s); Console.WriteLine(s); }

        try
        {
            Settings = AppSettings.Load();
            var settings = Settings;

            // UI 스모크 테스트: 창 XAML이 런타임에 정상 로드되는지 확인
            _ = new TranslationWindow();
            _ = new SettingsWindow();
            Out("ui: TranslationWindow / SettingsWindow OK");

            // 단축키 파싱 검증
            foreach (var g in new[] { "Ctrl+C", "Alt+Q", "Ctrl+Shift+T", "F9", "Q", "Shift+Q" })
            {
                var parsed = HotkeyGesture.TryParse(g);
                Out($"gesture: '{g}' -> {(parsed == null ? "null" : $"{parsed} vk=0x{parsed.VkCode:X2} valid={parsed.IsValid} copy={parsed.IsCopyGesture}")}");
            }

            // LlmGuard 검증: 캐시 저장/조회 (Language 필드 및 새 MakeKey 시그니처 포함)
            string key = LlmGuard.MakeKey("srv", "model-x", "Korean", "English", "", 0.2, "hello");
            LlmGuard.Store(key, "안녕", "model-x", "Korean");
            bool hit = LlmGuard.TryGet(key, out var cachedEntry);
            bool miss = !LlmGuard.TryGet(LlmGuard.MakeKey("srv", "model-x", "Korean", "English", "", 0.2, "different"), out _);
            // 용어집이 키에 반영되는지 — 다른 용어집이면 miss여야 한다
            bool glossaryMiss = !LlmGuard.TryGet(LlmGuard.MakeKey("srv", "model-x", "Korean", "English", "term=x", 0.2, "hello"), out _);
            Out($"guard-cache: hit={hit} text='{cachedEntry.Text}' model={cachedEntry.Model} lang={cachedEntry.Language} miss-on-other={miss} glossary-key={glossaryMiss}");

            // LlmGuard 검증: 전역 단일 실행 (동시 요청이 겹치지 않아야 함)
            int concurrent = 0, maxConcurrent = 0;
            var tasks = Enumerable.Range(0, 3).Select(_ => LlmGuard.RunExclusiveAsync(async () =>
            {
                int now = Interlocked.Increment(ref concurrent);
                maxConcurrent = Math.Max(maxConcurrent, now);
                await Task.Delay(80);
                Interlocked.Decrement(ref concurrent);
                return 0;
            }, CancellationToken.None)).ToArray();
            await Task.WhenAll(tasks);
            Out($"guard-serialize: maxConcurrent={maxConcurrent} (1이어야 정상)");

            // MarkerFilter 검증: 마커 있음/없음/개행 없는 짧은 출력/부분 도착
            {
                var f1 = new MarkerFilter();
                string body1 = f1.Finish("@@Korean@@\n안녕하세요");
                Out($"marker-present: lang={f1.MarkerLanguage} body='{body1}' (Korean/안녕하세요여야 정상)");

                var f2 = new MarkerFilter();
                string body2 = f2.Finish("Hello there");
                Out($"marker-absent: lang='{f2.MarkerLanguage}' body='{body2}' (빈 lang / 'Hello there'여야 정상)");

                var f3 = new MarkerFilter();
                string body3 = f3.Finish("@@English@@ Hello"); // 개행 없이 끝나는 짧은 출력
                Out($"marker-noline: lang={f3.MarkerLanguage} body='{body3}' (English/Hello여야 정상)");

                // 부분 도착 시뮬레이션: 마커가 덜 온 동안에는 표시 보류(빈 문자열), 완성 후 본문
                var f4 = new MarkerFilter();
                string p1 = f4.Process("@@Kor");             // 아직 마커 미완성 → 보류
                string p2 = f4.Process("@@Korean@@\n반가");    // 완성 → 본문
                Out($"marker-partial: hold='{p1}' after='{p2}' (hold는 빈 문자열, after는 '반가'여야 정상)");

                // 미등록 언어 마커는 ToDisplay가 null → 표시명 폴백 확인
                Out($"marker-todisplay: Korean->{LanguageMaps.ToDisplay("Korean")} Klingon->{LanguageMaps.ToDisplay("Klingon") ?? "null"}");
            }

            // BuildSystemPrompt 용어집 주입 검증
            {
                string promptNoGloss = LanguageMaps.BuildSystemPrompt("Korean", "English", "");
                string promptGloss = LanguageMaps.BuildSystemPrompt("Korean", "English", "LM Studio = LM Studio\n = 빈원어무시\nfoo=bar");
                bool hasMarker = promptGloss.Contains("@@Korean@@");
                bool noSectionWhenEmpty = !promptNoGloss.Contains("TERMINOLOGY");
                bool hasSectionWhenSet = promptGloss.Contains("TERMINOLOGY") && promptGloss.Contains("\"LM Studio\" → \"LM Studio\"") && promptGloss.Contains("\"foo\" → \"bar\"");
                bool ignoresBadLine = !promptGloss.Contains("빈원어무시");
                Out($"prompt: marker={hasMarker} noSectionEmpty={noSectionWhenEmpty} hasSectionSet={hasSectionWhenSet} ignoresBadLine={ignoresBadLine}");
            }

            // --no-llm: 모델 로드(JIT)를 유발하지 않고 UI·파싱 검증만 수행
            if (args.Contains("--no-llm"))
            {
                Out("selftest done (LLM 호출 생략)");
            }
            else
            {
                string text = "Local LLMs make private, offline translation possible.";
                int idx = Array.IndexOf(args, "--selftest");
                if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
                    text = args[idx + 1];

                // 모델 목록 조회는 LM Studio 모드에서만 의미가 있다 (내장 모드는 엔진이 직접 기동)
                if (settings.EngineMode == "LmStudio")
                {
                    var client = new LmStudioClient();
                    Out($"server: {LmStudioClient.NormalizeBaseUrl(settings.ServerUrl)}");
                    var models = await client.GetModelsAsync(settings.ServerUrl, CancellationToken.None);
                    Out("models: " + string.Join(" | ", models));
                }
                else
                {
                    Out($"engine: embedded ({settings.EmbeddedModelId})");
                }

                var service = new TranslationService();
                string last = "";
                var result = await service.TranslateAsync(settings, text, t => last = t, CancellationToken.None);
                Out($"model-used: {result.Model}");
                Out($"target: {result.TargetDisplay}");
                Out($"source: {text}");
                Out($"translation: {last}");

                // 동일 요청 재호출 — LLM을 다시 부르지 않고 캐시로 응답해야 한다
                var sw2 = System.Diagnostics.Stopwatch.StartNew();
                var second = await service.TranslateAsync(settings, text, _ => { }, CancellationToken.None);
                Out($"repeat-call: fromCache={second.FromCache} elapsed={sw2.ElapsedMilliseconds}ms (fromCache=True여야 정상)");

                // bypassCache=true는 캐시를 무시하고 새로 생성해야 한다
                var third = await service.TranslateAsync(settings, text, _ => { }, CancellationToken.None, bypassCache: true);
                Out($"regen-call: fromCache={third.FromCache} (False여야 정상)");
            }
        }
        catch (Exception ex)
        {
            Out("SELFTEST FAILED: " + ex.Message);
            exitCode = 1;
        }

        int outIdx = Array.IndexOf(args, "--out");
        if (outIdx >= 0 && outIdx + 1 < args.Length)
        {
            try { File.WriteAllText(args[outIdx + 1], log.ToString()); } catch { }
        }
        Shutdown(exitCode);
    }
}
