using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Parrot.UI;

enum OverlayState { Hidden, Recording, Transcribing }

/// <summary>
/// Borderless, click-through pill near the bottom of the primary screen —
/// a waveform while recording, a spinner while transcribing. Never takes
/// focus, so it can't steal keystrokes from the field being dictated into.
/// </summary>
sealed class RecordingOverlay : Form
{
    const int BarCount = 6;
    // Per-bar height multiplier — center bars peak higher than edge bars.
    static readonly float[] Envelope = [0.55f, 0.85f, 1f, 1f, 0.85f, 0.55f];

    static readonly Color Background = Color.FromArgb(16, 18, 18);
    static readonly Color BarColor = Color.FromArgb(181, 209, 255);

    readonly System.Windows.Forms.Timer animation;
    readonly Random jitter = new();
    readonly float[] levels = new float[BarCount];
    OverlayState state = OverlayState.Hidden;
    float spinnerAngle;
    float pendingLevel;

    public RecordingOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(96, 44);
        BackColor = Background;
        DoubleBuffered = true;

        using var path = CapsulePath(ClientRectangle);
        Region = new Region(path);

        animation = new System.Windows.Forms.Timer { Interval = 33 };
        animation.Tick += (_, _) => Animate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_TRANSPARENT = 0x20;
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Show(OverlayState newState)
    {
        state = newState;
        if (newState == OverlayState.Recording)
            Array.Clear(levels);

        if (!Visible)
        {
            var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
            Location = new Point(
                area.Left + (area.Width - Width) / 2,
                area.Bottom - Height - 32);
            // Show without activating: Visible=true on a WS_EX_NOACTIVATE window.
            Visible = true;
        }
        animation.Start();
        Invalidate();
    }

    public new void Hide()
    {
        state = OverlayState.Hidden;
        animation.Stop();
        Visible = false;
    }

    /// <summary>Push a new audio level (0…~1). Safe to call from any thread.</summary>
    public void PushLevel(float level)
    {
        if (IsDisposed) return;
        pendingLevel = level;
    }

    void Animate()
    {
        if (state == OverlayState.Recording)
        {
            // Shape the RMS like the original: sqrt curve, gain, per-bar
            // envelope with a little jitter so bars don't move in lockstep.
            float shaped = Math.Min(1f, MathF.Sqrt(Math.Max(0, pendingLevel)) * 3.4f);
            for (int i = 0; i < BarCount; i++)
            {
                float target = shaped * Envelope[i] * (0.78f + 0.22f * (float)jitter.NextDouble());
                levels[i] += (target - levels[i]) * 0.5f;
            }
        }
        else if (state == OverlayState.Transcribing)
        {
            spinnerAngle = (spinnerAngle + 12f) % 360f;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Background);

        if (state == OverlayState.Transcribing)
        {
            using var pen = new Pen(BarColor, 2.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var rect = new RectangleF(Width / 2f - 8, Height / 2f - 8, 16, 16);
            g.DrawArc(pen, rect, spinnerAngle, 270);
            return;
        }

        // Waveform: 6 slim capsules, scaled vertically by level.
        const float barWidth = 2.5f;
        const float spacing = 4f;
        const float maxHeight = 22f;
        float totalWidth = BarCount * barWidth + (BarCount - 1) * spacing;
        float x = (Width - totalWidth) / 2f;
        float centerY = Height / 2f;

        using var brush = new SolidBrush(BarColor);
        for (int i = 0; i < BarCount; i++)
        {
            float h = Math.Max(0.10f, levels[i]) * maxHeight;
            var bar = new RectangleF(x, centerY - h / 2f, barWidth, h);
            using var path = CapsulePath(Rectangle.Round(bar));
            g.FillPath(brush, path);
            x += barWidth + spacing;
        }
    }

    /// <summary>Rounded rectangle with fully round short ends — a capsule in
    /// either orientation.</summary>
    static GraphicsPath CapsulePath(Rectangle rect)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) animation.Dispose();
        base.Dispose(disposing);
    }
}
