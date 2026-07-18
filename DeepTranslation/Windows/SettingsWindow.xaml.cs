using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeepTranslation.Models;
using DeepTranslation.Services;

namespace DeepTranslation.Windows;

public partial class SettingsWindow : Window
{
    private const string AutoModel = "(자동 — 로드된 모델 사용)";
    private readonly LmStudioClient _client = new();
    private HotkeyGesture _gesture = HotkeyGesture.Default;

    // 유휴 언로드 선택지 (표시 문자열 ↔ 분)
    private static readonly string[] IdleChoices = { "사용 안 함", "3분 후", "5분 후", "10분 후" };
    private static readonly int[] IdleMinuteValues = { 0, 3, 5, 10 };

    // 테마 선택지 (표시 문자열 ↔ 설정값)
    private static readonly string[] ThemeChoices = { "시스템 기본", "라이트", "다크" };
    private static readonly string[] ThemeValues = { "System", "Light", "Dark" };

    // 엔진 백엔드 선택지 (표시 문자열 ↔ 설정값)

    private CancellationTokenSource? _downloadCts;
    private bool _suppressModelChanged;

    public SettingsWindow()
    {
        InitializeComponent();

        var s = App.Settings;
        ServerBox.Text = s.ServerUrl;

        // 번역 엔진 섹션
        _suppressModelChanged = true;
        foreach (var m in ModelCatalog.All) EmbeddedModelBox.Items.Add(FormatModelLabel(m));
        int modelIdx = 0;
        for (int i = 0; i < ModelCatalog.All.Count; i++)
            if (ModelCatalog.All[i].Id == s.EmbeddedModelId) { modelIdx = i; break; }
        EmbeddedModelBox.SelectedIndex = modelIdx;
        _suppressModelChanged = false;

        foreach (var c in IdleChoices) IdleUnloadBox.Items.Add(c);
        int idleIdx = Array.IndexOf(IdleMinuteValues, s.IdleUnloadMinutes);
        IdleUnloadBox.SelectedIndex = idleIdx >= 0 ? idleIdx : 2; // 목록에 없는 값이면 기본 5분

        bool embedded = s.EngineMode != "LmStudio";
        EmbeddedRadio.IsChecked = embedded;
        LmStudioRadio.IsChecked = !embedded;
        RefreshEmbeddedUi();
        RefreshBackendInfo();

        TargetBox.ItemsSource = LanguageMaps.TargetChoices;
        TargetBox.SelectedItem = LanguageMaps.TargetChoices.Contains(s.TargetLanguage) ? s.TargetLanguage : "한국어";

        KoreanSourceBox.ItemsSource = LanguageMaps.KoreanSourceChoices;
        KoreanSourceBox.SelectedItem = LanguageMaps.KoreanSourceChoices.Contains(s.KoreanSourceTarget) ? s.KoreanSourceTarget : "영어";

        GlossaryBox.Text = s.Glossary;

        foreach (var c in ThemeChoices) ThemeBox.Items.Add(c);
        int themeIdx = Array.IndexOf(ThemeValues, s.Theme);
        ThemeBox.SelectedIndex = themeIdx >= 0 ? themeIdx : 0; // 알 수 없는 값이면 시스템 기본

        ModelBox.Items.Add(AutoModel);
        if (!string.IsNullOrWhiteSpace(s.Model))
        {
            ModelBox.Items.Add(s.Model);
            ModelBox.SelectedIndex = 1;
        }
        else
        {
            ModelBox.SelectedIndex = 0;
        }

        _gesture = HotkeyGesture.TryParse(s.HotkeyGesture) ?? HotkeyGesture.Default;
        HotkeyBox.Text = _gesture.ToString();
        DoublePressCheck.IsChecked = s.HotkeyDoublePress || _gesture.IsCopyGesture;
        UpdateHotkeyUi(error: null);

        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        StartupCheck.IsChecked = s.RunAtStartup;
        UpdateCheck.IsChecked = s.AutoUpdateCheck;
        CursorCheck.IsChecked = s.PopupNearCursor;
        FocusCheck.IsChecked = s.CloseOnFocusLoss;

        Loaded += async (_, _) => await RefreshModelsAsync(silent: true);
    }

    private async Task RefreshModelsAsync(bool silent)
    {
        try
        {
            var current = ModelBox.SelectedItem as string;
            // 연결 테스트·새로고침은 항상 서버에 최신 상태를 다시 물어본다
            var models = await _client.GetModelsAsync(ServerBox.Text, CancellationToken.None, bypassCache: true);

            ModelBox.Items.Clear();
            ModelBox.Items.Add(AutoModel);
            foreach (var m in models) ModelBox.Items.Add(m);
            ModelBox.SelectedItem = current != null && ModelBox.Items.Contains(current) ? current : AutoModel;

            ServerStatus.Text = $"연결 성공 — 사용 가능한 모델 {models.Count}개";
            ServerStatus.Foreground = Brushes.Green;
        }
        catch (Exception)
        {
            if (!silent)
            {
                ServerStatus.Text = "연결 실패 — LM Studio를 실행하고 개발자 탭에서 서버를 시작했는지 확인하세요.";
                ServerStatus.Foreground = Brushes.Red;
            }
        }
    }

    // ---- 번역 엔진 ----

    private ModelCatalog.ModelInfo SelectedModel => ModelCatalog.All[Math.Max(0, EmbeddedModelBox.SelectedIndex)];

    private static string FormatModelLabel(ModelCatalog.ModelInfo m) =>
        $"{m.DisplayName} · {m.SizeText}" + (m.IsDownloaded ? " · 다운로드됨 ✓" : "");

    private void EngineMode_Changed(object sender, RoutedEventArgs e)
    {
        bool embedded = EmbeddedRadio.IsChecked == true;
        EmbeddedPanel.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        LmStudioPanel.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
    }

    private void EmbeddedModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelChanged) return;
        RefreshEmbeddedUi();
    }

    /// <summary>
    /// 자동 선택된 내장 백엔드를 읽기 전용으로 안내한다 (사용자 선택 없음).
    /// SAC 켜짐이면 서명된 Ollama, 꺼짐이면 경량 llama.cpp를 자동으로 사용한다.
    /// </summary>
    private void RefreshBackendInfo()
    {
        BackendInfo.Text = EmbeddedEngine.IsSmartAppControlOn
            ? "엔진: 자동 — 스마트 앱 컨트롤이 감지되어 서명된 Ollama 엔진을 사용합니다."
            : "엔진: 자동 — 경량 llama.cpp 엔진을 사용합니다.";
    }

    /// <summary>선택 모델 기준으로 라이선스·버튼·상태 표시를 갱신한다.</summary>
    private void RefreshEmbeddedUi()
    {
        var m = SelectedModel;
        EmbeddedLicense.Text = m.LicenseNote;

        bool downloading = _downloadCts != null;
        bool downloaded = m.IsDownloaded;
        DownloadButton.Content = downloading ? "취소" : "다운로드";
        DownloadButton.IsEnabled = downloading || !downloaded;
        DeleteModelButton.Visibility = !downloading && downloaded ? Visibility.Visible : Visibility.Collapsed;
        if (!downloading)
        {
            DownloadStatus.Text = downloaded
                ? "다운로드 완료 — 바로 사용할 수 있습니다."
                : $"모델을 내려받아야 내장 번역을 사용할 수 있습니다. ({m.SizeText}, 1회)";
        }
    }

    // 다운로드·삭제 후 콤보 항목의 "다운로드됨 ✓" 표시를 갱신한다
    private void RefreshModelLabels()
    {
        _suppressModelChanged = true;
        int sel = EmbeddedModelBox.SelectedIndex;
        for (int i = 0; i < ModelCatalog.All.Count; i++)
            EmbeddedModelBox.Items[i] = FormatModelLabel(ModelCatalog.All[i]);
        EmbeddedModelBox.SelectedIndex = sel < 0 ? 0 : sel;
        _suppressModelChanged = false;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null) // 진행 중이면 버튼은 [취소]로 동작
        {
            _downloadCts.Cancel();
            return;
        }

        var m = SelectedModel;
        if (m.IsDownloaded) return;

        var cts = _downloadCts = new CancellationTokenSource();
        EmbeddedModelBox.IsEnabled = false; // 받는 동안 모델 변경 방지
        DownloadBar.Visibility = Visibility.Visible;
        DownloadBar.Value = 0;
        RefreshEmbeddedUi();
        try
        {
            await ModelDownloader.DownloadAsync(m.Url, m.FilePath, m.SizeBytes,
                (received, total) =>
                {
                    double pct = received * 100.0 / total;
                    DownloadBar.Value = pct;
                    DownloadStatus.Text = $"{received / 1048576.0:0} / {total / 1048576.0:0} MB ({pct:0}%)";
                }, cts.Token);
        }
        catch (OperationCanceledException)
        {
            DownloadStatus.Text = "다운로드를 중단했습니다. 다시 시작하면 이어받습니다.";
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "다운로드 실패: " + ex.Message;
        }
        finally
        {
            _downloadCts = null;
            cts.Dispose();
            EmbeddedModelBox.IsEnabled = true;
            DownloadBar.Visibility = Visibility.Collapsed;
            RefreshModelLabels();
            RefreshEmbeddedUi();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        var m = SelectedModel;
        if (MessageBox.Show($"'{m.DisplayName}' 모델 파일({m.SizeText})을 삭제할까요?",
                "Deep Translation", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            // 실행 중인 엔진이 파일을 잡고 있으면 먼저 내린다
            if (EmbeddedEngine.RunningModelId == m.Id) EmbeddedEngine.Stop();
            if (File.Exists(m.FilePath)) File.Delete(m.FilePath);
            if (File.Exists(m.FilePath + ".part")) File.Delete(m.FilePath + ".part");
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = "삭제 실패: " + ex.Message;
            return;
        }
        RefreshModelLabels();
        RefreshEmbeddedUi();
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadCts?.Cancel(); // 창이 닫히면 진행 중 다운로드 중단 (.part가 남아 이어받기 가능)
        base.OnClosed(e);
    }

    // ---- 단축키 설정 ----

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // 캡처 중 다른 컨트롤로 포커스가 이동하지 않게 모두 삼킨다
        var key = e.Key switch
        {
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.System => e.SystemKey,
            _ => e.Key
        };

        var gesture = HotkeyGesture.FromKey(Keyboard.Modifiers, key);
        if (gesture == null) return; // 수정키만 누른 상태 — 나머지 키 입력을 기다림

        if (!gesture.IsValid)
        {
            UpdateHotkeyUi("문자·숫자 키는 Ctrl, Alt, Win 중 하나와 조합해야 합니다. (F1–F24는 단독 사용 가능)");
            return;
        }

        _gesture = gesture;
        HotkeyBox.Text = gesture.ToString();
        if (gesture.IsCopyGesture) DoublePressCheck.IsChecked = true;
        UpdateHotkeyUi(error: null);
    }

    private void DoublePress_Changed(object sender, RoutedEventArgs e) => UpdateHotkeyUi(error: null);

    private void HotkeyReset_Click(object sender, RoutedEventArgs e)
    {
        _gesture = HotkeyGesture.Default;
        HotkeyBox.Text = _gesture.ToString();
        DoublePressCheck.IsChecked = true;
        UpdateHotkeyUi(error: null);
    }

    private void UpdateHotkeyUi(string? error)
    {
        // Ctrl+C는 일반 복사와 겹치므로 두 번 누르기를 강제한다
        DoublePressCheck.IsEnabled = !_gesture.IsCopyGesture;

        if (error != null)
        {
            HotkeyHint.Text = error;
            HotkeyHint.Foreground = Brushes.Red;
            return;
        }

        HotkeyHint.Foreground = Brushes.Gray;
        bool doublePress = DoublePressCheck.IsChecked == true;
        HotkeyHint.Text = _gesture.IsCopyGesture
            ? "복사 단축키(Ctrl+C)와 같으므로 빠르게 두 번 눌러야 실행됩니다."
            : doublePress
                ? $"{_gesture} 키를 빠르게 두 번 누르면 선택한 텍스트를 자동으로 복사해 번역합니다."
                : $"{_gesture} 키를 누르면 선택한 텍스트를 자동으로 복사해 번역합니다.";
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.EngineMode = LmStudioRadio.IsChecked == true ? "LmStudio" : "Embedded";
        s.EmbeddedModelId = SelectedModel.Id;
        s.IdleUnloadMinutes = IdleMinuteValues[Math.Max(0, IdleUnloadBox.SelectedIndex)];
        s.ServerUrl = LmStudioClient.NormalizeBaseUrl(ServerBox.Text);
        s.Model = ModelBox.SelectedItem as string == AutoModel ? "" : ModelBox.SelectedItem as string ?? "";
        s.TargetLanguage = TargetBox.SelectedItem as string ?? "한국어";
        s.KoreanSourceTarget = KoreanSourceBox.SelectedItem as string ?? "영어";
        s.Glossary = GlossaryBox.Text;
        s.Theme = ThemeValues[Math.Max(0, ThemeBox.SelectedIndex)];
        s.HotkeyGesture = _gesture.ToString();
        s.HotkeyDoublePress = DoublePressCheck.IsChecked == true || _gesture.IsCopyGesture;
        s.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        s.RunAtStartup = StartupCheck.IsChecked == true;
        s.AutoUpdateCheck = UpdateCheck.IsChecked == true;
        s.PopupNearCursor = CursorCheck.IsChecked == true;
        s.CloseOnFocusLoss = FocusCheck.IsChecked == true;

        App.Instance.ApplySettings();

        // 미다운로드 모델로 저장하는 것은 허용하되 안내한다
        if (s.EngineMode == "Embedded" && !SelectedModel.IsDownloaded)
        {
            MessageBox.Show(
                $"선택한 모델('{SelectedModel.DisplayName}')이 아직 다운로드되지 않았습니다.\n" +
                "번역을 사용하려면 설정 → 번역 엔진에서 모델을 다운로드하세요.",
                "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }
}
