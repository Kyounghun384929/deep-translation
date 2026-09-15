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
    private static string AutoModel => UiText.Get("server.model.auto");
    private readonly LmStudioClient _client = new();
    private HotkeyGesture _gesture = HotkeyGesture.Default;

    private static readonly int[] IdleMinuteValues = { 0, 3, 5, 10 };
    private static readonly string[] ThemeValues = { "System", "Light", "Dark" };
    private static readonly string[] UiLanguageValues = { "en", "ko" };
    private static readonly string[] UiLanguageChoices = { "English", "한국어" };

    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _engineDownloadCts; // 모델 다운로드와 독립 (동시 진행 가능)
    private bool _suppressModelChanged;

    public SettingsWindow()
    {
        InitializeComponent();

        var s = App.Settings;
        UiText.Lang = s.UiLanguage;
        ServerBox.Text = s.ServerUrl;
        ApiKeyBox.Text = s.ApiKey;

        _suppressModelChanged = true;
        foreach (var _ in ModelCatalog.All) EmbeddedModelBox.Items.Add("");
        int modelIdx = 0;
        for (int i = 0; i < ModelCatalog.All.Count; i++)
            if (ModelCatalog.All[i].Id == s.EmbeddedModelId) { modelIdx = i; break; }
        EmbeddedModelBox.SelectedIndex = modelIdx;
        _suppressModelChanged = false;

        foreach (var _ in IdleMinuteValues) IdleUnloadBox.Items.Add("");
        int idleIdx = Array.IndexOf(IdleMinuteValues, s.IdleUnloadMinutes);
        IdleUnloadBox.SelectedIndex = idleIdx >= 0 ? idleIdx : 2; // 목록에 없는 값이면 기본 5분

        bool embedded = s.EngineMode != "LmStudio";
        EmbeddedRadio.IsChecked = embedded;
        LmStudioRadio.IsChecked = !embedded;

        foreach (var _ in LanguageMaps.TargetChoices) TargetBox.Items.Add("");
        TargetBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageMaps.TargetChoices, s.TargetLanguage));
        foreach (var _ in LanguageMaps.KoreanSourceChoices) KoreanSourceBox.Items.Add("");
        KoreanSourceBox.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageMaps.KoreanSourceChoices, s.KoreanSourceTarget));

        GlossaryBox.Text = s.Glossary;

        foreach (var _ in ThemeValues) ThemeBox.Items.Add("");
        int themeIdx = Array.IndexOf(ThemeValues, s.Theme);
        ThemeBox.SelectedIndex = themeIdx >= 0 ? themeIdx : 0; // 알 수 없는 값이면 시스템 기본

        foreach (var c in UiLanguageChoices) UiLanguageBox.Items.Add(c);
        UiLanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(UiLanguageValues, s.UiLanguage));

        ModelBox.Items.Add(AutoModel);
        if (!string.IsNullOrWhiteSpace(s.Model)) { ModelBox.Items.Add(s.Model); ModelBox.SelectedIndex = 1; }
        else ModelBox.SelectedIndex = 0;

        _gesture = HotkeyGesture.TryParse(s.HotkeyGesture) ?? HotkeyGesture.Default;
        HotkeyBox.Text = _gesture.ToString();
        DoublePressCheck.IsChecked = s.HotkeyDoublePress || _gesture.IsCopyGesture;
        ApplyLanguage();

        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        StartupCheck.IsChecked = s.RunAtStartup;
        UpdateCheck.IsChecked = s.AutoUpdateCheck;
        CursorCheck.IsChecked = s.PopupNearCursor;
        FocusCheck.IsChecked = s.CloseOnFocusLoss;

        Loaded += async (_, _) => await RefreshModelsAsync(silent: true);
    }

    private void UiLanguage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (UiLanguageBox.SelectedIndex < 0 || !IsLoaded) return;
        UiText.Lang = UiLanguageValues[UiLanguageBox.SelectedIndex];
        ApplyLanguage();
    }

    /// <summary>고정 라벨(Tag)과 코드에서 채우는 목록·상태 문자열을 현재 언어로 다시 그린다. 선택 상태는 유지.</summary>
    private void ApplyLanguage()
    {
        Title = UiText.Get("title");
        UiText.Apply(this);

        RefreshModelLabels();
        RelabelItems(IdleUnloadBox, IdleMinuteValues.Select(m => m == 0 ? UiText.Get("idle.never") : UiText.Format("idle.min", m)));
        RelabelItems(ThemeBox, ThemeValues.Select(v => UiText.Get("theme." + v.ToLowerInvariant())));
        RelabelItems(TargetBox, LanguageMaps.TargetChoices.Select(LanguageName));
        RelabelItems(KoreanSourceBox, LanguageMaps.KoreanSourceChoices.Select(LanguageName));

        // 외부 서버 모델 목록의 "(자동)" 항목은 항상 첫 번째
        bool wasAuto = ModelBox.SelectedIndex == 0;
        string typed = ModelBox.Text;
        ModelBox.Items[0] = AutoModel;
        if (wasAuto) ModelBox.SelectedIndex = 0; else ModelBox.Text = typed;

        RefreshEmbeddedUi();
        RefreshBackendInfo();
        RefreshEngineUi();
        UpdateHotkeyUi(error: null);
        if (ServerStatus.Foreground is SolidColorBrush { Color: var c } && c == Colors.Gray) ServerStatus.Text = UiText.Get("server.hint");
    }

    private static void RelabelItems(ComboBox box, IEnumerable<string> labels)
    {
        int sel = box.SelectedIndex;
        int i = 0;
        foreach (var l in labels) box.Items[i++] = l;
        box.SelectedIndex = sel;
    }

    /// <summary>언어 목록 표시명 — 영어 UI에서는 영어 이름, 저장값은 항상 한국어 키.</summary>
    private static string LanguageName(string koreanName) =>
        UiText.Lang == "ko" ? koreanName : LanguageMaps.ToEnglish(koreanName);

    private async Task RefreshModelsAsync(bool silent)
    {
        try
        {
            var current = ModelBox.Text;
            // 연결 테스트·새로고침은 항상 서버에 최신 상태를 다시 물어본다
            var models = await _client.GetModelsAsync(ServerBox.Text, CancellationToken.None, bypassCache: true,
                apiKey: ApiKeyBox.Text);

            ModelBox.Items.Clear();
            ModelBox.Items.Add(AutoModel);
            foreach (var m in models) ModelBox.Items.Add(m);
            if (string.IsNullOrWhiteSpace(current)) ModelBox.SelectedItem = AutoModel;
            else if (ModelBox.Items.Contains(current)) ModelBox.SelectedItem = current;
            else ModelBox.Text = current; // 목록에 없는 직접 입력 모델명은 유지

            ServerStatus.Text = UiText.Format("server.ok", models.Count);
            ServerStatus.Foreground = Brushes.Green;
        }
        catch (Exception)
        {
            if (!silent)
            {
                ServerStatus.Text = UiText.Get("server.fail");
                ServerStatus.Foreground = Brushes.Red;
            }
        }
    }

    // ---- 번역 엔진 ----

    private ModelCatalog.ModelInfo SelectedModel => ModelCatalog.All[Math.Max(0, EmbeddedModelBox.SelectedIndex)];

    private static string FormatModelLabel(ModelCatalog.ModelInfo m) =>
        m.DisplayName
        + (m.Tag.Length > 0 ? $" ({UiText.Get("model.tag." + m.Tag)})" : "")
        + $" · {m.SizeText}"
        + (m.IsDownloaded ? $" · {UiText.Get("model.downloaded")}" : "");

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
        BackendInfo.Text = UiText.Get(EmbeddedEngine.IsSmartAppControlOn ? "engine.auto.ollama" : "engine.auto.llama");
    }

    /// <summary>활성 백엔드의 엔진 설치 상태에 맞춰 사전 다운로드 버튼·상태 텍스트를 갱신한다.</summary>
    private void RefreshEngineUi()
    {
        bool ollama = OllamaEngine.UseOllamaBackend;
        bool installed = ollama ? OllamaEngine.IsBinaryInstalled : EmbeddedEngine.IsBinaryInstalled;
        bool downloading = _engineDownloadCts != null;

        EngineDownloadButton.Content = UiText.Get(downloading ? "cancel" : "engine.binary.download");
        EngineDownloadButton.Visibility = installed && !downloading ? Visibility.Collapsed : Visibility.Visible;
        if (downloading) return; // 진행 중 텍스트는 진행 콜백이 갱신한다

        EngineStatus.Text = UiText.Get(installed ? "engine.installed" : ollama ? "engine.missing.ollama" : "engine.missing.llama");
    }

    private async void EngineDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_engineDownloadCts != null) // 진행 중이면 버튼은 [취소]로 동작
        {
            _engineDownloadCts.Cancel();
            return;
        }

        var cts = _engineDownloadCts = new CancellationTokenSource();
        EngineDownloadBar.Visibility = Visibility.Visible;
        EngineDownloadBar.Value = 0;
        RefreshEngineUi();
        string? message = null; // 취소·실패 안내 (완료 후 상태 갱신에 덮이지 않게 마지막에 표시)
        try
        {
            void OnProgress(long received, long total)
            {
                double pct = total > 0 ? received * 100.0 / total : 0;
                EngineDownloadBar.Value = pct;
                EngineStatus.Text = $"{received / 1048576.0:0} / {total / 1048576.0:0} MB ({pct:0}%)";
            }
            if (OllamaEngine.UseOllamaBackend)
                await OllamaEngine.DownloadBinaryAsync(OnProgress, cts.Token);
            else
                await EmbeddedEngine.DownloadBinaryAsync(OnProgress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            message = UiText.Get("download.cancelled");
        }
        catch (Exception ex)
        {
            message = UiText.Format("download.failed", ex.Message);
        }
        finally
        {
            _engineDownloadCts = null;
            cts.Dispose();
            EngineDownloadBar.Visibility = Visibility.Collapsed;
            RefreshEngineUi();
            if (message != null) EngineStatus.Text = message;
        }
    }

    /// <summary>선택 모델 기준으로 라이선스·버튼·상태 표시를 갱신한다.</summary>
    private void RefreshEmbeddedUi()
    {
        var m = SelectedModel;
        EmbeddedLicense.Text = UiText.Get(m.LicenseKey);

        bool downloading = _downloadCts != null;
        bool downloaded = m.IsDownloaded;
        DownloadButton.Content = UiText.Get(downloading ? "cancel" : "engine.download");
        DownloadButton.IsEnabled = downloading || !downloaded;
        DeleteModelButton.Visibility = !downloading && downloaded ? Visibility.Visible : Visibility.Collapsed;
        if (!downloading)
        {
            DownloadStatus.Text = downloaded ? UiText.Get("model.ready") : UiText.Format("model.required", m.SizeText);
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
            DownloadStatus.Text = UiText.Get("download.cancelled");
        }
        catch (Exception ex)
        {
            DownloadStatus.Text = UiText.Format("download.failed", ex.Message);
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
        if (MessageBox.Show(UiText.Format("delete.confirm", m.DisplayName, m.SizeText),
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
            DownloadStatus.Text = UiText.Format("delete.failed", ex.Message);
            return;
        }
        RefreshModelLabels();
        RefreshEmbeddedUi();
    }

    protected override void OnClosed(EventArgs e)
    {
        _downloadCts?.Cancel(); // 창이 닫히면 진행 중 다운로드 중단 (.part가 남아 이어받기 가능)
        _engineDownloadCts?.Cancel();
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
            UpdateHotkeyUi(UiText.Get("hotkey.invalid"));
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
            ? UiText.Get("hotkey.copy")
            : UiText.Format(doublePress ? "hotkey.double.desc" : "hotkey.single.desc", _gesture);
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
        s.ApiKey = ApiKeyBox.Text.Trim();
        string typedModel = ModelBox.Text.Trim();
        s.Model = typedModel.Length == 0 || typedModel == AutoModel ? "" : typedModel;
        s.TargetLanguage = LanguageMaps.TargetChoices[Math.Max(0, TargetBox.SelectedIndex)];
        s.KoreanSourceTarget = LanguageMaps.KoreanSourceChoices[Math.Max(0, KoreanSourceBox.SelectedIndex)];
        s.Glossary = GlossaryBox.Text;
        s.Theme = ThemeValues[Math.Max(0, ThemeBox.SelectedIndex)];
        s.HotkeyGesture = _gesture.ToString();
        s.HotkeyDoublePress = DoublePressCheck.IsChecked == true || _gesture.IsCopyGesture;
        s.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        s.RunAtStartup = StartupCheck.IsChecked == true;
        s.AutoUpdateCheck = UpdateCheck.IsChecked == true;
        s.PopupNearCursor = CursorCheck.IsChecked == true;
        s.CloseOnFocusLoss = FocusCheck.IsChecked == true;
        s.UiLanguage = UiLanguageValues[Math.Max(0, UiLanguageBox.SelectedIndex)];

        App.Instance.ApplySettings();

        // 미다운로드 모델로 저장하는 것은 허용하되 안내한다
        if (s.EngineMode == "Embedded" && !SelectedModel.IsDownloaded)
        {
            MessageBox.Show(UiText.Format("save.nomodel", SelectedModel.DisplayName),
                "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }
}
