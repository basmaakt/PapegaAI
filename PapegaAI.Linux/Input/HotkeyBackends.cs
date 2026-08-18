using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// Chooses how to watch the push-to-talk key on this session and starts it.
///
/// On X11 the RECORD extension is tried first: it needs no permission at all,
/// so a fresh install just works. Everywhere else — and as the fallback when
/// RECORD is unavailable — the kernel's input layer is read directly, which
/// works under any compositor but wants the user in the "input" group.
/// </summary>
static class HotkeyBackends
{
    public const string Auto = "auto";

    /// <summary>Create and start a monitor, walking the candidates until one
    /// works. Throws with the last error when none does.</summary>
    /// <param name="preference">"auto", "x11" or "evdev" (config key
    /// "hotkey_backend").</param>
    public static IHotkeyMonitor Start(string hotkeyName, bool debug, string? preference = null)
    {
        var candidates = Candidates((preference ?? Auto).Trim().ToLowerInvariant(), hotkeyName, debug);

        Exception? last = null;
        foreach (var (name, create) in candidates)
        {
            IHotkeyMonitor? monitor = null;
            try
            {
                monitor = create();
                monitor.Start();
                return monitor;
            }
            catch (Exception ex)
            {
                monitor?.Dispose();
                last = ex;
                Console.Error.WriteLine($"hotkey via {name} lukte niet: {ex.Message.Split('\n')[0]}");
            }
        }

        throw last ?? new InvalidOperationException("geen bruikbare methode om de sneltoets te volgen");
    }

    static List<(string Name, Func<IHotkeyMonitor> Create)> Candidates(
        string preference, string hotkeyName, bool debug)
    {
        var x11 = ("X11 RECORD", (Func<IHotkeyMonitor>)(() => new X11HotkeyMonitor(hotkeyName, debug)));
        var evdev = ("evdev", (Func<IHotkeyMonitor>)(() => new EvdevHotkeyMonitor(hotkeyName, debug)));

        return preference switch
        {
            "x11" => [x11],
            "evdev" => [evdev],
            // X11 first when this really is an X session and the extension is
            // there; otherwise straight to evdev, which is the only thing that
            // can see keys under Wayland anyway.
            _ when X11HotkeyMonitor.IsAvailable() => [x11, evdev],
            _ => [evdev],
        };
    }

    /// <summary>What `doctor` reports before anything is started.</summary>
    public static string Describe(string? preference)
    {
        string want = (preference ?? Auto).Trim().ToLowerInvariant();
        if (want == "x11") return "X11 RECORD (vastgezet in config)";
        if (want == "evdev") return "evdev (vastgezet in config)";
        return X11HotkeyMonitor.IsAvailable()
            ? "X11 RECORD, met evdev als reserve"
            : "evdev";
    }
}
