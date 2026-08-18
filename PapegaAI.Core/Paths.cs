namespace Parrot;

/// <summary>
/// Where PapegaAI keeps its files. Windows puts everything under
/// %LOCALAPPDATA%\PapegaAI (unchanged from before the Linux port, so existing
/// installs keep their config, models and history). Linux follows the XDG
/// spec: settings in ~/.config/PapegaAI, models and history in
/// ~/.local/share/PapegaAI.
/// </summary>
public static class Paths
{
    const string AppFolder = "PapegaAI";

    /// <summary>Settings. Windows: %LOCALAPPDATA%\PapegaAI · Linux: $XDG_CONFIG_HOME/PapegaAI.</summary>
    public static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(OperatingSystem.IsWindows()
            ? Environment.SpecialFolder.LocalApplicationData
            // On Unix .NET maps ApplicationData to $XDG_CONFIG_HOME (~/.config);
            // on Windows it would mean Roaming, which is why this is branched.
            : Environment.SpecialFolder.ApplicationData),
        AppFolder);

    /// <summary>Bulk data — models, history. Windows: %LOCALAPPDATA%\PapegaAI ·
    /// Linux: $XDG_DATA_HOME/PapegaAI.</summary>
    public static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        AppFolder);

    public static string ModelsDir => Path.Combine(DataDir, "models");
    public static string ConfigFile => Path.Combine(ConfigDir, "config.json");
    public static string HistoryFile => Path.Combine(DataDir, "history.json");
    public static string BootMarkerFile => Path.Combine(DataDir, "lastboot.txt");

    /// <summary>Session-scoped scratch for the single-instance lock. Uses
    /// $XDG_RUNTIME_DIR (tmpfs, wiped on logout) when the session provides
    /// one, otherwise the temp directory.</summary>
    public static string RuntimeDir
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            return !string.IsNullOrEmpty(xdg) && Directory.Exists(xdg)
                ? xdg
                : Path.GetTempPath();
        }
    }

    public static string TempFile(string name) => Path.Combine(Path.GetTempPath(), name);

    public static void EnsureDir(string path) => Directory.CreateDirectory(path);
}
