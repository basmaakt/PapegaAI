using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot;

/// <summary>
/// Optional persistent defaults (see <see cref="Paths.ConfigFile"/>), so the
/// daily driver needs no flags. Command-line flags always win over config.
/// The file format is identical on Windows and Linux; only the location and a
/// few Linux-only keys differ.
/// </summary>
public sealed class Config
{
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("cpu_model")] public string? CpuModel { get; set; }
    [JsonPropertyName("gpu")] public bool? Gpu { get; set; }
    [JsonPropertyName("language")] public string? Language { get; set; }
    [JsonPropertyName("hotkey")] public string? Hotkey { get; set; }
    [JsonPropertyName("overlay")] public bool? Overlay { get; set; }
    [JsonPropertyName("clear_history_on_reboot")] public bool? ClearHistoryOnReboot { get; set; }

    /// <summary>Type a space before the transcript, so dictating twice in a
    /// row does not run the words together. Default on; see
    /// <see cref="Transcription.OutputFormatting.ForInjection"/>.</summary>
    [JsonPropertyName("leading_space")] public bool? LeadingSpace { get; set; }

    /// <summary>Linux only: how transcripts reach the focused window —
    /// "auto" (default), "xdotool", "wtype", "uinput" or "clipboard".
    /// Ignored on Windows, which always uses SendInput.</summary>
    [JsonPropertyName("injection")] public string? Injection { get; set; }

    /// <summary>Linux only: the paste shortcut used by the clipboard-based
    /// injection paths — "ctrl+v" (default) or "ctrl+shift+v" (terminals).</summary>
    [JsonPropertyName("paste_shortcut")] public string? PasteShortcut { get; set; }

    /// <summary>Linux only: capture device name passed to ALSA/PulseAudio.
    /// Null means the system default.</summary>
    [JsonPropertyName("audio_device")] public string? AudioDevice { get; set; }

    /// <summary>Linux only: how the push-to-talk key is watched — "auto"
    /// (default), "x11" for the RECORD extension, or "evdev" for the kernel's
    /// input layer. Ignored on Windows, which always uses a keyboard hook.</summary>
    [JsonPropertyName("hotkey_backend")] public string? HotkeyBackend { get; set; }

    public static string FilePath => Paths.ConfigFile;

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath)) ?? new Config();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not read {FilePath}: {ex.Message} — using defaults");
        }
        return new Config();
    }

    public void Save()
    {
        Paths.EnsureDir(Path.GetDirectoryName(FilePath)!);
        // Skip nulls: a Windows config should not sprout the Linux-only keys
        // (and vice versa) merely because the settings window saved once.
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        }));
    }
}
