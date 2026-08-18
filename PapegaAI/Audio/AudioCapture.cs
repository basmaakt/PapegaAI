using NAudio.CoreAudioApi;
using NAudio.Wave;
using Parrot.Platform;

namespace Parrot.Audio;

/// <summary>
/// Captures the default microphone while recording is active and returns a
/// 16 kHz mono float buffer when stopped. WASAPI shared mode delivers the
/// device's mix format (typically 48 kHz float stereo); we downmix to mono on
/// the fly and resample once at stop, since transcription only starts then
/// anyway.
/// </summary>
sealed class AudioCapture : IAudioCapture
{
    public const int TargetSampleRate = AudioFormat.SampleRate;

    /// <summary>Called for every audio buffer with the buffer's RMS level
    /// (0…~1). Invoked on the capture thread; hop to the UI thread for UI.</summary>
    public Action<float>? OnLevel { get; set; }

    WasapiCapture? capture;
    readonly List<float> samples = new();
    readonly object gate = new();
    int nativeRate;
    bool isRecording;

    /// <summary>Begin recording. Idempotent — calling while recording is a no-op.</summary>
    public void Start()
    {
        if (isRecording) return;

        // A fresh WasapiCapture per press tracks default-device changes and
        // keeps the mic-in-use indicator honest (only lit while the key is held).
        var cap = new WasapiCapture();
        var format = cap.WaveFormat;
        nativeRate = format.SampleRate;

        lock (gate) samples.Clear();

        cap.DataAvailable += (_, e) => Process(e.Buffer, e.BytesRecorded, format);
        cap.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
                Console.Error.WriteLine($"capture error: {e.Exception.Message}");
        };

        cap.StartRecording();
        capture = cap;
        isRecording = true;
    }

    /// <summary>Stop recording and return all captured samples (16 kHz mono float).</summary>
    public float[] Stop()
    {
        if (!isRecording) return Array.Empty<float>();
        isRecording = false;

        var cap = capture;
        capture = null;
        try
        {
            cap?.StopRecording();
        }
        finally
        {
            cap?.Dispose();
        }

        float[] native;
        lock (gate)
        {
            native = samples.ToArray();
            samples.Clear();
        }

        return Dsp.Resample(native, nativeRate, TargetSampleRate);
    }

    void Process(byte[] buffer, int bytes, WaveFormat format)
    {
        if (!isRecording || bytes == 0) return;

        int channels = format.Channels;
        float[] mono;

        if (format.BitsPerSample == 32)
        {
            // WASAPI shared-mode mix format: 32-bit IEEE float.
            int frames = bytes / (4 * channels);
            mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                    sum += BitConverter.ToSingle(buffer, (f * channels + ch) * 4);
                mono[f] = sum / channels;
            }
        }
        else if (format.BitsPerSample == 16)
        {
            int frames = bytes / (2 * channels);
            mono = new float[frames];
            for (int f = 0; f < frames; f++)
            {
                float sum = 0;
                for (int ch = 0; ch < channels; ch++)
                    sum += BitConverter.ToInt16(buffer, (f * channels + ch) * 2) / 32768f;
                mono[f] = sum / channels;
            }
        }
        else
        {
            return; // unsupported bit depth; nothing sensible to do
        }

        lock (gate) samples.AddRange(mono);
        OnLevel?.Invoke(Dsp.ComputeRms(mono));
    }

    public void Dispose()
    {
        isRecording = false;
        capture?.Dispose();
        capture = null;
    }
}

