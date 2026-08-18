using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace Parrot.UI;

/// <summary>
/// The Avalonia application. PapegaAI has no main window — the tray icon and
/// the pill are the whole interface — so the lifetime is told to keep running
/// until the daemon explicitly shuts it down.
/// </summary>
sealed class App : Application
{
    /// <summary>Handed over by the CLI before Avalonia starts; the framework
    /// constructs this class itself, so there is no constructor to pass to.</summary>
    public static DaemonOptions? Options { get; set; }

    public static LinuxDaemon? Daemon { get; private set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                Daemon = new LinuxDaemon(Options!, desktop);
            }
            catch (Exception ex)
            {
                Program.Fatal(ex.Message);
                desktop.Shutdown(1);
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
