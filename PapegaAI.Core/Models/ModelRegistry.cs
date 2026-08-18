namespace Parrot.Models;

/// <summary>
/// A transcription model. The macOS original runs CoreML models via
/// WhisperKit; the Windows port runs the same Whisper weights as GGML files
/// via whisper.cpp (Whisper.net), downloaded from ggerganov's Hugging Face
/// mirror.
/// </summary>
public sealed record TranscriptionModel(
    string Id,
    string DisplayName,
    string FileName,
    string Url,
    int SizeMB,
    string[] Languages,
    bool Recommended);

public static class ModelRegistry
{
    const string Hub = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main";

    public static readonly TranscriptionModel[] All =
    [
        new(
            Id: "whisper-base.en",
            DisplayName: "Whisper Base (English)",
            FileName: "ggml-base.en.bin",
            Url: $"{Hub}/ggml-base.en.bin",
            SizeMB: 142,
            Languages: ["en"],
            Recommended: false),
        new(
            Id: "whisper-small.en",
            DisplayName: "Whisper Small (English)",
            FileName: "ggml-small.en.bin",
            Url: $"{Hub}/ggml-small.en.bin",
            SizeMB: 466,
            Languages: ["en"],
            Recommended: false),
        new(
            Id: "whisper-base",
            DisplayName: "Whisper Base (multilingual)",
            FileName: "ggml-base.bin",
            Url: $"{Hub}/ggml-base.bin",
            SizeMB: 142,
            Languages: ["multi"],
            Recommended: false),
        new(
            Id: "whisper-small",
            DisplayName: "Whisper Small (multilingual — good Dutch)",
            FileName: "ggml-small.bin",
            Url: $"{Hub}/ggml-small.bin",
            SizeMB: 466,
            Languages: ["multi"],
            Recommended: false),
        new(
            Id: "whisper-large-v3-turbo",
            DisplayName: "Whisper Large v3 Turbo",
            FileName: "ggml-large-v3-turbo.bin",
            Url: $"{Hub}/ggml-large-v3-turbo.bin",
            SizeMB: 1624,
            Languages: ["multi"],
            // Het aanbevolen model: met een GPU is turbo zowel het snelste als
            // het nauwkeurigste. Zonder GPU schakelt ModelSelection.AutoCpuFallback
            // door naar whisper-small, want turbo op een CPU is trager dan praten.
            Recommended: true),
    ];

    public static TranscriptionModel? Find(string id) =>
        All.FirstOrDefault(m => m.Id == id);

    public static TranscriptionModel Recommended() =>
        All.FirstOrDefault(m => m.Recommended) ?? All[0];
}
