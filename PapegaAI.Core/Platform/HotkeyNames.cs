namespace Parrot.Platform;

/// <summary>
/// The push-to-talk keys PapegaAI understands, by name. Names are the same on
/// every platform so one config.json travels between machines; each platform
/// maps a name to its own key code.
///
/// The macOS original uses fn, which no other platform exposes usefully —
/// Windows keyboards handle it in firmware and Linux only sees it on a few
/// laptops — hence right-ctrl as the default everywhere.
/// </summary>
public static class HotkeyNames
{
    public const string Default = "right-ctrl";

    /// <summary>Named modifier/lock keys, in the order the settings window
    /// should offer them.</summary>
    public static readonly string[] Named =
    [
        "right-ctrl", "left-ctrl", "right-alt", "right-shift", "left-shift",
        "right-super", "caps-lock", "scroll-lock",
    ];

    /// <summary>Every accepted name, including f13…f24 — keys no normal
    /// keyboard sends by accident, which is why they make good hotkeys.</summary>
    public static IEnumerable<string> All =>
        Named.Concat(Enumerable.Range(13, 12).Select(n => $"f{n}"));

    /// <summary>Keys that do something on their own when tapped (caps-lock
    /// toggling case, scroll-lock the LED) and are therefore swallowed while
    /// they serve as the hotkey.</summary>
    public static bool ShouldSwallow(string name) =>
        name is "caps-lock" or "scroll-lock";

    /// <summary>Recognise "f13".."f24" and hand back the number.</summary>
    public static bool TryFunctionKey(string name, out int number)
    {
        number = 0;
        return name.Length >= 3
            && (name[0] is 'f' or 'F')
            && int.TryParse(name.AsSpan(1), out number)
            && number is >= 13 and <= 24;
    }

    public static string Normalize(string name) => name.Trim().ToLowerInvariant();

    public static bool IsKnown(string name)
    {
        string n = Normalize(name);
        return Named.Contains(n) || TryFunctionKey(n, out _);
    }

    public static string Describe() =>
        string.Join(", ", Named) + ", f13…f24";
}
