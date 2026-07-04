using System.Text.RegularExpressions;

namespace DeepTranslation.Services;

/// <summary>추론(reasoning) 모델이 출력하는 &lt;think&gt; 블록을 화면에서 숨긴다.</summary>
public static class ThinkFilter
{
    private static readonly Regex Closed = new(@"<think>[\s\S]*?</think>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Open = new(@"<think>[\s\S]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Strip(string text)
    {
        if (!text.Contains("<think", StringComparison.OrdinalIgnoreCase))
            return text.TrimStart();
        text = Closed.Replace(text, "");
        text = Open.Replace(text, "");
        return text.TrimStart();
    }
}
