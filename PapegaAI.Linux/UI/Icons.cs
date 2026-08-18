using System.Reflection;
using Avalonia.Media.Imaging;

namespace Parrot.UI;

/// <summary>
/// The macaw artwork, embedded in the executable so PapegaAI stays a single
/// file to copy around. The PNGs are exported from the same drawing code the
/// Windows tray icon uses, so both platforms show exactly the same bird.
/// </summary>
static class Icons
{
    static readonly Dictionary<string, Bitmap> cache = new();

    public static Bitmap Idle => Load("papegaai-256.png");
    public static Bitmap Recording => Load("papegaai-recording-256.png");
    public static Bitmap Small => Load("papegaai-64.png");

    public static Bitmap For(bool recording) => recording ? Recording : Idle;

    static Bitmap Load(string name)
    {
        lock (cache)
        {
            if (cache.TryGetValue(name, out var cached)) return cached;

            var assembly = Assembly.GetExecutingAssembly();
            string resource = $"PapegaAI.Linux.Assets.{name}";
            using Stream stream = assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException($"ingebouwde afbeelding ontbreekt: {resource}");

            var bitmap = new Bitmap(stream);
            cache[name] = bitmap;
            return bitmap;
        }
    }

    /// <summary>Writes the icon set into an icon theme directory, so the
    /// desktop entry and the window manager can find it by name.</summary>
    public static void ExportTo(string hicolorDir)
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (int size in new[] { 32, 48, 64, 128, 256 })
        {
            using Stream? stream = assembly.GetManifestResourceStream(
                $"PapegaAI.Linux.Assets.papegaai-{size}.png");
            if (stream is null) continue;

            string dir = Path.Combine(hicolorDir, $"{size}x{size}", "apps");
            Directory.CreateDirectory(dir);
            using var file = File.Create(Path.Combine(dir, "papegaai.png"));
            stream.CopyTo(file);
        }
    }
}
