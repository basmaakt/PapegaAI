namespace Parrot.Platform;

/// <summary>
/// What kind of desktop session this is. Nearly every Linux-specific choice in
/// PapegaAI — how to watch a key, how to type text, whether a window may place
/// itself — comes down to X11 versus Wayland.
/// </summary>
static class LinuxSession
{
    static string Env(string name) => Environment.GetEnvironmentVariable(name) ?? "";

    /// <summary>Wayland compositors hand out a socket name; the session type
    /// is a second, more explicit signal that some display managers set.</summary>
    public static bool IsWayland =>
        Env("WAYLAND_DISPLAY").Length > 0 ||
        Env("XDG_SESSION_TYPE").Equals("wayland", StringComparison.OrdinalIgnoreCase);

    /// <summary>A real X session. Deliberately false under Wayland: XWayland
    /// also sets DISPLAY, but an X client there only sees events aimed at X
    /// windows, which is useless for a global hotkey.</summary>
    public static bool IsX11 => !IsWayland && Env("DISPLAY").Length > 0;

    /// <summary>"GNOME", "KDE", "sway", … — used for the handful of places
    /// where a desktop's own limits matter (GNOME has no system tray of its
    /// own, and Mutter refuses the virtual-keyboard protocol).</summary>
    public static string Desktop => Env("XDG_CURRENT_DESKTOP");

    public static bool IsGnome =>
        Desktop.Contains("GNOME", StringComparison.OrdinalIgnoreCase);

    public static string Describe() => (IsWayland, IsX11) switch
    {
        (true, _) => $"Wayland ({(Desktop.Length > 0 ? Desktop : "onbekend")})",
        (_, true) => $"X11 ({(Desktop.Length > 0 ? Desktop : "onbekend")})",
        _ => "geen grafische sessie",
    };
}
