using System.Runtime.InteropServices;
using System.Windows;

namespace DeepTranslation.Services;

/// <summary>
/// 저수준 전역 키보드 훅으로 Ctrl+C가 짧은 간격으로 두 번 눌리는 것을 감지한다.
/// 키 입력을 가로채지 않고 그대로 통과시키므로 일반 복사 동작에는 영향이 없다.
/// </summary>
public sealed class KeyboardHookService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const long WM_KEYDOWN = 0x0100;
    private const long WM_KEYUP = 0x0101;
    private const long WM_SYSKEYDOWN = 0x0104;
    private const long WM_SYSKEYUP = 0x0105;
    private const uint VK_C = 0x43;
    private const int VK_CONTROL = 0x11;
    private const uint LLKHF_INJECTED = 0x10;

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
    private long _lastCtrlCTick;
    private bool _cReleasedSinceLastPress = true; // 키를 꾹 눌러 생기는 자동 반복 오작동 방지

    public event Action? CopyCopyPressed;

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

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            long msg = wParam.ToInt64();
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (info.vkCode == VK_C && (info.flags & LLKHF_INJECTED) == 0)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    bool ctrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
                    if (ctrlDown)
                    {
                        long now = Environment.TickCount64;
                        if (_cReleasedSinceLastPress && _lastCtrlCTick > 0 && now - _lastCtrlCTick <= DoublePressWindowMs)
                        {
                            _lastCtrlCTick = 0;
                            Application.Current?.Dispatcher.InvokeAsync(() => CopyCopyPressed?.Invoke());
                        }
                        else
                        {
                            _lastCtrlCTick = now;
                        }
                        _cReleasedSinceLastPress = false;
                    }
                }
                else if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
                {
                    _cReleasedSinceLastPress = true;
                }
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
