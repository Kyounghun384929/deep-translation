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

            // LlmGuard 검증: 전역 단일 실행 (동시 요청이 겹치지 않아야 