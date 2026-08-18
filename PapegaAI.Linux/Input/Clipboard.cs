using System.Diagnostics;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// The clipboard, through whichever helper this desktop ships: wl-clipboard on
/// Wayland, xclip or xsel on X11. Needed by the paste-based injection paths,
/// which put the transcript on the clipboard and then synthesise Ctrl+V.
/// </summary>
static class Clipboard
{
    /// <summary>The copy/paste tool pair for this session, or null when none
    /// is installed.</summary>
    public static (string Copy, string[] CopyArgs, string Paste, string[] PasteArgs)? Tools
    {
        get
        {
            if (LinuxSession.IsWayland && Which.Exists("wl-copy") && Which.Exists("wl-paste"))
                return ("wl-copy", ["--type", "text/plain"], "wl-paste", ["--no-newline"]);
            if (Which.Exists("xclip"))
                return ("xclip", ["-selection", "clipboard", "-in"],
                        "xclip", ["-selection", "clipboard", "-out"]);
            if (Which.Exists("xsel"))
                return ("xsel", ["--clipboard", "--input"], "xsel", ["--clipboard", "--output"]);
            // wl-clipboard also works under XWayland-less setups where only it
            // is installed; try it last regardless of session type.
            if (Which.Exists("wl-copy") && Which.Exists("wl-paste"))
                return ("wl-copy", ["--type", "text/plain"], "wl-paste", ["--no-newline"]);
            return null;
        }
    }

    public static bool IsAvailable => Tools is not null;

    public static string? ToolName => Tools?.Copy;

    public static bool Write(string text)
    {
        if (Tools is not { } t) return false;
        try
        {
            var psi = new ProcessStartInfo(t.Copy)
            {
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in t.CopyArgs) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return false;
            p.StandardInput.Write(text);
            p.StandardInput.Close();
            // wl-copy forks a helper that stays alive to serve the selection,
            // so the parent exiting is the signal that the copy landed.
            return p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"clipboard write failed: {ex.Message}");
            return false;
        }
    }

    public static string? Read()
    {
        if (Tools is not { } t) return null;
        try
        {
            var psi = new ProcessStartInfo(t.Paste)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in t.PasteArgs) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p is null) return null;
            string output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(3000) && p.ExitCode == 0 ? output : null;
        }
        catch
        {
            // An empty clipboard makes some tools exit non-zero; nothing to
            // restore then, which is the same as "no previous contents".
            return null;
        }
    }
}
