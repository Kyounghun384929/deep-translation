using System.Text;
using DeepTranslation.Models;

namespace DeepTranslation.Services;

public sealed class TranslationService
{
    private readonly LmStudioClient _client = new();

    /// <summary>자동 모드에서 마지막으로 성공한 모델 — 다음 번역 때 우선 시도한다.</summary>
    private static string? _lastWorkingModel;

    public sealed record Result(string Model, string TargetDisplay, string Text, bool FromCache = false);

    /// <summary>
    /// 스트리밍 번역. onText에는 지금까지 누적된 표시용 번역 전체가 전달된다.
    /// LlmGuard를 통해 실행되어 동시 호출이 직렬화되고, 동일 요청은 캐시로 즉시 응답한다.
    /// 모델이 지정되지 않았으면 서버의 모델을 순서대로 시도한다(로드 실패 대비).
    /// </summary>
    public async Task<Result> TranslateAsync(AppSettings settings, string text, Action<string> onText,
        CancellationToken ct, bool bypassCache = false)
    {
        var (targetDisplay, targetEnglish) = LanguageMaps.ResolveTarget(settings, text);
        string cacheKey = LlmGuard.MakeKey(settings.ServerUrl, settings.Model, targetEnglish, settings.Temperature, text);

        if (!bypassCache && LlmGuard.TryGet(cacheKey, out var cached))
        {
            onText(cached.Text);
            return new Result(cached.Model, targetDisplay, cached.Text, FromCache: true);
        }

        return await LlmGuard.RunExclusiveAsync(async () =>
        {
            // 게이트를 기다리는 동안 동일 요청이 먼저 완료되었을 수 있다 — 이중 확인
            if (!bypassCache && LlmGuard.TryGet(cacheKey, out var completed))
            {
                onText(completed.Text);
                return new Result(completed.Model, targetDisplay, completed.Text, FromCache: true);
            }

            List<string> candidates;
            if (!string.IsNullOrWhiteSpace(settings.Model))
            {
                candidates = new List<string> { settings.Model };
            }
            else
            {
                var models = await _client.GetModelsAsync(settings.ServerUrl, ct);
                if (models.Count == 0)
                    throw new LmStudioException(
                        "LM Studio에 사용 가능한 채팅 모델이 없습니다.\nLM Studio에서 모델을 로드한 뒤 다시 시도하세요.");
                if (_lastWorkingModel is { } last && models.Remove(last))
                    models.Insert(0, last);
                candidates = models.Take(3).ToList();
            }

            string systemPrompt = LanguageMaps.BuildSystemPrompt(targetEnglish);
            LmStudioException? lastError = null;
            foreach (var model in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var raw = new StringBuilder();
                try
                {
                    await _client.StreamChatAsync(settings.ServerUrl, model, systemPrompt, text,
                        settings.Temperature,
                        delta =>
                        {
                            raw.Append(delta);
                            onText(ThinkFilter.Strip(raw.ToString()));
                        }, ct);

                    if (raw.Length == 0)
                    {
                        // 응답 본문 없이 스트림 종료 — 대개 모델 로드 실패
                        lastError = new LmStudioException(
                            $"모델 '{model}'이(가) 응답을 생성하지 못했습니다.\n" +
                            "모델이 메모리에 로드되지 못했을 수 있습니다. LM Studio에서 직접 로드해 보세요.");
                        continue;
                    }

                    _lastWorkingModel = model;
                    string final = ThinkFilter.Strip(raw.ToString()).Trim();
                    onText(final);
                    LlmGuard.Store(cacheKey, final, model);
                    return new Result(model, targetDisplay, final);
                }
                catch (LmStudioException ex)
                {
                    lastError = ex; // 다음 후보 모델로 재시도
                }
            }

            throw lastError ?? new LmStudioException("번역에 실패했습니다.");
        }, ct);
    }
}
