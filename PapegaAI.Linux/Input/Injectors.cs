using System.Diagnostics;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>One way of getting text into the focused window.</summary>
interface IInjectionMethod
{
    string Name { get; }

    /// <summary>Whether this method can be used on this machine at all —
    /// checked once, at startup, so `doctor` can explain what is missing.</summary>
    bool IsUsable { get; }

    /// <summary>Type the text. False means "did not work, try the next one";
    /// exceptions are not expected and are caught by the chain regardless.</summary>
    bool TryInject(string text);
}

/// <summary>
/// Typing text on Linux has no single answer the way SendInput is on Windows,
/// so PapegaAI keeps an ordered chain and uses the first method that works —
/// re-trying down the chain at runtime, because a helper can be installed but
/// broken (ydotool without its daemon is the classic).
///
/// X11 sessions get xdotool, which types real unicode. Wayland forbids that
/// outright, so there it is wtype (wlroots, KDE) or a kernel-level virtual
/// keyboard pressing Ctrl+V over the clipboard (GNOME, and everything else).
/// </summary>
sealed class LinuxTextInjector : ITextInjector, IDisposable
{
    readonly List<IInjectionMethod> chain = new();
    string active;

    public LinuxTextInjector(string? preference, string? pasteShortcut)
    {
        bool shift = (pasteShortcut ?? "ctrl+v").Replace(" ", "")
            .Contains("shift", StringComparison.OrdinalIgnoreCase);

        var all = new List<IInjectionMethod>
        {
            new XdotoolInjector(),
            new WtypeInjector(),
            new YdotoolInjector(),
            new UinputPasteInjector(shift),
            new ClipboardOnlyInjector(),
        };

        string want = (preference ?? "auto").Trim().ToLowerInvariant();
        if (want is not "auto" and not "")
        {
            var forced = all.FirstOrDefault(m => m.Name == want);
            if (forced is null)
                Console.Error.WriteLine(
                    $"onbekende injection-instelling '{want}' — terug naar auto " +
                    $"(keuzes: {string.Join(", ", all.Select(m => m.Name))}, auto)");
            else if (!forced.IsUsable)
                Console.Error.WriteLine($"injection '{want}' is hier niet bruikbaar — terug naar auto");
            else
                chain.Add(forced);
        }

        if (chain.Count == 0)
        {
            // Session-appropriate order: the native typing tool first, the
            // clipboard-and-paste routes after it, plain clipboard last.
            IEnumerable<IInjectionMethod> ordered = LinuxSession.IsWayland
                ? all.Where(m => m.Name != "xdotool")
                : all.Where(m => m.Name != "wtype");
            chain.AddRange(ordered.Where(m => m.IsUsable));
        }

        active = chain.FirstOrDefault()?.Name ?? "geen";
    }

    public string Mechanism => chain.Count switch
    {
        0 => "geen — geen enkele injectiemethode beschikbaar",
        1 => active,
        _ => $"{active} (reserve: {string.Join(" → ", chain.Skip(1).Select(m => m.Name))})",
    };

    public bool HasAnyMethod => chain.Count > 0;

    public void Inject(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        foreach (var method in chain)
        {
            try
            {
                if (!method.TryInject(text)) continue;
                if (method.Name != active)
                {
                    active = method.Name;
                    Console.Error.WriteLine($"  (injectie via {active})");
                }
                return;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  injectie via {method.Name} mislukte: {ex.Message}");
            }
        }

        Console.Error.WriteLine("geen enkele injectiemethode werkte — tekst staat in de geschiedenis");
    }

    public void Dispose()
    {
        foreach (var m in chain.OfType<IDisposable>()) m.Dispose();
    }
}

/// <summary>Runs a helper and reports whether it succeeded.</summary>
static class Helper
{
    public static bool Run(string tool, IEnumerable<string> args, int timeoutMs = 15_000)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi);
        if (p is null) return false;
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        if (p.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
            Console.Error.WriteLine($"  [{tool}] {stderr.Trim()}");
        return p.ExitCode == 0;
    }
}

/// <summary>X11's answer: xdotool synthesises real unicode key events through
/// XTEST, so any character types correctly whatever the keyboard layout.</summary>
sealed class XdotoolInjector : IInjectionMethod
{
    public string Name => "xdotool";

    public bool IsUsable => LinuxSession.IsX11 && Which.Exists("xdotool");

    public bool TryInject(string text) =>
        // --clearmodifiers so a still-held push-to-talk key cannot turn the
        // transcript into shortcuts; a small delay keeps Electron apps happy.
        Helper.Run("xdotool", ["type", "--clearmodifiers", "--delay", "2", "--", text]);
}

/// <summary>Wayland's equivalent, using the virtual-keyboard protocol.
/// Implemented by wlroots compositors (Sway, Hyprland) and KWin; GNOME's
/// Mutter does not offer it, which is why the chain continues past this.</summary>
sealed class WtypeInjector : IInjectionMethod
{
    public string Name => "wtype";

    public bool IsUsable => LinuxSession.IsWayland && Which.Exists("wtype");

    public bool TryInject(string text) => Helper.Run("wtype", ["--", text]);
}

/// <summary>ydotool types through the kernel like our own uinput path, but via
/// its own background daemon. Common on Wayland systems, and already set up on
/// many; useless without ydotoold running, hence the runtime fallback.</summary>
sealed class YdotoolInjector : IInjectionMethod
{
    public string Name => "ydotool";

    public bool IsUsable => Which.Exists("ydotool");

    public bool TryInject(string text) => Helper.Run("ydotool", ["type", "--", text]);
}

/// <summary>
/// The universal route: put the transcript on the clipboard, then press Ctrl+V
/// on a kernel-level virtual keyboard. Works on every compositor including
/// GNOME under Wayland. The previous clipboard contents are put back
/// afterwards, so dictating does not quietly eat what you had copied.
/// </summary>
sealed class UinputPasteInjector : IInjectionMethod, IDisposable
{
    readonly bool shift;
    UinputDevice? device;
    bool broken;

    /// <param name="shift">Use Ctrl+Shift+V instead of Ctrl+V — what
    /// terminals want, since Ctrl+V means something else there.</param>
    public UinputPasteInjector(bool shift) => this.shift = shift;

    public string Name => "uinput";

    public bool IsUsable => !broken && UinputDevice.IsAvailable() && Clipboard.IsAvailable;

    public bool TryInject(string text)
    {
        if (broken) return false;
        string? previous = Clipboard.Read();
        if (!Clipboard.Write(text)) return false;

        try
        {
            device ??= new UinputDevice([UinputDevice.KEY_LEFTCTRL, UinputDevice.KEY_LEFTSHIFT, UinputDevice.KEY_V]);
        }
        catch (Exception ex)
        {
            broken = true;
            Console.Error.WriteLine($"  {ex.Message}");
            return false;
        }

        device.Tap(UinputDevice.KEY_V, shift
            ? [UinputDevice.KEY_LEFTCTRL, UinputDevice.KEY_LEFTSHIFT]
            : [UinputDevice.KEY_LEFTCTRL]);

        // Give the target application a moment to actually read the selection
        // before handing the clipboard back to whatever owned it.
        if (previous is not null && previous != text)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(600);
                Clipboard.Write(previous);
            });
        }
        return true;
    }

    public void Dispose() => device?.Dispose();
}

/// <summary>Last resort: leave the transcript on the clipboard and say so.
/// Not typing for you, but never losing what you dictated either.</summary>
sealed class ClipboardOnlyInjector : IInjectionMethod
{
    public string Name => "clipboard";

    public bool IsUsable => Clipboard.IsAvailable;

    public bool TryInject(string text)
    {
        if (!Clipboard.Write(text)) return false;
        Notify.Send("PapegaAI", "Transcript staat op het klembord — plak met Ctrl+V.");
        return true;
    }
}

/// <summary>Desktop notifications through notify-send, when it is there.</summary>
static class Notify
{
    public static void Send(string title, string body)
    {
        if (!Which.Exists("notify-send")) return;
        try
        {
            Helper.Run("notify-send", ["-a", "PapegaAI", title, body], timeoutMs: 3000);
        }
        catch { /* a missing notification is never worth an error */ }
    }
}
