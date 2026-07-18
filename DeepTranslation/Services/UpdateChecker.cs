using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace DeepTranslation.Services;

/// <summary>GitHub Releases에서 새 버전을 확인한다.</summary>
public static class UpdateChecker
{
    public record UpdateInfo(Version Version, string InstallerUrl, long InstallerSize);

    private const string LatestReleaseUrl =
        "https://api.github.com/repos/Kyounghun384929/deep-translation/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    static UpdateChecker()
    {
        // GitHub API는 User-Agent 헤더가 없으면 403을 반환한다
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepTranslation");
        Http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    /// <summary>현재 실행 파일 버전 (Major.Minor.Build로 정규화).</summary>
    internal static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>릴리스 버전이 현재 실행 버전보다 높은지 비교한다 (Revision 자릿수 차이 무시).</summary>
    internal static bool IsNewer(Version latest) =>
        new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > CurrentVersion;

    /// <summary>
    /// 최신 릴리스를 확인해 새 버전이면 정보를 반환하고, 아니면 null.
    /// 404(저장소 비공개)·네트워크 오류·파싱 실패 시 자동 확인(manual=false)은 완전히 조용히 넘어가고,
    /// 수동 확인(manual=true)일 때만 사용자에게 결과를 알린다.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(bool manual, CancellationToken ct)
    {
        UpdateInfo? latest;
        try
        {
            string json = await Http.GetStringAsync(LatestReleaseUrl, ct);
            latest = ParseLatest(json);
        }
        catch
        {
            latest = null;
        }

        if (latest == null)
        {
            if (manual)
                MessageBox.Show("업데이트 정보를 확인할 수 없습니다 (저장소 비공개 또는 네트워크 오류).",
                    "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        if (!IsNewer(latest.Version))
        {
            if (manual)
                MessageBox.Show($"최신 버전입니다 (v{CurrentVersion}).",
                    "Deep Translation", MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }

        return latest;
    }

    /// <summary>릴리스 JSON에서 버전(tag_name)과 설치 프로그램 자산의 url·크기를 추출한다. 실패 시 null.</summary>
    internal static UpdateInfo? ParseLatest(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;

            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("DeepTranslation-Setup-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                long size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (url.Length > 0) return new UpdateInfo(version, url, size);
            }
        }
        catch
        {
            // 형식이 다른 응답은 업데이트 없음으로 취급
        }
        return null;
    }
}
