using Parrot.Audio;
using Parrot.Input;
using Parrot.Models;
using Parrot.Platform;

namespace Parrot;

/// <summary>
/// Startup health checks. Linux needs more of them than Windows: the pieces
/// that Windows simply provides (a key hook, synthetic typing) are here a
/// matter of which session you run and which permissions you were given, so
/// the checks aim to name the exact fix rather than report a failure.
/// </summary>
static class Doctor
{
    public sealed record Check(string Name, bool Ok, string Detail, string? Fix = null);

    public static List<Check> RunChecks(Config config, string? modelId = null)
    {
        return
        [
            CheckSession(),
            CheckMicrophone(config.AudioDevice),
            CheckHotkey(config),
            CheckInjection(config),
            CheckModel(modelId ?? config.Model),
        ];
    }

    static Check CheckSession()
    {
        string description = LinuxSession.Describe();
        bool graphical = LinuxSession.IsX11 || LinuxSession.IsWayland;
        return new Check("sessie", graphical, description,
            graphical ? null : "PapegaAI heeft een grafische sessie nodig om tekst te kunnen invoegen.");
    }

    static Check CheckMicrophone(string? device)
    {
        string backend = LinuxAudio.Describe(device);
        try
        {
            using var capture = LinuxAudio.Create(device);
            capture.Start();
            capture.Stop();
            return new Check("microfoon", true, backend);
        }
        catch (Exception ex)
        {
            return new Check("microfoon", false, $"{backend} — {ex.Message}",
                "Controleer of er een opnameapparaat is (`arecord -l`) en of PulseAudio/PipeWire draait.");
        }
    }

    static Check CheckHotkey(Config config)
    {
        string hotkey = config.Hotkey ?? HotkeyNames.Default;
        string plan = HotkeyBackends.Describe(config.HotkeyBackend);

        if (LinuxKeys.Evdev(hotkey) is null)
            return new Check("sneltoets", false, $"onbekende toets '{hotkey}'",
                $"Kies er een uit: {HotkeyNames.Describe()}");

        // X11 RECORD needs nothing; evdev needs the input group. Only complain
        // when evdev is the route that will actually be taken.
        bool needsEvdev = !X11HotkeyMonitor.IsAvailable()
            || (config.HotkeyBackend ?? "").Equals("evdev", StringComparison.OrdinalIgnoreCase);

        if (needsEvdev && !EvdevHotkeyMonitor.CanReadInputDevices())
        {
            return new Check("sneltoets", false, $"{hotkey} via {plan} — geen toegang tot /dev/input",
                EvdevHotkeyMonitor.DescribePermissionProblem());
        }

        string detail = $"{hotkey} via {plan}";
        if (LinuxKeys.TogglesSomething(hotkey))
            detail += " — let op: deze toets blijft zijn eigen functie doen (caps-lock schakelt dus wél)";
        return new Check("sneltoets", true, detail);
    }

    static Check CheckInjection(Config config)
    {
        using var injector = new LinuxTextInjector(config.Injection, config.PasteShortcut);
        if (!injector.HasAnyMethod)
        {
            return new Check("tekst invoegen", false, "geen methode beschikbaar",
                LinuxSession.IsWayland
                    ? "Installeer wl-clipboard (`wl-copy`) en zorg voor toegang tot /dev/uinput, of installeer wtype/ydotool."
                    : "Installeer xdotool (`sudo apt install xdotool` / `sudo dnf install xdotool`).");
        }

        // Clipboard-only means the user still has to press Ctrl+V themselves —
        // it works, but it is not the experience the app promises.
        bool typesByItself = !injector.Mechanism.StartsWith("clipboard");
        return new Check("tekst invoegen", typesByItself, injector.Mechanism,
            typesByItself
                ? null
                : "Alleen het klembord is beschikbaar: PapegaAI plakt de tekst niet zelf. " +
                  UinputDevice.DescribePermissionProblem());
    }

    static Check CheckModel(string? modelId)
    {
        var model = modelId is not null
            ? ModelRegistry.Find(modelId) ?? ModelRegistry.Recommended()
            : ModelRegistry.Recommended();
        bool cached = ModelDownloader.IsCached(model);
        return new Check($"model {model.Id}", true,
            cached ? "in cache" : "nog niet gedownload (gebeurt bij de eerste start)");
    }

    public static bool AllOk(List<Check> checks) => checks.All(c => c.Ok);

    public static void Print(List<Check> checks)
    {
        foreach (var c in checks)
        {
            Console.Error.WriteLine($"  {(c.Ok ? "✓" : "✗")} {c.Name} — {c.Detail}");
            if (!c.Ok && c.Fix is not null)
                foreach (string line in c.Fix.Split('\n'))
                    Console.Error.WriteLine($"      {line}");
        }
    }

    public static int RunCli(Config config)
    {
        var checks = RunChecks(config);
        Print(checks);
        return AllOk(checks) ? 0 : 1;
    }
}
