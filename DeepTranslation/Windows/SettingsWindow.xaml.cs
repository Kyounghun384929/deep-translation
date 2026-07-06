using System.Windows;
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

    public SettingsWindow()
    {
        InitializeComponent();

        var s = App.Settings;
        ServerBox.Text = s.ServerUrl;

        TargetBox.ItemsSource = LanguageMaps.TargetChoices;
        TargetBox.SelectedItem = LanguageMaps.TargetChoices.Contains(s.TargetLanguage) ? s.TargetLanguage : "한국어";

        KoreanSourceBox.ItemsSource = LanguageMaps.KoreanSourceChoices;
        KoreanSourceBox.SelectedItem = LanguageMaps.KoreanSourceChoices.Contains(s.KoreanSourceTarget) ? s.KoreanSourceTarget : "영어";

        GlossaryBox.Text = s.Glossary;

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
        s.ServerUrl = LmStudioClient.NormalizeBaseUrl(ServerBox.Text);
        s.Model = ModelBox.SelectedItem as string == AutoModel ? "" : ModelBox.SelectedItem as string ?? "";
        s.TargetLanguage = TargetBox.SelectedItem as string ?? "한국어";
        s.KoreanSourceTarget = KoreanSourceBox.SelectedItem as string ?? "영어";
        s.Glossary = GlossaryBox.Text;
        s.HotkeyGesture = _gesture.ToString();
        s.HotkeyDoublePress = DoublePressCheck.IsChecked == true || _gesture.IsCopyGesture;
        s.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        s.RunAtStartup = StartupCheck.