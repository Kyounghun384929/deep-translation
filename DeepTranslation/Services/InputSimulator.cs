using System.Runtime.InteropServices;

namespace DeepTranslation.Services;

/// <summary>
/// 사용자 지정 단축키(예: Alt+Q)로 번역할 때, 선택 영역을 클립보드로 가져오기 위해
/// Ctrl+C 키 입력을 합성한다. 합성 입력은 키보드 훅에서 LLKHF_INJECTED로 걸러지므로
/// 단축키가 다시 발동하지 않는다.
/// </summary>
public static class InputSimulator
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12; // Alt
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_C = 0x43;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi; // 공용체 크기 확보용
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    /// <summary>현재 눌린 수정키를 잠시 해제한 뒤 Ctrl+C를 보낸다.</summary>
    public static void SendCopy()
    {
        var inputs = new List<INPUT>(8);

        // 사용자가 아직 누르고 있는 단축키의 수정키가 복사 입력을 오염시키지 않도록 해제
        foreach (ushort vk in new[] { VK_MENU, VK_SHIFT, VK_LWIN, VK_RWIN })
        {
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                inputs.Add(Key(vk, up: true));
        }

        inputs.Add(Key(VK_CONTROL, up: false));
        inputs.Add(Key(VK_C, up: false));
        inputs.Add(Key(VK_C, up: true));
        inputs.Add(Key(VK_CONTROL, up: true));

        SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
    }

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 }
        }
    };
}
