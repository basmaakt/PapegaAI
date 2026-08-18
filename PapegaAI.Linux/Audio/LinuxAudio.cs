using Parrot.Platform;

namespace Parrot.Audio;

/// <summary>Picks a capture backend for this machine.</summary>
static class LinuxAudio
{
    /// <param name="device">Optional override from config.json's
    /// "audio_device". A plain name goes to ALSA ("default", "pulse",
    /// "hw:1,0"); prefix it with a tool name to force the helper-process
    /// route instead, e.g. "parec:alsa_input.usb-Blue_Yeti".</param>
    public static IAudioCapture Create(string? device = null)
    {
        if (device is not null && device.Contains(':')
            && device.Split(':', 2) is [var tool, var rest]
            && tool is "parec" or "pw-record" or "arecord")
        {
            return new ProcessCapture(tool, rest.Length == 0 ? null : rest);
        }

        if (AlsaCapture.IsAvailable())
            return new AlsaCapture(device);

        return new ProcessCapture(device: device);
    }

    /// <summary>One line describing what capture will use, for `doctor`.</summary>
    public static string Describe(string? device)
    {
        if (AlsaCapture.IsAvailable() && (device is null || !device.Contains(':')))
            return $"ALSA ({device ?? "default"})";
        string? tool = ProcessCapture.FindTool();
        return tool is null ? "none found" : $"{tool} (helper process)";
    }
}
