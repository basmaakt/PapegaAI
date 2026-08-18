namespace Parrot.Platform;

/// <summary>
/// Records the default microphone while the push-to-talk key is held.
/// Implementations resample to <see cref="SampleRate"/> mono float, which is
/// what whisper.cpp wants — WASAPI on Windows, ALSA (or a helper process) on
/// Linux.
/// </summary>
public interface IAudioCapture : IDisposable
{
    /// <summary>Every capture buffer's RMS level (0…~1), for the overlay's
    /// waveform. Raised on the capture thread — marshal before touching UI.</summary>
    Action<float>? OnLevel { get; set; }

    /// <summary>Begin recording. Idempotent while already recording.</summary>
    void Start();

    /// <summary>Stop and return everything captured, 16 kHz mono float.</summary>
    float[] Stop();
}

public static class AudioFormat
{
    public const int SampleRate = 16_000;
}
