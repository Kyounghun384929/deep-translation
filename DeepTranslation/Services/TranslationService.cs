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
        CancellationToken ct, bool bypassCache = false, Action<string>? onStatus = null)
    {
        bool embedded = settings.EngineMode == "Embedded";
        // 내장 모드에서 실제 사용할 백엔드(Ollama/llama.cpp) — 결과가 섞이지 않게 캐시·기동에 함께 쓴다
        bool useOllama = embedded && OllamaEngine.UseOllamaBackend;
        var (targetDisplay, targetEnglish) = LanguageMaps.ResolveTarget(settings, text);
        // 폴백 언어: 대상이 한국어이면 설정된 한국어-원문 대상, 아니면 한국어.
        string fallbackEnglish = targetEnglish == "Korean"
            ? LanguageMaps.ToEnglish(settings.KoreanSourceTarget)
            : "Korean";
        // 내장 모드는 서버 URL·LM Studio 모델명이 무관하므로 엔진 식별자로 캐시 키를 만든다
        // (백엔드도 반영 — 같은 GGUF라도 llama.cpp와 Ollama 결과를 분리한다)
        string cacheKey = LlmGuard.MakeKey(
            embedded ? (useOllama ? "embedded-ollama" : "embedded-llamacpp") : settings.ServerUrl,
            embedded ? settings.EmbeddedModelId : settings.Model,
            targetEnglish, fallbackEnglish, settings.Glossary, settings.Temperature, text);

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

            string serverUrl = settings.ServerUrl;
            // 내장 엔진은 인증이 없으므로 사용자 API 키를 로컬 엔진에 보내지 않는다
            string? apiKey = embedded ? null : settings.ApiKey;
            List<string> candidates;
            if (embedded)
            {
                // 내장 엔진: 필요 시 서버를 기동하고 후보 모델 하나만 시도한다
                if (useOllama)
                {
                    // Ollama는 "dt-"를 붙인 모델명으로 기존 GGUF를 재활용해 등록·기동한다
                    serverUrl = await OllamaEngine.EnsureRunningAsync(settings, onStatus, ct);
                    candidates = new List<string> { "dt-" + settings.EmbeddedModelId };
                }
                else
                {
                    serverUrl = await EmbeddedEngine.EnsureRunningAsync(settings, onStatus, ct);
                    candidates = new List<string> { settings.EmbeddedModelId };
                }
                onStatus?.Invoke("번역 중…"); // 로딩 상태 표시를 되돌린다
            }
            else if (!string.IsNullOrWhiteSpace(settings.Model))
            {
                candidates = new List<string> { settings.Model };
            }
            else
            {
                var models = await _client.GetModelsAsync(settings.ServerUrl, ct, apiKey: settings.ApiKey);
                if (models.Count == 0)
                    throw new LmStudioException(
                        "서버에 사용 가능한 채팅 모델이 없습니다.\n서버에서 모델을 로드한 뒤 다시 시도하세요.");
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
                    await _client.StreamChatAsync(serverUrl, model, systemPrompt, text,
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
                        }, ct, apiKey);

                    if (raw.Length == 0)
                    {
                        // 응답 본문 없이 스트림 종료 — 대개 모델 로드 실패
                        lastError = new LmStudioException(
                            $"모델 '{model}'이(가) 응답을 생성하지 못했습니다.\n" +
                            (embedded
                                ? "잠시 후 다시 시도해 보세요."
                                : "모델이 메모리에 로드되지 못했을 수 있습니다. 서버에서 모델을 직접 로드해 보세요."));
                        continue;
                    }

                    // 성공 모델을 설정에 영속화 (LM Studio 모드에서만, 값이 바뀔 때만 저장)
                    if (!embedded && settings.LastWorkingModel != model)
                    {
                        settings.LastWorkingModel = model;
                        settings.Save();
                    }

                    string final = marker.Finish(ThinkFilter.Strip(raw.ToString())).Trim();
                    onText(final);
                    string markerLang = marker.MarkerLanguage; // 파싱된 실제 번역 언어(영어명), 실패 시 ""
                    LlmGuard.Store(cacheKey, final, model, markerLang);
                    // 사용된 엔진의 유휴 언로드 타이머를 리셋한다
                    if (embedded)
                    {
                        if (useOllama) OllamaEngine.NotifyActivity();
                        else EmbeddedEngine.NotifyActivity();
                    }
                    return new Result(model, ResolveDisplay(markerLang, targetDisplay), final);
                }
                catch (LmStudioException ex)
                {
                    lastError = ex; // 다음 후보 모델로 재시도
                }
            }

            throw lastError ?? new LmStudioException("번역에 실패했습니다.");
        }, ct);
    }

    // 마커에서 파싱된 언어(영어명)를 표시명으로 변환한다. 미등록/파싱 실패면 기존 요청 대상 표시명을 유지한다.
    private static string ResolveDisplay(string markerLanguage, string requestedDisplay)
    {
        if (string.IsNullOrEmpty(markerLanguage)) return requestedDisplay;
        return LanguageMaps.ToDisplay(markerLanguage) ?? requestedDisplay;
    }
}
