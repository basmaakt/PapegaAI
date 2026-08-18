using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Parrot.Platform;

namespace Parrot.UI;

enum OverlayState { Hidden, Recording, Transcribing }

/// <summary>
/// The recording pill: a small borderless capsule near the bottom of the
/// screen, showing a live waveform while the mic is hot and a spinner while
/// the model works. Never takes focus, so it cannot steal keystrokes from the
/// field being dictated into.
/// </summary>
sealed class OverlayWindow : Window
{
    readonly PillView pill = new();

    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Width = 96;
        Height = 44;
        Focusable = false;
        Content = pill;
    }

    public void SetState(OverlayState state)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (state == OverlayState.Hidden)
            {
                pill.SetState(state);
                Hide();
                return;
            }

            pill.SetState(state);
            if (!IsVisible)
            {
                PlaceAtBottom();
                Show();
                MakeClickThrough();
            }
        });
    }

    public void PushLevel(float level) => pill.PushLevel(level);

    void PlaceAtBottom()
    {
        var screen = Screens.Primary ?? Screens.All.FirstOrDefault();
        if (screen is null) return;

        var area = screen.WorkingArea;
        double scaling = screen.Scaling;
        int width = (int)(Width * scaling);
        int height = (int)(Height * scaling);
        Position = new PixelPoint(
            area.X + (area.Width - width) / 2,
            area.Y + area.Height - height - (int)(32 * scaling));
    }

    /// <summary>
    /// Ask X to give the window an empty input region, so clicks land on
    /// whatever is behind the pill. Avalonia has no API for this, but the
    /// shape extension does exactly one thing and does it well. Best effort:
    /// on a compositor where this fails the pill is merely clickable.
    /// </summary>
    void MakeClickThrough()
    {
        try
        {
            var handle = TryGetPlatformHandle();
            if (handle is null || handle.HandleDescriptor != "XID") return;

            nint display = XOpenDisplay(null);
            if (display == 0) return;
            try
            {
                // ShapeInput = 2, ShapeSet = 0, Unsorted = 0; zero rectangles
                // means "no part of this window accepts pointer events".
                XShapeCombineRectangles(display, handle.Handle, 2, 0, 0, IntPtr.Zero, 0, 0, 0);
                XFlush(display);
            }
            finally
            {
                XCloseDisplay(display);
            }
        }
        catch
        {
            // No X11, no shape extension — the pill stays clickable, which is
            // cosmetic rather than broken.
        }
    }

    [DllImport("libX11.so.6")]
    static extern nint XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? name);

    [DllImport("libX11.so.6")]
    static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6")]
    static extern int XFlush(nint display);

    [DllImport("libXext.so.6")]
    static extern void XShapeCombineRectangles(nint display, nint window, int destKind,
        int xOffset, int yOffset, nint rectangles, int count, int op, int ordering);
}

/// <summary>The pill's contents — capsule background, six waveform bars while
/// recording, a sweeping arc while transcribing.</summary>
sealed class PillView : Control
{
    const int BarCount = 6;
    // Per-bar height multiplier — centre bars peak higher than edge bars.
    static readonly float[] Envelope = [0.55f, 0.85f, 1f, 1f, 0.85f, 0.55f];

    static readonly IBrush Background = new SolidColorBrush(Color.FromRgb(16, 18, 18));
    static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(181, 209, 255));
    static readonly IPen SpinnerPen = new Pen(
        new SolidColorBrush(Color.FromRgb(181, 209, 255)), 2.5, lineCap: PenLineCap.Round);

    readonly DispatcherTimer animation;
    readonly Random jitter = new();
    readonly float[] levels = new float[BarCount];
    OverlayState state = OverlayState.Hidden;
    double spinnerAngle;
    float pendingLevel;

    public PillView()
    {
        animation = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        animation.Tick += (_, _) => Animate();
    }

    public void SetState(OverlayState newState)
    {
        state = newState;
        if (newState == OverlayState.Recording) Array.Clear(levels);
        if (newState == OverlayState.Hidden) animation.Stop();
        else animation.Start();
        InvalidateVisual();
    }

    /// <summary>Push a new audio level (0…~1). Safe to call from any thread —
    /// the animation timer picks the value up on the UI thread.</summary>
    public void PushLevel(float level) => pendingLevel = level;

    void Animate()
    {
        if (state == OverlayState.Recording)
        {
            // Shape the RMS like the macOS original: sqrt curve, gain, per-bar
            // envelope with a little jitter so bars do not move in lockstep.
            float shaped = Math.Min(1f, MathF.Sqrt(Math.Max(0, pendingLevel)) * 3.4f);
            for (int i = 0; i < BarCount; i++)
            {
                float target = shaped * Envelope[i] * (0.78f + 0.22f * (float)jitter.NextDouble());
                levels[i] += (target - levels[i]) * 0.5f;
            }
        }
        else if (state == OverlayState.Transcribing)
        {
            spinnerAngle = (spinnerAngle + 12) % 360;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        context.DrawRectangle(Background, null, new RoundedRect(
            new Rect(0, 0, w, h), h / 2));

        if (state == OverlayState.Transcribing)
        {
            DrawSpinner(context, w, h);
            return;
        }

        const double barWidth = 2.5, spacing = 4, maxHeight = 22;
        double total = BarCount * barWidth + (BarCount - 1) * spacing;
        double x = (w - total) / 2;
        double centerY = h / 2;

        for (int i = 0; i < BarCount; i++)
        {
            double barHeight = Math.Max(0.10f, levels[i]) * maxHeight;
            context.DrawRectangle(BarBrush, null, new RoundedRect(
                new Rect(x, centerY - barHeight / 2, barWidth, barHeight), barWidth / 2));
            x += barWidth + spacing;
        }
    }

    void DrawSpinner(DrawingContext context, double w, double h)
    {
        const double radius = 8;
        var center = new Point(w / 2, h / 2);

        // Three quarters of a circle, rotating — the same shape the WinForms
        // overlay draws with DrawArc.
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            Point At(double degrees)
            {
                double rad = degrees * Math.PI / 180;
                return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
            }

            ctx.BeginFigure(At(spinnerAngle), isFilled: false);
            ctx.ArcTo(At(spinnerAngle + 270), new Size(radius, radius), 0,
                isLargeArc: true, SweepDirection.Clockwise);
            ctx.EndFigure(false);
        }
        context.DrawGeometry(null, SpinnerPen, geometry);
    }
}
