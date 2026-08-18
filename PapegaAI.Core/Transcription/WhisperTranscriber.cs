using System.Text;
using System.Text.RegularExpressions;
using Parrot.Models;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Parrot.Transcription;

/// <summary>
/// Whisper inference via whisper.cpp (Whisper.net). Same weights as the macOS
/// original's WhisperKit, a different runtime: whisper.cpp picks the best
/// native library available — Vulkan or CUDA on a GPU, plain CPU otherwise —
/// on both Windows and Linux.
/// </summary>
public sealed class WhisperTranscriber : IDisposable
{
    readonly TranscriptionModel model;
    readonly string language;
    WhisperFactory? factory;
    WhisperProcessor? processor;
    readonly SemaphoreSlim gate = new(1, 1);

    /// <param name="language">ISO code ("nl", "en", …) to force a language,
    /// or "auto" to detect per utterance (multilingual models only).</param>
    public WhisperTranscriber(TranscriptionModel model, string language = "en")
    {
        this.model = model;
        this.language = language;
    }

    /// <summary>Which native runtime whisper.cpp actually loaded (Cuda,
    /// Vulkan, Cpu, …). Only meaningful after a factory has been created.</summary>
    public static string LoadedRuntime =>
        RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";

    public static bool IsGpuRuntime => RuntimeOptions.LoadedLibrary
        is RuntimeLibrary.Cuda or RuntimeLibrary.Vulkan
        or RuntimeLibrary.CoreML or RuntimeLibrary.OpenVino;

    /// <summary>Downloads (if needed) the model file and creates the native
    /// factory — after this, <see cref="LoadedRuntime"/> is known, so callers
    /// can still switch to a CPU fallback model before full warmup.</summary>
    public async Task LoadFactory()
    {
        if (factory is not null) return;
        string path = await ModelDownloader.Ensure(model);
        Console.Error.WriteLine($"loading {model.Id}...");
        factory = WhisperFactory.FromPath(path);
    }

    /// <summary>Downloads (if needed) and loads the model. Call once at
    /// startup so the first hotkey press isn't blocked on a download.</summary>
    public async Task WarmUp()
    {
        if (processor is not null) return;

        await LoadFactory();
        var builder = factory!.CreateBuilder().WithThreads(Math.Max(2, Environment.ProcessorCount / 2));
        processor = language == "auto"
            ? builder.WithLanguageDetection().Build()
            : builder.WithLanguage(language).Build();

        // Run a sliver of silence through the pipeline so the first real
        // dictation doesn't pay the lazy-initialization cost.
        var warmup = new float[16_000];
        await foreach (var _ in processor.ProcessAsync(warmup)) { }

        Console.Error.WriteLine($"✓ {model.Id} ready · runtime: {LoadedRuntime}");
    }

    public async Task<string> Transcribe(float[] samples)
    {
        if (processor is null) await WarmUp();

        await gate.WaitAsync();
        try
        {
            var sb = new StringBuilder();
            await foreach (var segment in processor!.ProcessAsync(samples))
                sb.Append(segment.Text).Append(' ');
            return Sanitize(sb.ToString());
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Strip Whisper's non-speech bracket tokens ([BLANK_AUDIO], [MUSIC],
    /// (silence), &lt;|nospeech|&gt;, etc.) and collapse whitespace. When the
    /// model hears silence it emits these literally; we don't want to paste them.
    /// </summary>
    public static string Sanitize(string text)
    {
        string[] patterns =
        [
            @"\[[^\]]*\]",   // [BLANK_AUDIO], [MUSIC], [Applause]
            @"\([^)]*\)",    // (silence), (music playing)
            @"<\|[^|]*\|>",  // <|nospeech|>, <|endoftext|>
            @"\*[^*]*\*",    // *background noise*
        ];
        string result = text;
        foreach (string p in patterns)
            result = Regex.Replace(result, p, " ");
        result = Regex.Replace(result, @"\s+", " ").Trim();
        // A transcript of only punctuation ("*", "...", "-") is a silence
        // hallucination, not speech — never worth injecting.
        return Regex.IsMatch(result, @"^[\p{P}\p{S}\s]*$") ? "" : result;
    }

    public void Dispose()
    {
        processor?.Dispose();
        factory?.Dispose();
        gate.Dispose();
    }
}
