using System.Diagnostics;
using System.Runtime.InteropServices;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// A single push-to-talk key, identified by virtual-key code. The macOS
/// original uses the Fn modifier, which Windows keyboards handle in firmware —
/// it never reaches the OS — so the port defaults to right-ctrl instead.
/// </summary>
readonly struct Hotkey
{
    public int VirtualKey { get; }
    public string DisplayName { get; }
    /// <summary>Keys with a system side effect when tapped (caps-lock toggling
    /// case) are swallowed by the hook so holding them only talks to PapegaAI.</summary>
    public bool Swallow { get; }

    Hotkey(int vk, string name, bool swallow)
    {
        VirtualKey = vk;
        DisplayName = name;
        Swallow = swallow;
    }

    public static Hotkey? Parse(string name)
    {
        string n = HotkeyNames.Normalize(name);
        bool swallow = HotkeyNames.ShouldSwallow(n);
        int? vk = n switch
        {
            "right-ctrl" => 0xA3,
            "left-ctrl" => 0xA2,
            "right-alt" => 0xA5,
            "right-shift" => 0xA1,
            "left-shift" => 0xA0,
            "right-super" => 0x5C,   // VK_RWIN
            "caps-lock" => 0x14,
            "scroll-lock" => 0x91,
            _ => null,
        };
        if (vk is not null) return new Hotkey(vk.Value, n, swallow);

        // f13..f24 — VK_F13 = 0x7C
        if (HotkeyNames.TryFunctionKey(n, out int fn))
            return new Hotkey(0x7C + (fn - 13), n, false);

        return null;
    }
}

/// <summary>
/// Watches one key via a low-level keyboard hook (WH_KEYBOARD_LL) and emits
/// press/release edges. Must be started on a thread that pumps messages (the
/// WinForms UI thread); the callback fires on that same thread. Unlike the
/// macOS CGEventTap this needs no permission grant.
/// </summary>
sealed class HotkeyMonitor : IHotkeyMonitor
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;
    const uint LLKHF_INJECTED = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    delegate nint LowLevelKeyboardProc(int nCode, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    static extern nint GetModuleHandleW(string? lpModuleName);

    public event Action<HotkeyEventKind>? OnEvent;

    public string Mechanism => "WH_KEYBOARD_LL";

    readonly Hotkey hotkey;
    readonly bool debug;
    // Keep a reference so the GC doesn't collect the delegate while the hook lives.
    readonly LowLevelKeyboardProc proc;
    nint hook;
    bool isPressed;

    public HotkeyMonitor(Hotkey hotkey, bool debug = false)
    {
        this.hotkey = hotkey;
        this.debug = debug;
        proc = Callback;
    }

    public void Start()
    {
        if (hook != 0) return;
        using var module = Process.GetCurrentProcess().MainModule;
        hook = SetWindowsHookExW(WH_KEYBOARD_LL, proc, GetModuleHandleW(module?.ModuleName), 0);
        if (hook == 0)
            throw new InvalidOperationException(
                $"failed to install keyboard hook (win32 error {Marshal.GetLastWin32Error()})");
    }

    public void Stop()
    {
        if (hook != 0)
        {
            UnhookWindowsHookEx(hook);
            hook = 0;
        }
        isPressed = false;
    }

    nint Callback(int nCode, nint wParam, nint lParam)
    {
        if (nCode < 0)
            return CallNextHookEx(hook, nCode, wParam, lParam);

        var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
        int msg = (int)wParam;
        bool injected = (info.flags & LLKHF_INJECTED) != 0;

        if (debug)
            Console.Error.WriteLine($"  [debug] msg=0x{msg:X4} vk=0x{info.vkCode:X2} flags=0x{info.flags:X}{(injected ? " injected" : "")}");

        if (!injected && info.vkCode == (uint)hotkey.VirtualKey)
        {
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            if (down && !isPressed)
            {
                isPressed = true;
                OnEvent?.Invoke(HotkeyEventKind.Pressed);
            }
            else if (up && isPressed)
            {
                isPressed = false;
                OnEvent?.Invoke(HotkeyEventKind.Released);
            }
            // Auto-repeat key-downs while held fall through to here with
            // isPressed already true; ignored by the guards above.
            if (hotkey.Swallow && (down || up))
                return 1;
        }

        return CallNextHookEx(hook, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
