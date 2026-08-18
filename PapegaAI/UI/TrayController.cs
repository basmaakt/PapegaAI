using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Parrot.UI;

/// <summary>
/// Notification-area icon — the Windows counterpart of the macOS menu-bar
/// item. Shows recording state at a glance and provides the persistent
/// control surface for the daemon. The icon is drawn in code so the
/// executable stays a true single binary, like the original's inlined SVG.
/// </summary>
sealed class TrayController : IDisposable
{
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem stateLabel;
    readonly ToolStripMenuItem modelLabel;
    readonly Icon idleIcon;
    readonly Icon recordingIcon;
    string idleText;
    bool recording;

    public TrayController(string modelId, string runtime, string hotkeyName, Action onQuit, Action onOpenSettings)
    {
        idleText = IdleText(hotkeyName);
        idleIcon = DrawIcon(recording: false);
        recordingIcon = DrawIcon(recording: true);

        var menu = new ContextMenuStrip();
        stateLabel = new ToolStripMenuItem(idleText) { Enabled = false };
        menu.Items.Add(stateLabel);
        modelLabel = new ToolStripMenuItem($"model: {modelId} · runtime: {runtime}") { Enabled = false };
        menu.Items.Add(modelLabel);
        menu.Items.Add(new ToolStripSeparator());
        var settings = new ToolStripMenuItem("Instellingen…");
        settings.Click += (_, _) => onOpenSettings();
        menu.Items.Add(settings);
        menu.Items.Add(new ToolStripSeparator());
        var quit = new ToolStripMenuItem("PapegaAI afsluiten");
        quit.Click += (_, _) => onQuit();
        menu.Items.Add(quit);

        icon = new NotifyIcon
        {
            Icon = idleIcon,
            Text = "PapegaAI",
            ContextMenuStrip = menu,
            Visible = true,
        };
        icon.DoubleClick += (_, _) => onOpenSettings();
    }

    static string IdleText(string hotkeyName) =>
        $"inactief · houd {hotkeyName} ingedrukt om te dicteren";

    public void SetRecording(bool nowRecording)
    {
        recording = nowRecording;
        stateLabel.Text = nowRecording ? "● opnemen" : idleText;
        icon.Icon = nowRecording ? recordingIcon : idleIcon;
    }

    public void SetTranscribing()
    {
        stateLabel.Text = "transcriberen…";
    }

    public void Notify(string text) =>
        icon.ShowBalloonTip(4000, "PapegaAI", text, ToolTipIcon.Warning);

    public void UpdateHotkey(string hotkeyName)
    {
        idleText = IdleText(hotkeyName);
        if (!recording) stateLabel.Text = idleText;
    }

    /// <summary>A macaw-head profile in full colour: red head, white cheek
    /// patch with a dark eye, ivory hooked beak, yellow-and-blue chest.
    /// White-ringed red dot in the corner while recording. Drawn on a 32-unit
    /// grid, scaled to any size — the settings window shows a big one.</summary>
    internal static Bitmap DrawBitmap(int size, bool recording)
    {
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.ScaleTransform(size / 32f, size / 32f);
            using var redHead = new SolidBrush(Color.FromArgb(225, 60, 50));
            using var ivory = new SolidBrush(Color.FromArgb(235, 222, 200));
            using var yellow = new SolidBrush(Color.FromArgb(250, 190, 60));
            using var blue = new SolidBrush(Color.FromArgb(70, 130, 220));
            using var white = new SolidBrush(Color.White);
            using var dark = new SolidBrush(Color.FromArgb(35, 30, 30));

            // Kop
            g.FillEllipse(redHead, 11, 2, 19, 19);

            // Haaksnavel: buitenrand voorhoofd → haakpunt, binnenrand terug naar de wang
            using (var beak = new GraphicsPath())
            {
                beak.AddBezier(16, 3, 6, 2, 0, 9, 3, 15);
                beak.AddBezier(3, 15, 3, 20, 6, 24, 11, 26);
                beak.AddBezier(11, 26, 8, 21, 10, 16, 14, 13);
                beak.CloseFigure();
                g.FillPath(ivory, beak);
            }

            // Borst: geel met blauwe onderkant, los van de snavel ('kin'-inkeping)
            using (var chest = new GraphicsPath())
            {
                chest.AddBezier(17, 18, 15, 24, 16, 29, 20, 31);
                chest.AddLine(20, 31, 28, 31);
                chest.AddBezier(28, 31, 29, 25, 29, 21, 27, 17);
                chest.CloseFigure();
                g.FillPath(yellow, chest);
                g.SetClip(chest);
                g.FillRectangle(blue, 0, 25, 32, 7);
                g.ResetClip();
            }

            // Witte wangvlek + donker oog (ara's hebben een kaal wit 'gezicht')
            g.FillEllipse(white, 14.5f, 4.5f, 9.5f, 9.5f);
            g.FillEllipse(dark, 17.5f, 7.5f, 3.5f, 3.5f);

            if (recording)
            {
                using var red = new SolidBrush(Color.FromArgb(235, 68, 60));
                g.FillEllipse(white, 19, 19, 13, 13);
                g.FillEllipse(red, 20.5f, 20.5f, 10f, 10f);
            }
        }
        return bmp;
    }

    internal static Icon DrawIcon(bool recording)
    {
        using var bmp = DrawBitmap(32, recording);
        nint h = bmp.GetHicon();
        using var tmp = Icon.FromHandle(h);
        var result = (Icon)tmp.Clone();
        DestroyIcon(h);
        return result;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool DestroyIcon(nint handle);

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
        idleIcon.Dispose();
        recordingIcon.Dispose();
    }
}
