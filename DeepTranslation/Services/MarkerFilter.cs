using System.Text.RegularExpressions;

namespace DeepTranslation.Services;

/// <summary>
/// 스트리밍 출력 최선두의 언어 마커(@@언어@@)를 감지·제거하는 상태 유지 필터.
/// 모델은 첫 줄에 @@Korean@@ 같은 마커를 출력하도록 지시받는다. 이 클래스는
/// (ThinkFilter로 &lt;think&gt; 블록이 제거된) 누적 텍스트를 받아 마커를 떼어낸 본문만 돌려준다.
///
/// 처리해야 하는 엣지 케이스:
/// (1) 마커가 아직 덜 도착한 경우(@@Eng 까지만 옴): 마커 후보 프리픽스는 표시를 보류한다.
///     — 잘못 확정하면 화면에 "@@Eng"가 잠깐 보였다가 사라지는 깜빡임이 생긴다.
/// (2) 첫 부분에 마커 패턴이 성립하지 않으면(공백 제외 선두가 @@로 시작하지 않음): 전체를 본문으로 간주한다.
/// (3) 개행 없이 끝나는 짧은 출력: 스트림 종료 시점(Finish)에 최종 판정한다.
///     — 스트리밍 도중에는 @@Korean@@ 뒤에 개행이 올지(=마커) 더 이어질지(=본문 @@…) 알 수 없어 보류한다.
/// </summary>
public sealed class MarkerFilter
{
    // 선두 공백 후 @@언어@@ 그리고 (선택적) 개행까지. 언어명은 1~40자, @와 개행은 불허.
    private static readonly Regex Marker = new(@"^\s*@@([^@\n]{1,40})@@\s*\r?\n?", RegexOptions.Compiled);

    // 마커 후보 프리픽스: "\s*@@언어@@"의 임의 접두사를 허용한다. 아직 이 형태의 접두사이면 마커가
    // 될 여지가 있으므로 표시를 보류한다(케이스 1). 끝의 @?는 닫는 @@ 중 첫 @만 도착한 순간을 포함한다.
    // 이 형태를 벗어나면(선두가 @@가 아니거나 언어명이 40자 초과) 마커가 아니라고 보고 본문 확정(케이스 2).
    private static readonly Regex Prefix = new(@"^\s*(@(@([^@\n]{0,40}@?)?)?)?$", RegexOptions.Compiled);

    private bool _resolved;          // 마커 판정이 끝났는지
    private string _markerLang = ""; // 파싱된 마커 언어(영어명). 마커 없음/실패면 "".

    /// <summary>파싱된 마커 언어(영어명). 아직/영영 판정되지 않았으면 빈 문자열.</summary>
    public string MarkerLanguage => _markerLang;

    /// <summary>
    /// 지금까지 누적된 (think 제거 후) 전체 텍스트에서 마커를 제거한 표시용 본문을 반환한다.
    /// 마커가 아직 확정되지 않아 표시를 보류해야 하면 빈 문자열을 반환한다.
    /// </summary>
    public string Process(string accumulated)
    {
        if (_resolved) return StripResolved(accumulated);

        var m = Marker.Match(accumulated);
        if (m.Success)
        {
            // 마커가 완성됐다(닫는 @@까지 도착). 확정하고 이후로는 고정 길이로 잘라낸다.
            _markerLang = m.Groups[1].Value.Trim();
            _resolved = true;
            return accumulated[m.Length..].TrimStart();
        }

        // 아직 마커가 완성되지 않았다. 누적 텍스트가 여전히 "마커 후보 프리픽스"이면 표시 보류(케이스 1).
        if (Prefix.IsMatch(accumulated)) return "";

        // 마커 후보가 아니다 → 선두가 @@로 시작하지 않거나 언어명이 너무 길다. 전체를 본문으로 확정(케이스 2).
        _resolved = true;
        _markerLang = "";
        return accumulated.TrimStart();
    }

    /// <summary>
    /// 스트림 종료 시 최종 본문을 반환한다. 개행 없이 끝난 짧은 출력의 마커도 여기서 판정한다(케이스 3).
    /// </summary>
    public string Finish(string accumulated)
    {
        if (!_resolved)
        {
            var m = Marker.Match(accumulated);
            if (m.Success)
            {
                _markerLang = m.Groups[1].Value.Trim();
                _resolved = true;
                return accumulated[m.Length..].TrimStart();
            }
            _resolved = true;
            _markerLang = "";
        }
        return StripResolved(accumulated);
    }

    // 확정된 마커를 다시 제거한다. 확정 시 캡처한 언어를 신뢰하지 않고 매번 정규식으로 떼어
    // 스트리밍 중 재호출에도 일관되게 동작하도록 한다. 마커 없음(_markerLang == "")이면 그대로 둔다.
    private string StripResolved(string accumulated)
    {
        if (_markerLang.Length == 0) return accumulated.TrimStart();
        var m = Marker.Match(accumulated);
        return m.Success ? accumulated[m.Length..].TrimStart() : accumulated.TrimStart();
    }
}
