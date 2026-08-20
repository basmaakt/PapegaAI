using System.Reflection;

namespace Parrot;

/// <summary>
/// Where a crash goes when nobody is watching the terminal.
///
/// The daemon normally starts from the autostart entry, so an exception that
/// reaches Main writes its message to a stream no one reads and the process is
/// gone. All that reaches the user is a tray icon that disappeared. Keeping the
/// whole exception — type, stack, inner exceptions — makes the difference
/// between "hij viel ineens weg" and a bug someone can actually find.
/// </summary>
static class CrashLog
{
    /// <summary>Trim the file once it passes this; old crashes stop being
    /// useful long before they stop taking up room.</summary>
    const long MaxBytes = 64 * 1024;

    public static string Path => System.IO.Path.Combine(Paths.DataDir, "crash.log");

    /// <summary>
    /// Append one crash and return a line naming the file, for the message the
    /// user sees. Returns an empty string when the log could not be written:
    /// failing to record an error must never replace the error itself.
    /// </summary>
    public static string Record(Exception ex)
    {
        try
        {
            Paths.EnsureDir(Paths.DataDir);
            string path = Path;
            Trim(path);

            string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            File.AppendAllText(path,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} · PapegaAI {version} · " +
                $"{Platform.LinuxSession.Describe()} ---\n{ex}\n\n");

            return $"\ndetails staan in {path}";
        }
        catch
        {
            return "";
        }
    }

    static void Trim(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length <= MaxBytes) return;

        string[] lines = File.ReadAllLines(path);
        File.WriteAllLines(path, lines.Skip(lines.Length / 2));
    }
}
