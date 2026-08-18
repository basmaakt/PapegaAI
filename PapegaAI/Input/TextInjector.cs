using System.Runtime.InteropServices;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// Types a string at the current cursor location by synthesizing keyboard
/// events with SendInput + KEYEVENTF_UNICODE. Works in nearly every text
/// field on Windows without any permission grant (the macOS original needs
/// Accessibility for the same trick). Elevated (admin) windows won't accept
/// input from a non-elevated PapegaAI — that's UIPI, by design.
/// </summary>
sealed class TextInjector : ITextInjector
{
    public string Mechanism => "SendInput";

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    struct InputUnion
    {
        // The union also holds MOUSEINPUT (28 bytes on x64), which is larger
        // than KEYBDINPUT — pad to the full union size so INPUT has the exact
        // native layout SendInput expects.
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var inputs = new List<INPUT>(text.Length * 2);
        foreach (char c in text)
        {
            inputs.Add(MakeUnicode(c, keyUp: false));
            inputs.Add(MakeUnicode(c, keyUp: true));
        }

        // Send in modest batches; a single huge SendInput call can be dropped
        // wholesale by some applications.
        const int batch = 64;
        var array = inputs.ToArray();
        int size = Marshal.SizeOf<INPUT>();
        for (int i = 0; i < array.Length; i += batch)
        {
            int count = Math.Min(batch, array.Length - i);
            var slice = new INPUT[count];
            Array.Copy(array, i, slice, 0, count);
            SendInput((uint)count, slice, size);
        }
    }

    static INPUT MakeUnicode(char c, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion
        {
            ki = new KEYBDINPUT
            {
                wVk = 0,
                wScan = c,
                dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0),
            }
        }
    };
}
