using Parrot.Models;

namespace Parrot;

/// <summary>
/// The rules for turning a model id + language preference into an actual
/// model, shared by both platform front-ends so a config file behaves the
/// same everywhere.
/// </summary>
public static class ModelSelection
{
    /// <summary>Taal waarin zonder configuratie wordt getranscribeerd.
    /// PapegaAI is een Nederlandse port en wordt in het Nederlands gebruikt;
    /// "auto" laten detecteren kost nauwkeurigheid op korte fragmenten.
    /// Meertalige modellen accepteren elke ISO-code via --language.</summary>
    public const string DefaultLanguage = "nl";

    /// <summary>Resolve model id + language, enforcing that forced non-English
    /// languages get a multilingual model. Returns null after printing an
    /// error the user can act on.</summary>
    public static (TranscriptionModel Model, string Language)? Resolve(
        string? modelId, string? languageArg)
    {
        TranscriptionModel model;
        if (modelId is not null)
        {
            var m = ModelRegistry.Find(modelId);
            if (m is null)
            {
                Console.Error.WriteLine($"unknown model: {modelId}");
                Console.Error.WriteLine("run `PapegaAI models list` to see options.");
                return null;
            }
            model = m;
        }
        else
        {
            model = ModelRegistry.Recommended();
        }

        bool multilingual = model.Languages.Contains("multi");
        string language = languageArg ?? (multilingual ? DefaultLanguage : "en");
        if (!multilingual && language is not ("en" or "auto"))
        {
            Console.Error.WriteLine($"model {model.Id} is English-only, so `--language {language}` won't work.");
            Console.Error.WriteLine("use a multilingual model, e.g.: --model whisper-small --language nl");
            return null;
        }
        if (!multilingual) language = "en";
        return (model, language);
    }

    /// <summary>Without GPU acceleration, models beyond small are slower than
    /// speaking. Auto-pick the small sibling; small/base run as-is. Only
    /// applies when a GPU was wanted but none loaded — explicitly disabling
    /// the GPU keeps the chosen model, however slow.</summary>
    public static string? AutoCpuFallback(TranscriptionModel model) =>
        model.SizeMB <= 500
            ? null
            : model.Languages.Contains("multi") ? "whisper-small" : "whisper-small.en";
}
