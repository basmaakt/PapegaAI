using System.Text.Json;

namespace Parrot.History;

public sealed record HistoryEntry(DateTime Time, double Seconds, string Text);

/// <summary>
/// The last dictations, persisted to <see cref="Paths.HistoryFile"/> so a
/// transcript survives a failed injection or an accidental overwrite. Capped;
/// everything stays local, like the audio itself.
/// </summary>
public sealed class HistoryStore
{
    const int Cap = 100;

    static string FilePath => Paths.HistoryFile;

    static string BootMarkerPath => Paths.BootMarkerFile;

    readonly object gate = new();
    List<HistoryEntry> entries = new();

    /// <param name="clearOnReboot">Wipe the stored history when the machine has
    /// rebooted since the previous PapegaAI run. A PapegaAI-only restart (model
    /// switch, upgrade) keeps the history — same boot session.</param>
    public HistoryStore(bool clearOnReboot = false)
    {
        try
        {
            if (File.Exists(FilePath))
                entries = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not read history: {ex.Message}");
        }

        if (clearOnReboot && IsNewBootSession() && entries.Count > 0)
        {
            Console.Error.WriteLine("new boot session — clearing dictation history");
            entries.Clear();
            Persist();
        }
        WriteBootMarker();
    }

    /// <summary>Boot time derived from the uptime counter (TickCount64 counts
    /// since boot on Windows and Linux alike). Recomputed values
    /// for the same boot drift by at most seconds; a real reboot moves it by
    /// far more than the two-minute tolerance.</summary>
    static DateTime CurrentBootTime =>
        DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);

    static bool IsNewBootSession()
    {
        try
        {
            // No marker yet (fresh install or feature just enabled): treat as
            // the same session — only wipe after an actual observed reboot.
            if (!File.Exists(BootMarkerPath)) return false;
            var stored = DateTime.Parse(File.ReadAllText(BootMarkerPath).Trim(),
                null, System.Globalization.DateTimeStyles.RoundtripKind);
            return (CurrentBootTime - stored).Duration() > TimeSpan.FromMinutes(2);
        }
        catch
        {
            return false;
        }
    }

    static void WriteBootMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BootMarkerPath)!);
            File.WriteAllText(BootMarkerPath, CurrentBootTime.ToString("o"));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not write boot marker: {ex.Message}");
        }
    }

    public void Add(double seconds, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (gate)
        {
            entries.Add(new HistoryEntry(DateTime.Now, seconds, text));
            if (entries.Count > Cap)
                entries.RemoveRange(0, entries.Count - Cap);
            Persist();
        }
    }

    public IReadOnlyList<HistoryEntry> Newest()
    {
        lock (gate)
            return entries.AsEnumerable().Reverse().ToList();
    }

    public void Clear()
    {
        lock (gate)
        {
            entries.Clear();
            Persist();
        }
    }

    void Persist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(entries));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"warning: could not write history: {ex.Message}");
        }
    }
}
