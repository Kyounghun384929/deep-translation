using System.Diagnostics;
using System.Text;
using DeepTranslation.Models;

namespace DeepTranslation.Services;

public sealed class TranslationService
{
    private readonly LmStudioClient _client = new();

    // 스트리밍 중 UI 갱신 최소 간격 — 델타마다 갱신하면 O(n²)이라 긴 글에서 UI 부하가 크다.
    private const int UiEmitIntervalMs = 66;

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
        // 폴백 언어: 대상이 한국어이면 설정된 한국어-원문 대상, 아니면 한국어.
        string fallbackEnglish = targetEnglish == "Korean"
            ? LanguageMaps.ToEnglish(settings.KoreanSourceTarget)
            : "Korean";
        string cacheKey = LlmGuard.MakeKey(settings.ServerUrl, settings.Model, targetEnglish,
            fallbackEnglish, settings.Glossary, settings.Temperature, text);

        if (!bypassCache && LlmGuard.TryGet(cacheKey, out var cached))
        {
            onText(cached.Text);
            return new Result(cached.Model, ResolveDisplay(cached.Language, targetDisplay), cached.Text, FromCache: true);
        }

        return await LlmGuard.RunExclusiveAsync(async () =>
        {
            // 게이트를 기다리는 동안 동일 요청이 먼저 완료되었을 수 있다 — 이중 확인
            if (!bypassCache && LlmGuard.TryGet(cacheKey, out var completed))
            {
                onText(completed.Text);
                return new Result(completed.Model, ResolveDisplay(completed.Language, targetDisplay), completed.Text, FromCache: true);
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
                if (!string.IsNullOrWhiteSpace(settings.LastWorkingModel) && models.Remove(settings.LastWorkingModel))
                    models.Insert(0, settings.LastWorkingModel);
                candidates = models.Take(3).ToList();
            }

            string systemPrompt = LanguageMaps.BuildSystemPrompt(targetEnglish, fallbackEnglish, settings.Glossary);
            LmStudioException? lastError = null;
            foreach (var model in candidates)
            {
                ct.ThrowIfCancellationRequested();
                var raw = new StringBuilder();
                var marker = new MarkerFilter();
                var sw = Stopwatch.StartNew();
                long lastEmit = -UiEmitIntervalMs; // 첫 델타는 즉시 반영되도록
                try
                {
                    await _client.StreamChatAsync(settings.ServerUrl, model, systemPrompt, text,
                        settings.Temperature,
                        delta =>
                        {
                            raw.Append(delta);
                            // UI 갱신 스로틀: 마지막 emit 후 일정 시간이 지났을 때만 표시용 본문을 계산·전달한다.
                            long now = sw.ElapsedMilliseconds;
                            if (now - lastEmit < UiEmitIntervalMs) return;
                            lastEmit = now;
                            string body = marker.Process(ThinkFilter.Strip(raw.ToString()));
                            if (body.Length > 0) onText(body);
                        }, ct