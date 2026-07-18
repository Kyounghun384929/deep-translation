using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeepTranslation.Services;
using WinForms = System.Windows.Forms;

namespace DeepTranslation.Windows;

public partial class TranslationWindow : Window
{
    private readonly TranslationService _service = new();
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _cts;
    private bool _suppressTextChanged;
    private bool _suppressTargetChanged;
    private bool _holdOpen;     // 설정 창이 떠 있는 동안 포커스를 잃어도 닫지 않음
    private bool _forceClosing;
    private bool _inFlight;                  // LLM 요청 진행 중 여부
    private string _inFlightSignature = "";  // 진행 중인 요청 식별자 (중복 재시작 방지)

    public TranslationWindow()
    {
        InitializeComponent();
        StatusText.Text = $"텍스트를 선택하고 {App.HotkeyDisplayText} 누르세요";

        _suppressTargetChanged = true;
        TargetCombo.ItemsSource = LanguageMaps.TargetChoices;
        TargetCombo.SelectedItem = LanguageMaps.TargetChoices.Contains(App.Settings.TargetLanguage)
            ? App.Settings.TargetLanguage : "한국어";
        _suppressTargetChanged = false;

        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _debounce.Tick += (s, e) =>
        {
            _debounce.Stop();
            _ = TranslateAsync();
        };
    }

    /// <summary>설정 창 등 다른 경로로 바뀐 대상 언어를 콤보박스에 반영한다.</summary>
    private void SyncTargetCombo()
    {
        if (TargetCombo.SelectedItem as string == App.Settings.TargetLanguage) return;
        _suppressTargetChanged = true;
        TargetCombo.SelectedItem = LanguageMaps.TargetChoices.Contains(App.Settings.TargetLanguage)
            ? App.Settings.TargetLanguage : "한국어";
        _suppressTargetChanged = false;
    }

    /// <summary>단축키로 호출 — 원문을 채우고 즉시 번역한다.</summary>
    public void ShowAndTranslate(string text)
    {
        _suppressTextChanged = true;
        SourceBox.Text = text;
        _suppressTextChanged = false;
        _debounce.Stop();
        Reposition();
        ShowActivateTop();
        _ = TranslateAsync();
    }

    /// <summary>트레이 메뉴에서 호출 — 직접 입력용으로 창만 연다.</summary>
    public void ShowManual()
    {
        if (!IsVisible) Reposition();
        ShowActivateTop();
        SourceBox.Focus();
    }

    private void ShowActivateTop()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
    }

    /// <summary>마우스 커서가 있는 모니터의 작업 영역 안에 창을 배치한다.</summary>
    private void Reposition()
    {
        var p = WinForms.Cursor.Position;
        var area = WinForms.Screen.FromPoint(p).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double w = Width * dpi.DpiScaleX;
        double h = Height * dpi.DpiScaleY;

        double left, top;
        if (App.Settings.PopupNearCursor)
        {
            left = p.X + 18;
            top = p.Y + 18;
        }
        else
        {
            left = area.Left + (area.Width - w) / 2;
            top = area.Top + (area.Height - h) / 2;
        }

        if (left + w > area.Right - 8) left = area.Right - w - 8;
        if (top + h > area.Bottom - 8) top = area.Bottom - h - 8;
        left = Math.Max(area.Left + 8, left);
        top = Math.Max(area.Top + 8, top);

        Left = left / dpi.DpiScaleX;
        Top = top / dpi.DpiScaleY;
    }

    private async Task TranslateAsync(bool force = false)
    {
        string text = SourceBox.Text.Trim();
        string signature = string.Join((char)31,
            App.Settings.ServerUrl, App.Settings.Model, App.Settings.TargetLanguage,
            App.Settings.EngineMode, App.Settings.EmbeddedModelId, text);

        // 같은 내용의 요청이 이미 진행 중이면 재시작하지 않는다 (불필요한 LLM 재호출 방지)
        if (!force && _inFlight && signature == _inFlightSignature) return;

        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();

        OutputBox.Text = "";
        ModelLabel.Text = "";
        SyncTargetCombo();

        if (text.Length == 0)
        {
            SetStatus("번역할 텍스트가 없습니다. 원문을 입력해 보세요.", error: false);
            return;
        }

        _inFlight = true;
        _inFlightSignature = signature;
        SetStatus("번역 중…", error: false);
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await _service.TranslateAsync(App.Settings, text,
                visible =>
                {
                    if (cts.IsCancellationRequested) return;
                    OutputBox.Text = visible;
                    OutputBox.ScrollToEnd();
                },
                cts.Token,
                bypassCache: force, // "다시 번역"은 캐시를 무시하고 새로 생성한다
                onStatus: msg =>
                {
                    // 내장 엔진의 다운로드·로딩 진행 상태를 상태줄에 표시 (델타 처리와 같은 관례)
                    if (cts.IsCancellationRequested) return;
                    SetStatus(msg, error: false);
                });

            if (cts.IsCancellationRequested) return;
            ModelLabel.Text = result.Model;
            // 한국어 원문이라 보조 언어로 번역된 경우 상태 표시줄에 알려준다
            string fallbackNote = result.TargetDisplay != App.Settings.TargetLanguage
                ? $" · 한국어 원문 → {result.TargetDisplay}" : "";
            SetStatus(result.FromCache
                ? $"완료 · 캐시{fallbackNote} — 새로 생성하려면 '다시 번역'"
                : $"완료 · {sw.Elapsed.TotalSeconds:0.0}초{fallbackNote}", error: false);
        }
        catch (OperationCanceledException)
        {
            // 새 번역 요청이나 창 닫힘으로 인한 취소 — 무시
        }
        catch (LmStudioException ex)
        {
            if (cts.IsCancellationRequested) return;
            OutputBox.Text = ex.Message;
            SetStatus(App.Settings.EngineMode == "Embedded"
                ? "오류 — 번역 엔진을 시작하지 못했습니다"
                : "오류 — LM Studio 상태를 확인하세요", error: true);
        }
        catch (Exception ex)
        {
            if (cts.IsCancellationRequested) return;
            OutputBox.Text = ex.Message;
            SetStatus("예기치 않은 오류가 발생했습니다", error: true);
        }
        finally
        {
            // 더 새로운 요청이 시작됐다면 그 요청의 진행 상태를 건드리지 않는다
            if (ReferenceEquals(_cts, cts)) _inFlight = false;
        }
    }

    private void SetStatus(string message, bool error)
    {
        StatusText.Text = message;
        StatusText.Foreground = (Brush)FindResource(error ? "ErrorBrush" : "SubTextBrush");
    }

    private void HideAndCancel()
    {
        _cts?.Cancel();
        _debounce.Stop();
        Hide();
    }

    public void ForceClose()
    {
        _forceClosing = true;
        Close();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_forceClosing)
        {
            e.Cancel = true; // X로 닫아도 프로세스는 살아있고 창만 숨긴다
            HideAndCancel();
            return;
        }
        base.OnClosing(e);
    }

    // ---- 이벤트 핸들러 ----

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => HideAndCancel();

    private void Retranslate_Click(object sender, RoutedEventArgs e)
    {
        _debounce.Stop();
        _ = TranslateAsync(force: true); // 캐시를 무시하고 새로 생성
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (OutputBox.Text.Length == 0) return;
        try
        {
            Clipboard.SetText(OutputBox.Text);
            SetStatus("번역문을 클립보드에 복사했습니다", error: false);
        }
        catch
        {
            SetStatus("클립보드 접근에 실패했습니다. 다시 시도하세요.", error: true);
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _holdOpen = true;
        try { App.Instance.ShowSettingsDialog(this); }
        finally { _holdOpen = false; }
    }

    private void SourceBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressTextChanged) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void TargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTargetChanged || TargetCombo.SelectedItem is not string selected) return;
        if (App.Settings.TargetLanguage == selected) return;

        App.Settings.TargetLanguage = selected;
        App.Settings.Save();
        _debounce.Stop();
        _ = TranslateAsync();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            HideAndCancel();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            _debounce.Stop();
            _ = TranslateAsync(force: true); // 캐시를 무시하고 새로 생성
            e.Handled = true;
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (_holdOpen || PinToggle.IsChecked == true || !App.Settings.CloseOnFocusLoss) return;
        HideAndCancel();
    }
}
