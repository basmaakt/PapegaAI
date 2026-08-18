using NAudio.CoreAudioApi;
using Parrot.Models;

namespace Parrot;

/// <summary>
/// Startup health checks. Windows needs far fewer than macOS — there is no
/// accessibility permission, no fn-key remap, and desktop apps can normally
/// read the mic — so we check what can actually go wrong here: a capture
/// device being present and the model cache.
/// </summary>
static class Doctor
{
    public sealed record Check(string Name, bool Ok, string Detail);

    public static List<Check> RunChecks(string? modelId = null)
    {
        var checks = new List<Check> { CheckMicrophone() };

        var model = modelId is not null
            ? ModelRegistry.Find(modelId) ?? ModelRegistry.Recommended()
            : ModelRegistry.Recommended();
        bool cached = ModelDownloader.IsCached(model);
        checks.Add(new Check(
            $"model {model.Id}",
            true, // not fatal — the daemon downloads on warmup
            cached ? "cached" : "not downloaded yet (will download on first run)"));

        return checks;
    }

    static Check CheckMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            return new Check("microphone", true, device.FriendlyName);
        }
        catch (Exception)
        {
            return new Check(
                "microphone", false,
                "no default capture device. Plug in a mic, and check Settings → Privacy & security → Microphone → 'Let desktop apps access your microphone'.");
        }
    }

    public static bool AllOk(List<Check> checks) => checks.All(c => c.Ok);

    public static void Print(List<Check> checks)
    {
        foreach (var c in checks)
            Console.Error.WriteLine($"  {(c.Ok ? "✓" : "✗")} {c.Name} — {c.Detail}");
    }

    public static int RunCli()
    {
        var checks = RunChecks();
        Print(checks);
        return AllOk(checks) ? 0 : 1;
    }
}
