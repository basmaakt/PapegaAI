using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// Maps PapegaAI's platform-neutral key names onto the two numbering schemes
/// Linux uses: kernel evdev codes (KEY_*, from linux/input-event-codes.h) and
/// X11 keycodes, which are the same numbers plus 8.
/// </summary>
static class LinuxKeys
{
    /// <summary>evdev KEY_* code for a hotkey name, or null when unknown.</summary>
    public static int? Evdev(string name)
    {
        string n = HotkeyNames.Normalize(name);
        int? code = n switch
        {
            "right-ctrl" => 97,     // KEY_RIGHTCTRL
            "left-ctrl" => 29,      // KEY_LEFTCTRL
            "right-alt" => 100,     // KEY_RIGHTALT
            "right-shift" => 54,    // KEY_RIGHTSHIFT
            "left-shift" => 42,     // KEY_LEFTSHIFT
            "right-super" => 126,   // KEY_RIGHTMETA
            "caps-lock" => 58,      // KEY_CAPSLOCK
            "scroll-lock" => 70,    // KEY_SCROLLLOCK
            _ => null,
        };
        if (code is not null) return code;

        // KEY_F13 = 183 … KEY_F24 = 194
        return HotkeyNames.TryFunctionKey(n, out int fn) ? 183 + (fn - 13) : null;
    }

    /// <summary>X11 keycode: the same physical key, offset by 8 — an accident
    /// of history the X server has carried since the 1980s.</summary>
    public static int? X11(string name) => Evdev(name) is { } code ? code + 8 : null;

    /// <summary>Whether this key does something on its own when tapped. Unlike
    /// Windows, PapegaAI cannot swallow it on Linux: evdev and the X RECORD
    /// extension both watch passively, and blocking a key would mean grabbing
    /// the whole keyboard away from the rest of the desktop.
    /// </summary>
    public static bool TogglesSomething(string name) => HotkeyNames.ShouldSwallow(name);
}
