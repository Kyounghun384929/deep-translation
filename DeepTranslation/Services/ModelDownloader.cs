using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace DeepTranslation.Services;

/// <summary>
/// 대용량 파일 다운로더 (GGUF 모델, llama.cpp zip 공용).
/// ".part" 임시 파일에 받고 완료 후 최종 이름으로 바꾼다. 중단된 .part는 Range 요청으로 이어받는다.
/// </summary>
public static class ModelDownloader
{
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static ModelDownloader()
    {
        // GitHub 릴리스 자산 다운로드는 User-Agent가 없으면 거부될 수 있다 (HF에는 무해)
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("DeepTranslation");
    }

    // 진행 콜백 호출 간격 (바이트) — 너무 잦은 UI 갱신을 막는다
    private const long ProgressStepBytes = 1024 * 1024;

    /// <summary>
    /// url을 destPath로 내려받는다. expectedSize가 양수이면 완료 후 크기를 검증하고
    /// 불일치 시 .part를 지우고 실패 처리한다. onProgress에는 (받은 바이트, 전체 바이트)가 전달된다.
    /// 취소 시 .part는 남겨 두어 다음에 이어받을 수 있게 한다.
    /// </summary>
    public static async Task DownloadAsync(string url, string destPath, long expectedSize,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        string partPath = destPath + ".part";
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        long existing = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (expectedSize > 0 && existing >= expectedSize)
        {
            // 기대 크기 이상인 .part는 손상으로 보고 처음부터 다시 받는다
            File.Delete(partPath);
            existing = 0;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (existing > 0) req.Headers.Range = new RangeHeaderValue(existing, null);

        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // 서버 기준으로 유효하지 않은 .part — 지우고 처음부터 다시
            File.Delete(partPath);
            await DownloadAsync(url, destPath, expectedSize, onProgress, ct);
            return;
        }
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"다운로드 실패 (HTTP {(int)resp.StatusCode}): {url}");

        // Range를 요청했는데 206이 아니면 서버가 전체를 다시 보내는 것 — 처음부터 덮어쓴다
        bool resume = existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent;
        long received = resume ? existing : 0;
        long total = expectedSize > 0 ? expectedSize : received + (resp.Content.Headers.ContentLength ?? 0);

        await using (var fs = new FileStream(partPath, resume ? FileMode.Append : FileMode.Create, FileAccess.Write))
        await using (var body = await resp.Content.ReadAsStreamAsync(ct))
        {
            var buffer = new byte[81920];
            long lastReport = received;
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;
                if (onProgress != null && received - lastReport >= ProgressStepBytes)
                {
                    lastReport = received;
                    onProgress(received, total);
                }
            }
        }

        long finalSize = new FileInfo(partPath).Length;
        if (expectedSize > 0 && finalSize != expectedSize)
        {
            File.Delete(partPath);
            throw new IOException($"다운로드된 파일 크기가 예상과 다릅니다 ({finalSize:N0} / {expectedSize:N0} 바이트).");
        }
        File.Move(partPath, destPath, overwrite: true);
        onProgress?.Invoke(finalSize, total);
    }
}
