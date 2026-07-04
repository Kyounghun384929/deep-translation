using System.Runtime.InteropServices;
using System.Windows;

namespace DeepTranslation.Services;

/// <summary>
/// 저수준 전역 키보드 훅으로 설정된 단축키를 감지한다.
/// 기본은 Ctrl+C 두 번(빠르게)이며, 임의 조합(예: Alt+Q) 단일/이중 누름도 지원한다.
/// 키 입력을 가로채지 않고 그대로 통과시키므로 일반 복사 등 원래 동작에는 영향이 없다.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const long WM_KEYDOWN = 0x0100;
    private const long WM_KEYUP = 0x0101;
    private const long WM_SYSKEYDOWN = 0x0104;
    private const long WM_SYSKEYUP = 0x0105;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const uint LLKHF_INJECTED = 0x10;

    /// <summary>발동 후 자동 반복으로 연속 재발동되는 것을 막는 최소 간격.</summary>
    private const int RefireGuardMs = 600;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private LowLevelKeyboardProc? _proc; // GC로 수집되지 않도록 참조 유지
    private IntPtr _hookId = IntPtr.Zero;
    private long _lastPressTick;                 // 이중 누름 시퀀스의 첫 번째 누름 시각
    private long _lastFireTick;                  // 마지막 발동 시각
    private bool _releasedSinceLastPress = true; // 키 꾹 누름(자동 반복) 오작동 방지

    public event Action? HotkeyTriggered;

    // 감지할 단축키 (UI 스레드에서 설정, 훅 콜백에서 읽음 — 단순 값이라 동기화 불필요)
    public uint TriggerVkCode { get; set; } = 0x43; // C
    public bool NeedCtrl { get; set; } = true;
    public bool NeedAlt { get; set; }
    public bool NeedShift { get; set; }
    public bool NeedWin { get; set; }
    public bool RequireDoublePress { get; set; } = true;
    public int DoublePressWindowMs { get; set; } = 500;

    public bool IsRunning => _hookId != IntPtr.Zero;

    public void Start()
    {
        if (IsRunning) return;
        _proc = HookCallback;
        _hookId = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW(null), 0);
        if (_hookId == IntPtr.Zero)
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "키보드 훅 설치에 실패했습니다.");
    }

    public void Stop()
    {
        if (!IsRunning) return;
        UnhookWindowsHookEx(_hookId);
        _hookId = IntPtr.Zero;
        _proc = null;
    }

    private bool ModifiersMatch()
    {
        static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
        // 정확히 일치해야 발동 — Ctrl+C 설정 시 Ctrl+Shift+C(개발자 도구 등)에는 반응하지 않는다
        return Down(VK_CONTROL) == NeedCtrl
            && Down(VK_MENU) == NeedAlt
            && Down(VK_SHIFT) == NeedShift
            && (Down(VK_LWIN) || Down(VK_RWIN)) == NeedWin;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == TriggerVkCode && (info.flags & LLKHF_INJECTED) == 0)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    long now = Environment.TickCount64;
                    if (ModifiersMatch())
                    {
                        if (!RequireDoublePress)
                        {
                            if (_releasedSinceLastPress && now - _lastFireTick > RefireGuardMs)
                            {
                                _lastFireTick = now;
                                Fire();
                            }
                        }
                        else if (_releasedSinceLastPress && _lastPressTick > 0 && now - _lastPressTick <= DoublePressWindowMs)
                        {
                            _lastPressTick = 0;
                            _lastFireTick = now;
                            Fire();
                        }
                        else
                        {
                            _lastPressTick = now;
                        }
                    }
                    else
                    {
                        _lastPressTick = 0; // 수정키가 다르면 시퀀스 리셋 (예: 그냥 문자 타이핑)
                    }
                    _releasedSinceLastPress = false;
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _releasedSinceLastPress = true;
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void Fire() =>
        Application.Current?.Dispatcher.InvokeAsync(() => HotkeyTriggered?.Invoke());

    public void Dispose() => Stop();
}
