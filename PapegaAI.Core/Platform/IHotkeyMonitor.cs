namespace Parrot.Platform;

public enum HotkeyEventKind { Pressed, Released }

/// <summary>
/// Watches one key system-wide and raises press/release edges — the whole
/// user interface of the daemon. Windows uses a low-level keyboard hook,
/// Linux reads evdev or taps X11 with the RECORD extension.
/// </summary>
public interface IHotkeyMonitor : IDisposable
{
    event Action<HotkeyEventKind>? OnEvent;

    /// <summary>Begin watching. Throws when the platform refuses (no
    /// permission to read input devices, no X display, …) with a message
    /// aimed at the user, not the developer.</summary>
    void Start();

    void Stop();

    /// <summary>Short description of the mechanism in use ("evdev",
    /// "X11 RECORD", "WH_KEYBOARD_LL") — shown by `doctor` and the tray.</summary>
    string Mechanism { get; }
}
