using System.Windows.Input;

namespace DeepTranslation.Models;

/// <summary>
/// 번역 단축키 조합. "Ctrl+Alt+Q" 형태의 문자열로 저장된다.
/// </summary>
public sealed record HotkeyGesture(bool Ctrl, bool Alt, bool Shift, bool Win, uint VkCode, string KeyName)
{
    private const uint VK_C = 0x43;

    /// <summary>일반 복사 단축키(Ctrl+C)와 동일한 조합인가 — 이 경우 두 번 누르기가 강제된다.</summary>
    public bool IsCopyGesture => Ctrl && !Alt && !Shift && !Win && VkCode == VK_C;

    public bool IsFunctionKey => VkCode is >= 0x70 and <= 0x87; // F1–F24

    /// <summary>
    /// 타이핑을 방해하지 않는 조합인가.
    /// 기능키는 단독 허용, 그 외 키는 Ctrl/Alt/Win 중 하나가 필요하다 (Shift 단독은 대문자 입력과 충돌).
    /// </summary>
    public bool IsValid => IsFunctionKey || Ctrl || Alt || Win;

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Ctrl) parts.Add("Ctrl");
        if (Alt) parts.Add("Alt");
        if (Shift) parts.Add("Shift");
        if (Win) parts.Add("Win");
        parts.Add(KeyName);
        return string.Join("+", parts);
    }

    public static HotkeyGesture Default { get; } = new(true, false, false, false, VK_C, "C");

    /// <summary>설정 창의 키 캡처에서 사용 — 수정키 자체나 매핑 불가 키는 null.</summary>
    public static HotkeyGesture? FromKey(ModifierKeys mods, Key key)
    {
        if (IsModifierKey(key)) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return null;
        return new HotkeyGesture(
            mods.HasFlag(ModifierKeys.Control),
            mods.HasFlag(ModifierKeys.Alt),
            mods.HasFlag(ModifierKeys.Shift),
            mods.HasFlag(ModifierKeys.Windows),
            (uint)vk, KeyDisplayName(key));
    }

    public static HotkeyGesture? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        bool ctrl = false, alt = false, shift = false, win = false;
        string keyName = "";
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL": ctrl = true; break;
                case "ALT": alt = true; break;
                case "SHIFT": shift = true; break;
                case "WIN" or "WINDOWS": win = true; break;
                default: keyName = part; break;
            }
        }
        if (keyName.Length == 0) return null;

        string enumName = keyName.Length == 1 && char.IsDigit(keyName[0]) ? "D" + keyName : keyName;
        if (!Enum.TryParse<Key>(enumName, ignoreCase: true, out var key) || IsModifierKey(key)) return null;
        int vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk <= 0) return null;
        return new HotkeyGesture(ctrl, alt, shift, win, (uint)vk, KeyDisplayName(key));
    }

    private static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or
        Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyDisplayName(Key key)
    {
        string n = key.ToString();
        return n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1]) ? n[1..] : n; // D1 → 1
    }
}
