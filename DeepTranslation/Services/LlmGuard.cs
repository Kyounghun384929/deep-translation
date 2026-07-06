using System.Security.Cryptography;
using System.Text;

namespace DeepTranslation.Services;

/// <summary>
/// LLM 중복 호출을 막는 안전 장치.
/// 1) 전역 단일 실행 — 서버에 생성 요청이 동시에 두 개 이상 가지 않도록 직렬화한다.
///    새 요청은 이전 요청이 (취소 포함) 완전히 종료된 뒤에야 시작된다.
/// 2) 최소 호출 간격 — 연속 요청 사이에 짧은 냉각 시간을 둔다.
/// 3) 결과 캐시 — 같은 (서버, 모델, 대상 언어, 원문)은 재호출 없이 즉시 응답한다.
///    "다시 번역"처럼 재생성이 필요할 때는 bypassCache로 우회하고, 새 결과가 캐시를 덮어쓴다.
/// </summary>
public static class LlmGuard
{
    // Language: 마커에서 파싱한 실제 번역 언어(영어명). 마커 실패 시 "" — 캐시 히트 때도 대상 표시를 정확히 하기 위함.
    public readonly record struct CachedTranslation(string Text, string Model, string Language);

    private sealed record Entry(string Text, string Model, string Language, long Tick);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Entry> Cache = new();

    private const int CacheCapacity = 32;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
    private const int CooldownMs = 250;
    private static long _lastCallEndTick;

    // 직전 게이트 실행이 취소로 끝났는지. 게이트 안에서만 갱신되므로 경쟁이 없다.
    private static bool _lastCallCanceled;

    public static string MakeKey(string server, string model, string targetEnglish, string fallbackEnglish,
        string glossary, double temperature, string text)
    {
        char sep = (char)31; // 필드 경계 모호성 방지용 구분자 (unit separator)
        string raw = string.Join(sep, server, model, targetEnglish, fallbackEnglish, glossary,
            temperature.ToString("0.###"), text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public static bool TryGet(string key, out CachedTranslation value)
    {
        lock (Sync)
        {
            if (Cache.TryGetValue(key, out var entry))
            {
                if (Environment.TickCount64 - entry.Tick <= CacheTtl.TotalMilliseconds)
                {
                    value = new CachedTranslation(entry.Text, entry.Model, entry.Language);
                    return true;
                }
                Cache.Remove(key); // 만료
            }
        }
        value = default;
        return false;
    }

    public static void Store(string key, string text, string model, string language)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (Sync)
        {
            Cache[key] = new Entry(text, model, language, Environment.TickCount64);
            if (Cache.Count <= CacheCapacity) return;

            // 용량 초과 시 가장 오래된 항목 제거 (용량이 작아 선형 탐색으로 충분)
            string? oldestKey = null;
            long oldestTick = long.MaxValue;
            foreach (var (k, e) in Cache)
            {
                if (e.Tick < oldestTick) { oldestTick = e.Tick; oldestKey = k; }
            }
            if (oldestKey != null) Cache.Remove(oldestKey);
        }
    }

    /// <summary>
    /// LLM 생성 요청을 전역적으로 한 번에 하나만 실행한다.
    /// 이전 요청이 끝나기 전에 들어온 요청은 이전 요청이 종료(완료/취소)될 때까지 대기한다.
    /// </summary>
    public static async Task<T