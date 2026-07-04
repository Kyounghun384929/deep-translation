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
    private KeyboardHookService? _hook;
    private TranslationWindow? _window;
    private HotkeyGesture _gesture = HotkeyGesture.Default;

    /// <summary>현재 단축키의 표시용 문자열 (예: "Ctrl+C 두 번", "Alt+Q").</summary>
    public static string HotkeyDisplayText { get; private set; } = "Ctrl+C 두 번";

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

        StartupManager.Sync(Settings.RunAtStartup);

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

        if (_gesture.IsCopyGesture)
        {
            await Task.Delay(250); // 두 번째 복사가 클립보드에 반영될 시간
        }
        else
        {
            // Ctrl+C가 아닌 단축키는 복사가 일어나지 않았으므로 직접 복사 입력을 보낸다
            InputSimulator.SendCopy();
            await Task.Delay(350); // 대상 앱이 복사를 처리할 시간
        }

        string text = await ReadClipboardTextAsync();
        ShowTranslationWindow(text, translate: true);
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

            string text = "Local LLMs make private, offline translation possible.";
            int idx = Array.IndexOf(args, "--selftest");
            if (idx >= 0 && idx + 1 < args.Length && !args[idx + 1].StartsWith("--"))
                text = args[idx + 1];

            var client = new LmStudioClient();
            Out($"server: {LmStudioClient.NormalizeBaseUrl(settings.ServerUrl)}");
            var models = await client.GetModelsAsync(settings.ServerUrl, CancellationToken.None);
            Out("models: " + string.Join(" | ", models));

            var service = new TranslationService();
            string last = "";
            var result = await service.TranslateAsync(settings, text, t => last = t, CancellationToken.None);
            Out($"model-used: {result.Model}");
            Out($"target: {result.TargetDisplay}");
            Out($"source: {text}");
            Out($"translation: {last}");
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
