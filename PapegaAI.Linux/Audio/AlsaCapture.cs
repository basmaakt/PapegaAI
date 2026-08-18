using System.Runtime.InteropServices;
using Parrot.Platform;

namespace Parrot.Audio;

/// <summary>
/// Microphone capture through alsa-lib. Every Linux desktop has ALSA at the
/// bottom of its audio stack — PipeWire and PulseAudio both ship a plugin that
/// makes the "default" device route through them — so this one path covers
/// modern and older systems alike.
///
/// Unlike WASAPI on Windows, ALSA's plug layer converts sample rate and
/// channel count for us, so we can simply ask for the 16 kHz mono the model
/// wants and skip resampling entirely.
/// </summary>
sealed class AlsaCapture : IAudioCapture
{
    const string Lib = "libasound.so.2";

    const int SND_PCM_STREAM_CAPTURE = 1;
    const int SND_PCM_FORMAT_S16_LE = 2;
    const int SND_PCM_ACCESS_RW_INTERLEAVED = 3;
    const int EPIPE = 32;   // overrun

    [DllImport(Lib)]
    static extern int snd_pcm_open(out nint pcm,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int stream, int mode);

    [DllImport(Lib)]
    static extern int snd_pcm_set_params(nint pcm, int format, int access,
        uint channels, uint rate, int softResample, uint latencyMicroseconds);

    [DllImport(Lib)]
    static extern nint snd_pcm_readi(nint pcm, byte[] buffer, nint frames);

    [DllImport(Lib)]
    static extern int snd_pcm_recover(nint pcm, int err, int silent);

    [DllImport(Lib)]
    static extern int snd_pcm_drop(nint pcm);

    [DllImport(Lib)]
    static extern int snd_pcm_close(nint pcm);

    [DllImport(Lib)]
    static extern nint snd_strerror(int err);

    static string Err(int code) =>
        Marshal.PtrToStringUTF8(snd_strerror(code)) ?? $"alsa error {code}";

    /// <summary>True when alsa-lib can be loaded at all. Checked before the
    /// daemon commits to this backend so a missing library becomes a clear
    /// message instead of a DllNotFoundException mid-dictation.</summary>
    public static bool IsAvailable()
    {
        try
        {
            return NativeLibrary.TryLoad(Lib, out _);
        }
        catch
        {
            return false;
        }
    }

    public Action<float>? OnLevel { get; set; }

    readonly string device;
    readonly List<float> samples = new();
    readonly object gate = new();
    nint pcm;
    Thread? reader;
    volatile bool running;

    /// <param name="device">ALSA device name. "default" follows the desktop's
    /// audio server; "pulse", "hw:1,0" and friends also work.</param>
    public AlsaCapture(string? device = null) => this.device = device ?? "default";

    public void Start()
    {
        if (running) return;

        int rc = snd_pcm_open(out pcm, device, SND_PCM_STREAM_CAPTURE, 0);
        if (rc < 0)
        {
            pcm = 0;
            throw new InvalidOperationException(
                $"could not open audio device '{device}': {Err(rc)}");
        }

        // 100 ms of latency is plenty for push-to-talk and keeps the level
        // meter responsive; soft_resample=1 lets ALSA convert from whatever
        // the hardware actually runs at.
        rc = snd_pcm_set_params(pcm, SND_PCM_FORMAT_S16_LE, SND_PCM_ACCESS_RW_INTERLEAVED,
            channels: 1, rate: (uint)AudioFormat.SampleRate, softResample: 1,
            latencyMicroseconds: 100_000);
        if (rc < 0)
        {
            snd_pcm_close(pcm);
            pcm = 0;
            throw new InvalidOperationException(
                $"device '{device}' will not do 16 kHz mono: {Err(rc)}");
        }

        lock (gate) samples.Clear();
        running = true;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "papegaai-alsa" };
        reader.Start();
    }

    void ReadLoop()
    {
        const int framesPerRead = 1600;           // 100 ms
        var buffer = new byte[framesPerRead * 2]; // S16 mono
        var mono = new float[framesPerRead];

        while (running)
        {
            nint got = snd_pcm_readi(pcm, buffer, framesPerRead);
            if (got < 0)
            {
                int err = (int)got;
                // Overruns happen when the machine stalls; recover and carry
                // on rather than losing the whole dictation.
                if (snd_pcm_recover(pcm, err, silent: 1) < 0)
                {
                    if (running)
                        Console.Error.WriteLine($"capture error: {Err(err)}");
                    return;
                }
                continue;
            }

            int frames = (int)got;
            if (frames == 0) continue;

            for (int i = 0; i < frames; i++)
                mono[i] = BitConverter.ToInt16(buffer, i * 2) / 32768f;

            var span = mono.AsSpan(0, frames);
            lock (gate) samples.AddRange(span.ToArray());
            OnLevel?.Invoke(Dsp.ComputeRms(span));
        }
    }

    public float[] Stop()
    {
        if (!running) return Array.Empty<float>();
        running = false;

        reader?.Join(500);
        reader = null;

        if (pcm != 0)
        {
            snd_pcm_drop(pcm);
            snd_pcm_close(pcm);
            pcm = 0;
        }

        lock (gate)
        {
            var result = samples.ToArray();
            samples.Clear();
            return result;   // already 16 kHz mono — ALSA resampled for us
        }
    }

    public void Dispose()
    {
        running = false;
        try { reader?.Join(500); } catch { }
        if (pcm != 0)
        {
            snd_pcm_close(pcm);
            pcm = 0;
        }
    }
}
