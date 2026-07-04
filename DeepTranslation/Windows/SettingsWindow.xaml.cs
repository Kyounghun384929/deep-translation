using System.Windows;
using System.Windows.Media;
using DeepTranslation.Services;

namespace DeepTranslation.Windows;

public partial class SettingsWindow : Window
{
    private const string AutoModel = "(자동 — 로드된 모델 사용)";
    private readonly LmStudioClient _client = new();

    public SettingsWindow()
    {
        InitializeComponent();

        var s = App.Settings;
        ServerBox.Text = s.ServerUrl;

        TargetBox.ItemsSource = LanguageMaps.TargetChoices;
        TargetBox.SelectedItem = LanguageMaps.TargetChoices.Contains(s.TargetLanguage) ? s.TargetLanguage : "한국어";

        KoreanSourceBox.ItemsSource = LanguageMaps.KoreanSourceChoices;
        KoreanSourceBox.SelectedItem = LanguageMaps.KoreanSourceChoices.Contains(s.KoreanSourceTarget) ? s.KoreanSourceTarget : "영어";

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
            var models = await _client.GetModelsAsync(ServerBox.Text, CancellationToken.None);

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

    private async void Test_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshModelsAsync(silent: false);

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = App.Settings;
        s.ServerUrl = LmStudioClient.NormalizeBaseUrl(ServerBox.Text);
        s.Model = ModelBox.SelectedItem as string == AutoModel ? "" : ModelBox.SelectedItem as string ?? "";
        s.TargetLanguage = TargetBox.SelectedItem as string ?? "한국어";
        s.KoreanSourceTarget = KoreanSourceBox.SelectedItem as string ?? "영어";
        s.HotkeyEnabled = HotkeyCheck.IsChecked == true;
        s.RunAtStartup = StartupCheck.IsChecked == true;
        s.PopupNearCursor = CursorCheck.IsChecked == true;
        s.CloseOnFocusLoss = FocusCheck.IsChecked == true;

        App.Instance.ApplySettings();
        Close();
    }
}
