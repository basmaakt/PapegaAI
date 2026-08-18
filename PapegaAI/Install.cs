using Microsoft.Win32;
using Parrot.Platform;

namespace Parrot;

/// <summary>
/// Launch-at-login via the per-user Run registry key — the Windows
/// counterpart of the original's LaunchAgent plist. No admin rights needed;
/// `--uninstall` removes the entry again.
/// </summary>
static class Install
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PapegaAI";
    const string LegacyValueName = "parrot"; // pre-rename installs

    static void RemoveLegacy()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(LegacyValueName) is not null)
            key.DeleteValue(LegacyValueName);
    }

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is not null;
    }

    public static void Enable()
    {
        string exe = Environment.ProcessPath
            ?? throw new InvalidOperationException("could not determine the path of this executable");
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{exe}\" run --hidden");
        RemoveLegacy();
    }

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(ValueName) is not null)
            key.DeleteValue(ValueName);
        RemoveLegacy();
    }

    /// <summary>Interface form of the same registry entry, so shared code
    /// can toggle launch-at-login without knowing which OS it is on.</summary>
    public sealed class Adapter : IAutostart
    {
        public bool IsEnabled => Install.IsEnabled();
        public void Enable() => Install.Enable();
        public void Disable() => Install.Disable();
        public string Location => $@"HKCU\{RunKey}\{ValueName}";
    }

    public static int Run(List<string> args)
    {
        if (args.Remove("--uninstall"))
        {
            if (!IsEnabled())
            {
                Console.Error.WriteLine("PapegaAI is not registered to launch at login.");
                return 0;
            }
            Disable();
            Console.Error.WriteLine("✓ removed launch-at-login entry.");
            Console.Error.WriteLine("  (a running PapegaAI keeps running — quit it from the tray icon)");
            return 0;
        }

        if (args.Remove("--launch-at-login"))
        {
            Enable();
            Console.Error.WriteLine($"✓ PapegaAI will launch at login: \"{Environment.ProcessPath}\" run --hidden");
            Console.Error.WriteLine("  remove with: PapegaAI install --uninstall");
            return 0;
        }

        Console.Error.WriteLine("usage: PapegaAI install --launch-at-login | --uninstall");
        return 1;
    }
}
