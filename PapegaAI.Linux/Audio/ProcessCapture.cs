using System.Diagnostics;
using Parrot.Platform;

namespace Parrot.Audio;

/// <summary>
/// Fallback capture that pipes raw audio out of the desktop's own recording
/// tool (parec, pw-record or arecord). Slower to start than talking to
/// alsa-lib directly, but it works on stripped-down systems that have no
/// alsa-lib development symlinks, and it makes "which device?" debuggable
/// with the same command on the shell.
/// </summary>
sealed class ProcessCapture : IAudioCapture
{
    /// <summary>Recorders in preference order. parec speaks the PulseAudio
    /// protocol, which PipeWire also serves; pw-record is PipeWire-native;
    /// arecord is the bare-metal ALSA one.</summary>
    static readonly (string Tool, string[] Args)[] Recorders =
    [
        ("parec", ["--format=s16le", "--rate=16000", "--channels=1"]),
        ("pw-record", ["--rate=16000", "--channels=1", "--format=s16", "--raw", "-"]),
        ("arecord", ["-q", "-f", "S16_LE", "-r", "16000", "-c", "1", "-t", "raw"]),
    ];

    public static string? FindTool() =>
        Recorders.Select(r => r.Tool).FirstOrDefault(Which.Exists);

    public Action<float>? OnLevel { get; set; }

    readonly string tool;
    readonly string[] args;
    readonly List<float> samples = new();
    readonly object gate = new();
    Process? process;
    Thread? reader;
    volatile bool running;

    public ProcessCapture(string? tool = null, string? device = null)
    {
        var chosen = tool is not null
            ? Recorders.FirstOrDefault(r => r.Tool == tool)
            : Recorders.FirstOrDefault(r => Which.Exists(r.Tool));

        if (chosen.Tool is null)
            throw new InvalidOperationException(
                "no recording tool found — install pulseaudio-utils (parec), pipewire-utils (pw-record) or alsa-utils (arecord)");

        this.tool = chosen.Tool;
        var list = chosen.Args.ToList();
        if (!string.IsNullOrEmpty(device))
        {
            // Every one of the three spells "device" as -d/-D, but with
            // different meanings for the value; the user supplies whatever
            // their tool expects.
            list.AddRange(this.tool == "arecord" ? ["-D", device] : ["-d", device]);
        }
        args = list.ToArray();
    }

    public string Description => $"{tool} {string.Join(' ', args)}";

    public void Start()
    {
        if (running) return;

        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string a in args) psi.ArgumentList.Add(a);

        process = Process.Start(psi)
            ?? throw new InvalidOperationException($"could not start {tool}");

        // Drain stderr so a chatty recorder can't block on a full pipe.
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.Error.WriteLine($"  [{tool}] {e.Data}");
        };
        process.BeginErrorReadLine();

        lock (gate) samples.Clear();
        running = true;
        reader = new Thread(ReadLoop) { IsBackground = true, Name = "papegaai-capture" };
        reader.Start();
    }

    void ReadLoop()
    {
        var stream = process!.StandardOutput.BaseStream;
        var buffer = new byte[3200];   // 100 ms of S16 mono at 16 kHz
        var mono = new float[1600];

        while (running)
        {
            int read;
            try
            {
                read = stream.Read(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                if (running) Console.Error.WriteLine($"capture error: {ex.Message}");
                return;
            }
            if (read <= 0) return;

            int frames = read / 2;
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

        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch { /* already gone */ }

        reader?.Join(500);
        reader = null;
        process?.Dispose();
        process = null;

        lock (gate)
        {
            var result = samples.ToArray();
            samples.Clear();
            return result;   // the recorder was asked for 16 kHz mono directly
        }
    }

    public void Dispose()
    {
        running = false;
        try
        {
            if (process is { HasExited: false }) process.Kill(entireProcessTree: true);
        }
        catch { }
        process?.Dispose();
        process = null;
    }
}
