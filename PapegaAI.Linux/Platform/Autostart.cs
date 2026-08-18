namespace Parrot.Platform;

/// <summary>
/// Launch at login through an XDG autostart entry — the Linux counterpart of
/// the Windows Run registry key and the macOS LaunchAgent. A desktop file in
/// ~/.config/autostart is honoured by GNOME, KDE, XFCE, Cinnamon and the
/// tiling compositors alike, needs no root, and is easy to inspect.
/// </summary>
sealed class Autostart : IAutostart
{
    const string FileName = "papegaai.desktop";

    static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "autostart");

    static string File_ => Path.Combine(Dir, FileName);

    public string Location => File_;

    public bool IsEnabled => File.Exists(File_);

    public void Enable()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("kon het pad van dit programma niet bepalen");

        Directory.CreateDirectory(Dir);
        File.WriteAllText(File_, $"""
            [Desktop Entry]
            Type=Application
            Name=PapegaAI
            Comment=Dicteren met een druk op de knop
            Exec="{exe}" run
            Icon=papegaai
            Terminal=false
            Categories=Utility;AudioVideo;
            X-GNOME-Autostart-enabled=true

            """);
    }

    public void Disable()
    {
        if (File.Exists(File_)) File.Delete(File_);
    }

    public static int RunCli(List<string> args)
    {
        var autostart = new Autostart();

        if (args.Remove("--uninstall"))
        {
            if (!autostart.IsEnabled)
            {
                Console.Error.WriteLine("PapegaAI start nu al niet automatisch op.");
                return 0;
            }
            autostart.Disable();
            Console.Error.WriteLine($"✓ {autostart.Location} verwijderd.");
            Console.Error.WriteLine("  (een draaiende PapegaAI blijft draaien — sluit die via het tray-icoon)");
            return 0;
        }

        if (args.Remove("--launch-at-login"))
        {
            autostart.Enable();
            Console.Error.WriteLine($"✓ PapegaAI start voortaan bij het inloggen: {autostart.Location}");
            Console.Error.WriteLine("  verwijderen met: papegaai install --uninstall");
            return 0;
        }

        Console.Error.WriteLine("gebruik: papegaai install --launch-at-login | --uninstall");
        return 1;
    }
}
