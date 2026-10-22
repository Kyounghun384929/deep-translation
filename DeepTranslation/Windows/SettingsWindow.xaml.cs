using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeepTranslation.Models;
using DeepTranslation.Services;
using Microsoft.Win32;

namespace DeepTranslation.Windows;

public partial class SettingsWindow : Window
{
    private static string AutoModel => UiText.Get("server.model.auto");
    private readonly LmStudioClient _client = new();
    private HotkeyGesture _gesture = HotkeyGesture.Default;

    private static readonly int[] IdleMinuteValues = { 0, 3, 5, 10 };
    private static readonly int[] ContextValues = { 4096, 8192, 16384, 32768 };
    private static readonly string[] ThemeValues = { "System", "Light", "Dark" };
    private static readonly string[] UiLanguageValues = { "en", "ko" };
    private static readonly string[] UiLanguageChoices = { "English", "한국어" };

    private CancellationTokenSource? _downloadCts;
    private CancellationTokenSource? _engineDownloadCts; // 모델 다운로드와 독립 (동시 진행 가능)
    private bool _checkingEngine;
    private string? _pendingEngineTag; // 업데이트 확인으로 찾은 새 llama.cpp 태그 (설치 대기)
    private bool _suppressModelChanged;
    private bool _serverStatusIsHint = true; // 서버 상태줄이 기본 안내 문구인지 (언어 전환 시 다시 그림)
    private string _customPath;

    /// <summary>모델 카드 한 줄 (ModelCards 템플릿이 바인딩).</summary>
    public sealed record ModelRow(string Name, string Badge, string Detail, string State, bool Ready)
    {
        public Visibility BadgeVisibility => Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public SettingsWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);

        var s = App.Settings;
        UiText.Lang = s.UiLanguage;
        VersionText.Text = $"Deep Translation v{UpdateChecker.CurrentVersion}";
        ServerBox.Text = s.ServerUrl;
        ApiKeyBox.Text = s.ApiKey;
        _customPath = s.CustomModelPath;
        CustomPathBox.Text = _customPath;

        // 카탈로그 모델 뒤에 "내 GGUF 파일" 항목이 붙는다
        RefreshModelLabels();
        int modelIdx = s.EmbeddedModelId == ModelCatalog.CustomId ? ModelCatalog.All.Count : 0;
        for (int i = 0; i < ModelCatalog.All.Count; i++)
            if (ModelCatalog.All[i].Id == s.EmbeddedModelId) { modelIdx = i; break; }
        _suppressModelChanged = true;
        ModelList.SelectedIndex = modelIdx;
        _suppressModelChanged = false;

        foreach (var _ in IdleMinuteValues) IdleUnloadBox.Items.Add("");
        int idleIdx = Array.IndexOf(IdleMinuteValues, s.IdleUnloadMinutes);
        IdleUnloadBox.SelectedIndex = idleIdx >= 0 ? idleIdx : 2; // 목록에 없는 값이면 기본 5분

        foreach (var _ in ContextValues) ContextBox.Items.Add("");
        int ctxIdx = Array.IndexOf(ContextValues, s.ContextSize);
        ContextBox.SelectedIndex = ctxIdx >= 0 ? ctxIdx : 1; // 목록에 없는 값이면 기본 8K

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

        FillModelBox(Array.Empty<string>(), s.Model);

        _gesture = HotkeyGesture.TryParse(s.HotkeyGesture) ?? HotkeyGesture.Default;
        HotkeyBox.Text = _gesture.ToString();
        DoublePressCheck.IsChecked = s.HotkeyDoublePress || _gesture.IsCopyGesture;
        ApplyLanguage();

        HotkeyCheck.IsChecked = s.HotkeyEnabled;
        StartupCheck.IsChecked = s.RunAtStartup;
        UpdateCheck.IsChecked = s.AutoUpdateCheck;
        CursorCheck.IsChecked = s.PopupNearCursor;
        FocusCheck.IsChecked = s.CloseOnFocusLoss;
        NavList.SelectedIndex = 0;

        // 외부 서버 모드일 때만 서버에 모델 목록을 묻는다 (내장 모드에서 LM Studio를 깨우지 않게)
        Loaded += async (_, _) => { if (LmStudioRadio.IsChecked == true) await RefreshModelsAsync(silent: true); };
    }

    private void Nav_Changed(object sender, SelectionChangedEventArgs e)
    {
        var pages = new UIElement[] { EnginePage, TranslationPage, HotkeyPage, GeneralPage };
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == NavList.SelectedIndex ? Visibility.Visible : Visibility.Collapsed;
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
        RelabelItems(ContextBox, ContextValues.Select(v => $"{v / 1024}K" + (v == 8192 ? $" · {UiText.Get("ctx.default")}" : "")));
        RelabelItems(ThemeBox, ThemeValues.Select(v => UiText.Get("theme." + v.ToLowerInvariant())));
        RelabelItems(TargetBox, LanguageMaps.TargetChoices.Select(LanguageName));
        RelabelItems(KoreanSourceBox, LanguageMaps.KoreanSourceChoices.Select(LanguageName));

        // 외부 서버 모델 목록의 "(자동)" 항목은 항상 첫 번째
        int modelSel = ModelBox.SelectedIndex;
        ModelBox.Items[0] = AutoModel;
        ModelBox.SelectedIndex = modelSel;

        RefreshEmbeddedUi();
        RefreshEngineUi();
        UpdateHotkeyUi(error: null);
        if (_serverStatusIsHint) ServerStatus.Text = UiText.Get("server.hint");
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

    /// <summary>상태 문구 색을 팔레트 키로 지정한다 (테마가 바뀌어도 따라가도록 리소스 참조).</summary>
    private static void SetTone(TextBlock text, string brushKey) =>
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);

    // ---- 외부 서버 ----

    private string SelectedServerModel => ModelBox.SelectedIndex > 0 ? (string)ModelBox.SelectedItem : "";

    private void FillModelBox(IEnumerable<string> models, string current)
    {
        ModelBox.Items.Clear();
        ModelBox.Items.Add(AutoModel);
        foreach (var m in models) ModelBox.Items.Add(m);
        // 서버가 꺼져 있거나 목록에 없어도 저장된 모델은 유지한다
        if (current.Length > 0 && !ModelBox.Items.Contains(current)) ModelBox.Items.Add(current);
        ModelBox.SelectedItem = current.Length > 0 ? current : AutoModel;
    }

    private async Task RefreshModelsAsync(bool silent)
    {
        try
        {
            string current = SelectedServerModel;
            // 연결 테스트·새로고침은 항상 서버에 최신 상태를 다시 물어본다
            var models = await _client.GetModelsAsync(ServerBox.Text, CancellationToken.None, bypassCache: true,
                apiKey: ApiKeyBox.Text);
            FillModelBox(models, current);
            ServerStatus.Text = UiText.Format("server.ok", models.Count);
            SetTone(ServerStatus, "SuccessBrush");
            _serverStatusIsHint = false;
        }
        catch (Exception)
        {
            if (silent) return;
            ServerStatus.Text = UiText.Get("server.fail");
            SetTone(ServerStatus, "ErrorBrush");
            _serverStatusIsHint = false;
        }
    }

    private async void Preset_Click(object sender, RoutedEventArgs e)
    {
        ServerBox.Text = (string)((Button)sender).CommandParameter;
        await RefreshModelsAsync(silent: false);
    }

    /// <summary>프리셋 포트들을 동시에 확인해 응답한 첫 서버(프리셋 순서 우선)를 채운다.</summary>
    private async void Detect_Click(object sender, RoutedEventArgs e)
    {
        ServerStatus.Text = UiText.Get("server.detecting");
        SetTone(ServerStatus, "SubTextBrush");
        _serverStatusIsHint = false;

        var urls = PresetPanel.Children.OfType<Button>().Select(b => (string)b.CommandParameter).ToArray();
        var alive = await Task.WhenAll(urls.Select(async url =>
        {
            try { await _client.GetModelsAsync(url, CancellationToken.None, bypassCache: true); return true; }
            catch { return false; } // 꺼진 포트·다른 웹 서버(비 JSON 응답) 모두 미발견
        }));

        int found = Array.IndexOf(alive, true);
        if (found < 0)
        {
            ServerStatus.Text = UiText.Get("server.detect.none");
            SetTone(ServerStatus, "ErrorBrush");
            return;
        }
        ServerBox.Text = urls[found];
        await RefreshModelsAsync(silent: false);
    }

    private async void Test_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    // ---- 내장 엔진: 모델 ----

    private bool IsCustomSelected => ModelList.SelectedIndex == ModelCatalog.All.Count;

    /// <summary>선택한 모델. "내 GGUF 파일"인데 파일을 아직 고르지 않았으면 null.</summary>
    private ModelCatalog.ModelInfo? SelectedModel => IsCustomSelected
        ? ModelCatalog.Custom(_customPath)
        : ModelCatalog.All[Math.Max(0, ModelList.SelectedIndex)];

    private void EngineMode_Changed(object sender, RoutedEventArgs e)
    {
        bool embedded = EmbeddedRadio.IsChecked == true;
        EmbeddedPanel.Visibility = embedded ? Visibility.Visible : Visibility.Collapsed;
        LmStudioPanel.Visibility = embedded ? Visibility.Collapsed : Visibility.Visible;
        if (!embedded && IsLoaded) _ = RefreshModelsAsync(silent: true);
    }

    private void EmbeddedModel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressModelChanged) return;
        RefreshEmbeddedUi();
    }

    // 다운로드·삭제·파일 선택 후 카드의 "받음 ✓" 표시를 갱신한다
    private void RefreshModelLabels()
    {
        _suppressModelChanged = true;
        int sel = ModelList.SelectedIndex;
        var rows = ModelCatalog.All.Select(m => new ModelRow(
            m.DisplayName,
            m.Tag.Length > 0 ? UiText.Get("model.tag." + m.Tag) : "",
            $"{m.SizeText} · {UiText.Get(m.LicenseKey)}",
            m.IsDownloaded ? UiText.Get("model.downloaded") : "",
            m.IsDownloaded)).ToList();
        var custom = ModelCatalog.Custom(_customPath);
        rows.Add(new ModelRow(UiText.Get("custom.name"), "", custom?.DisplayName ?? UiText.Get("custom.detail"),
            custom is { IsDownloaded: true } ? custom.SizeText : "", custom is { IsDownloaded: true }));
        ModelList.ItemsSource = rows;
        ModelList.SelectedIndex = Math.Max(0, sel);
        _suppressModelChanged = false;
    }

    /// <summary>선택 모델 기준으로 동작 버튼·상태 표시를 갱신한다.</summary>
    private void RefreshEmbeddedUi()
    {
        bool custom = IsCustomSelected;
        CatalogActions.Visibility = custom ? Visibility.Collapsed : Visibility.Visible;
        CustomActions.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        var m = SelectedModel;
        if (custom)
        {
            DownloadStatus.Text = UiText.Get(m == null ? "custom.none" : m.IsDownloaded ? "custom.ready" : "custom.missing");
            return;
        }

        bool downloading = _downloadCts != null;
        bool downloaded = m!.IsDownloaded;
        DownloadButton.Content = UiText.Get(downloading ? "cancel" : "engine.download");
        DownloadButton.Visibility = downloading || !downloaded ? Visibility.Visible : Visibility.Collapsed;
        DeleteModelButton.Visibility = !downloading && downloaded ? Visibility.Visible : Visibility.Collapsed;
        if (!downloading)
            DownloadStatus.Text = downloaded ? UiText.Get("model.ready") : UiText.Format("model.required", m.SizeText);
    }

    private void BrowseCustom_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "GGUF (*.gguf)|*.gguf", Title = UiText.Get("custom.name") };
        string? dir = File.Exists(_customPath) ? Path.GetDirectoryName(_customPath) : LmStudioModelsDir();
        if (dir != null) dlg.InitialDirectory = dir;
        if (dlg.ShowDialog(this) != true) return;

        _customPath = dlg.FileName;
        CustomPathBox.Text = _customPath;
        RefreshModelLabels();
        RefreshEmbeddedUi();
    }

    // LM Studio가 모델을 받는 폴더 (신버전 ~/.lmstudio/models, 구버전 ~/.cache/lm-studio/models)
    private static string? LmStudioModelsDir()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(home, ".lmstudio", "models"), Path.Combine(home, ".cache", "lm-studio", "models") }
            .FirstOrDefault(Directory.Exists);
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadCts != null) // 진행 중이면 버튼은 [취소]로 동작
        {
            _downloadCts.Cancel();
            return;
        }

        var m = SelectedModel;
        if (m == null || m.IsDownloaded) return;

        var cts = _downloadCts = new CancellationTokenSource();
        ModelList.IsEnabled = false; // 받는 동안 모델 변경 방지
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
            ModelList.IsEnabled = true;
            DownloadBar.Visibility = Visibility.Collapsed;
            RefreshModelLabels();
            RefreshEmbeddedUi();
        }
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        // 사용자 GGUF는 앱 소유가 아니므로 삭제 버튼 자체가 숨겨져 있다
        if (SelectedModel is not { IsCustom: false } m) return;
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

    // ---- 내장 엔진: 엔진 바이너리·업데이트 ----

    /// <summary>
    /// 활성 백엔드의 설치·업데이트 상태에 맞춰 엔진 카드를 갱신한다.
    /// 버튼 하나가 상태에 따라 [엔진 다운로드] → [업데이트 확인] → [bXXXX 설치] → (진행 중) [취소]로 바뀐다.
    /// </summary>
    private void RefreshEngineUi()
    {
        bool ollama = OllamaEngine.UseOllamaBackend;
        bool installed = ollama ? OllamaEngine.IsBinaryInstalled : EmbeddedEngine.IsBinaryInstalled;
        bool downloading = _engineDownloadCts != null;
        bool busy = downloading || _checkingEngine;

        BackendInfo.Text = ollama
            ? UiText.Format("engine.auto.ollama", OllamaEngine.OllamaVersion)
            : UiText.Format("engine.auto.llama", EmbeddedEngine.Tag);
        // Ollama 경로는 컨텍스트가 모델 최초 등록 때 고정되므로 여기서 바꿀 수 없다
        ContextRow.Visibility = ollama ? Visibility.Collapsed : Visibility.Visible;
        RetryGpuButton.Visibility = !ollama && !busy && EmbeddedEngine.IsCpuFallback ? Visibility.Visible : Visibility.Collapsed;
        ResetVersionButton.Visibility = !ollama && !busy && App.Settings.LlamaVersion.Length > 0
            ? Visibility.Visible : Visibility.Collapsed;

        EngineDownloadButton.IsEnabled = !_checkingEngine;
        EngineDownloadButton.Content =
            downloading ? UiText.Get("cancel")
            : !installed ? UiText.Get("engine.binary.download")
            : _pendingEngineTag != null ? UiText.Format("engine.update.install", _pendingEngineTag)
            : UiText.Get("engine.update.check");
        // Ollama는 버전 고정 — 설치된 뒤에는 할 일이 없다
        EngineDownloadButton.Visibility = ollama && installed && !downloading ? Visibility.Collapsed : Visibility.Visible;
        if (busy) return; // 진행 중 텍스트는 진행 콜백이 갱신한다

        EngineStatus.Text =
            !installed ? UiText.Get(ollama ? "engine.missing.ollama" : "engine.missing.llama")
            : _pendingEngineTag != null ? UiText.Format("engine.update.available", _pendingEngineTag)
            : UiText.Get("engine.installed") + (!ollama && EmbeddedEngine.IsCpuFallback ? " · " + UiText.Get("engine.cpu") : "");
    }

    private async void EngineDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_engineDownloadCts != null) // 진행 중이면 버튼은 [취소]로 동작
        {
            _engineDownloadCts.Cancel();
            return;
        }

        bool ollama = OllamaEngine.UseOllamaBackend;
        bool installed = ollama ? OllamaEngine.IsBinaryInstalled : EmbeddedEngine.IsBinaryInstalled;
        if (installed && _pendingEngineTag == null)
        {
            await CheckEngineUpdateAsync();
            return;
        }

        string? newTag = installed ? _pendingEngineTag : null; // null = 현재 버전 설치
        var cts = _engineDownloadCts = new CancellationTokenSource();
        EngineDownloadBar.Visibility = Visibility.Visible;
        EngineDownloadBar.Value = 0;
        RefreshEngineUi();
        string? message = null; // 결과 안내 (완료 후 상태 갱신에 덮이지 않게 마지막에 표시)
        try
        {
            void OnProgress(long received, long total)
            {
                double pct = total > 0 ? received * 100.0 / total : 0;
                EngineDownloadBar.Value = pct;
                EngineStatus.Text = $"{received / 1048576.0:0} / {total / 1048576.0:0} MB ({pct:0}%)";
            }
            if (ollama)
                await OllamaEngine.DownloadBinaryAsync(OnProgress, cts.Token);
            else if (newTag != null)
            {
                await EmbeddedEngine.InstallVersionAsync(newTag, OnProgress, cts.Token);
                _pendingEngineTag = null;
                message = UiText.Format("engine.update.done", newTag);
            }
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

    private async Task CheckEngineUpdateAsync()
    {
        _checkingEngine = true;
        RefreshEngineUi();
        EngineStatus.Text = UiText.Get("engine.update.checking");
        string? message = null;
        try
        {
            _pendingEngineTag = await EmbeddedEngine.CheckUpdateAsync(CancellationToken.None);
            if (_pendingEngineTag == null) message = UiText.Format("engine.update.latest", EmbeddedEngine.Tag);
        }
        catch (Exception ex)
        {
            message = UiText.Format("engine.update.failed", ex.Message);
        }
        finally
        {
            _checkingEngine = false;
            RefreshEngineUi();
            if (message != null) EngineStatus.Text = message;
        }
    }

    private void RetryGpu_Click(object sender, RoutedEventArgs e)
    {
        EmbeddedEngine.RetryGpu();
        RefreshEngineUi();
        EngineStatus.Text = UiText.Get("engine.gpu.retried");
    }

    private void ResetVersion_Click(object sender, RoutedEventArgs e)
    {
        EmbeddedEngine.ResetToDefaultVersion();
        _pendingEngineTag = null;
        RefreshEngineUi();
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
            SetTone(HotkeyHint, "ErrorBrush");
            return;
        }

        SetTone(HotkeyHint, "SubTextBrush");
        bool doublePress = DoublePressCheck.IsChecked == true;
        HotkeyHint.Text = _gesture.IsCopyGesture
            ? UiText.Get("hotkey.copy")
            : UiText.Format(doublePress ? "hotkey.double.desc" : "hotkey.single.desc", _gesture);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.EngineMode = LmStudioRadio.IsChecked == true ? "LmStudio" : "Embedded";
        s.EmbeddedModelId = IsCustomSelected ? ModelCatalog.CustomId : SelectedModel!.Id;
        s.CustomModelPath = _customPath;
        s.ContextSize = ContextValues[Math.Max(0, ContextBox.SelectedIndex)];
        s.IdleUnloadMinutes = IdleMinuteValues[Math.Max(0, IdleUnloadBox.SelectedIndex)];
        s.ServerUrl = LmStudioClient.NormalizeBaseUrl(ServerBox.Text);
        s.ApiKey = ApiKeyBox.Text.Trim();
        s.Model = SelectedServerModel;
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

        // 준비되지 않은 모델로 저장하는 것은 허용하되 안내한다
        if (s.EngineMode == "Embedded" && SelectedModel is not { IsDownloaded: true })
        {
            MessageBox.Show(IsCustomSelected
                    ? UiText.Get("save.nocustom")
                    : UiText.Format("save.nomodel", SelectedModel!.DisplayName),
                "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        Close();
    }
}
