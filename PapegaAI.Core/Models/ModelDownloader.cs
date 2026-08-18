namespace Parrot.Models;

/// <summary>
/// Downloads GGML model files into the models directory (see
/// <see cref="Paths.ModelsDir"/>) with progress on stderr. Downloads to a
/// .part file and renames on completion, so an interrupted download never
/// leaves a truncated model behind.
/// </summary>
public static class ModelDownloader
{
    public static string ModelsDir => Paths.ModelsDir;

    public static string PathFor(TranscriptionModel model) =>
        Path.Combine(ModelsDir, model.FileName);

    public static bool IsCached(TranscriptionModel model) =>
        File.Exists(PathFor(model));

    /// <summary>Return the local path, downloading first if needed.</summary>
    public static async Task<string> Ensure(TranscriptionModel model)
    {
        string path = PathFor(model);
        if (File.Exists(path)) return path;

        Directory.CreateDirectory(ModelsDir);
        string partPath = path + ".part";

        Console.Error.WriteLine($"downloading {model.Id} ({model.SizeMB} MB)...");

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromMinutes(30);

        using var response = await http.GetAsync(model.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using (var source = await response.Content.ReadAsStreamAsync())
        await using (var target = File.Create(partPath))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read));
                done += read;
                if (total > 0)
                {
                    int percent = (int)(done * 100 / total);
                    if (percent != lastPercent && percent % 5 == 0)
                    {
                        lastPercent = percent;
                        Console.Error.Write($"\r  {percent}% ({done / (1024 * 1024)} / {total / (1024 * 1024)} MB)");
                    }
                }
            }
        }

        Console.Error.WriteLine();
        File.Move(partPath, path, overwrite: true);
        Console.Error.WriteLine($"✓ saved to {path}");
        return path;
    }
}
