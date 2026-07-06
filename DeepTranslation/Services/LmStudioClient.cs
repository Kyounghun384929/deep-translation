using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeepTranslation.Services;

public class LmStudioException : Exception
{
    public LmStudioException(string message) : base(message) { }
    public LmStudioException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>LM Studio의 OpenAI 호환 로컬 서버와 통신하는 클라이언트.</summary>
public sealed class LmStudioClient
{
    private static readonly HttpClient Http = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    // 모델 목록 캐시 (키: 정규화된 서버 URL). 자동 모드의 매 번역마다 게이트 안에서 재조회되는 비용을 줄인다.
    private static readonly object ModelsCacheSync = new();
    private static readonly Dictionary<string, (List<string> Models, long Tick)> ModelsCache = new();
    private static readonly TimeSpan ModelsCacheTtl = TimeSpan.FromSeconds(30);

    public static string NormalizeBaseUrl(string? url)
    {
        url = (url ?? "").Trim();
        if (url.Length == 0) url = "http://localhost:1234";
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = "http://" + url;
        return url.TrimEnd('/');
    }

    /// <summary>
    /// 사용 가능한 채팅 모델 목록. LM Studio 전용 엔드포인트(/api/v0/models)로 로드 상태를
    /// 확인해 이미 로드된 모델을 앞에 두고, 실패하면 표준 /v1/models로 대체한다.
    /// 30초 TTL 캐시로 자동 모드의 반복 조회를 줄인다. bypassCache=true면 캐시를 무시하고 새로 조회한다
    /// (연결 테스트·새로고침처럼 최신 상태가 필요할 때).
    /// </summary>
    public async Task<List<string>> GetModelsAsync(string baseUrl, CancellationToken ct, bool bypassCache = false)
    {
        string root = NormalizeBaseUrl(baseUrl);

        if (!bypassCache)
        {
            lock (ModelsCacheSync)
            {
                if (ModelsCache.TryGetValue(root, out var cached) &&
                    Environment.TickCount64 - cached.Tick <= ModelsCacheTtl.TotalMilliseconds)
                    return new List<string>(cached.Models); // 호출부가 목록을 변형(재정렬)하므로 사본 반환
            }
        }

        var result = await FetchModelsAsync(root, ct);

        lock (ModelsCacheSync)
        {
            ModelsCache[root] = (new List<string>(result), Environment.TickCount64);
        }
        return result;
    }

    private async Task<List<string>> FetchModelsAsync(string root, CancellationToken ct)
    {
        try
        {
            var list = await GetModelsV0Async(root, ct);
            if (list.Count > 0) return list;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            // 구버전 LM Studio에는 /api/v0/models가 없음 — 아래 표준 엔드포인트로 대체
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        string json;
        try
        {
            json = await Http.GetStringAsync($"{root}/v1/models", cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new LmStudioException(ConnectionHelp(root), ex);
        }

        var result = new List<string>();
        if (JsonNode.Parse(json)?["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(id) && !id.Contains("embed", StringComparison.OrdinalIgnoreCase))
                    result.Add(id);
            }
        }
        return result;
    }

    private static async Task<List<string>> GetModelsV0Async(string root, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        string json = await Http.GetStringAsync($"{root}/api/v0/models", cts.Token);

        var loaded = new List<string>();
        var others = new List<string>();
        if (JsonNode.Parse(json)?["data"] is JsonArray data)
        {
            foreach (var item in data)
            {
                var id = item?["id"]?.GetValue<string>();
                var type = item?["type"]?.GetValue<string>() ?? "";
                var state = item?["state"]?.GetValue<string>() ?? "";
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (type is not ("llm" or "vlm")) continue; // 임베딩 모델 제외
                if (state == "loaded") loaded.Add(id); else others.Add(id);
            }
        }
        loaded.AddRange(others);
        return loaded;
    }

    /// <summary>스트리밍 채팅 완성 요청. 토큰이 도착할 때마다 onDelta를 호출한다.</summary>
    public async Task StreamChatAsync(string baseUrl, string model, string systemPrompt, string userText,
        double temperature, Action<string> onDelta, CancellationToken ct)
    {
        string root = NormalizeBaseUrl(baseUrl);
        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = systemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = userText }),
            ["temperature"] = temperature,
            ["stream"] = true
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, $"{root}/v1/chat/completions")
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };

        HttpResponseMessage resp;
        try
        {
            resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LmStudioException(ConnectionHelp(root), ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                string body = "";
                try { body = await resp.Content.ReadAsStringAsync(ct); } catch { }
                throw new LmStudioException($"LM Studio 서버 오류 (HTTP {(int)resp.StatusCode})\n{ExtractErrorMessage(body)}");
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(ct);
                if (line == null) break;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;
                var data = line[5..].Trim();
                if (data == "[DONE]") break;
                try
                {
                    var node = JsonNode.Parse(data);
                    if (node?["error"] is { } err)
                    {
                        string msg = err is JsonValue v && v.TryGetValue<string>(out var s)
                            ? s : err["message"]?.GetValue<string>() ?? err.ToJsonString();
                