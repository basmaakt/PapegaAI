using System.Runtime.InteropServices;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// Watches the push-to-talk key through the X11 RECORD extension — the same
/// idea as the Windows low-level keyboard hook, and like it, needing no
/// special permission. Preferred over evdev on an X11 session because the
/// user does not have to join the "input" group first.
///
/// RECORD is a passive tap: it reports keys but cannot swallow them, so a
/// caps-lock hotkey still toggles caps here (on Windows it does not).
/// </summary>
sealed class X11HotkeyMonitor : IHotkeyMonitor
{
    const string LibX11 = "libX11.so.6";
    const string LibXtst = "libXtst.so.6";

    const int KeyPress = 2;
    const int KeyRelease = 3;
    const nuint XRecordAllClients = 3;
    const int XRecordFromServer = 0;
    const short POLLIN = 0x001;

    [DllImport(LibX11)]
    static extern nint XOpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string? name);

    [DllImport(LibX11)]
    static extern int XCloseDisplay(nint display);

    [DllImport(LibX11)]
    static extern int XFlush(nint display);

    [DllImport(LibX11)]
    static extern int XConnectionNumber(nint display);

    [DllImport(LibXtst)]
    static extern nint XRecordAllocRange();

    [DllImport(LibXtst)]
    static extern int XRecordQueryVersion(nint display, out int major, out int minor);

    [DllImport(LibXtst)]
    static extern nuint XRecordCreateContext(nint display, int datumFlags,
        ref nuint clients, int nclients, ref nint ranges, int nranges);

    delegate void InterceptProc(nint closure, nint data);

    [DllImport(LibXtst)]
    static extern int XRecordEnableContextAsync(nint display, nuint context,
        InterceptProc callback, nint closure);

    [DllImport(LibXtst)]
    static extern void XRecordProcessReplies(nint display);

    [DllImport(LibXtst)]
    static extern void XRecordFreeData(nint data);

    [DllImport(LibXtst)]
    static extern int XRecordDisableContext(nint display, nuint context);

    [DllImport(LibXtst)]
    static extern int XRecordFreeContext(nint display, nuint context);

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    /// <summary>Both libraries present and a display we may talk to. Checked
    /// before this backend is chosen, so a Wayland-only box falls through to
    /// evdev instead of throwing.</summary>
    public static bool IsAvailable()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            return false;
        try
        {
            if (!NativeLibrary.TryLoad(LibX11, out _) || !NativeLibrary.TryLoad(LibXtst, out _))
                return false;
        }
        catch
        {
            return false;
        }

        nint display = XOpenDisplay(null);
        if (display == 0) return false;
        bool ok = XRecordQueryVersion(display, out _, out _) != 0;
        XCloseDisplay(display);
        return ok;
    }

    public event Action<HotkeyEventKind>? OnEvent;

    public string Mechanism => "X11 RECORD";

    readonly int keyCode;
    readonly bool debug;
    // Held so the GC cannot collect the delegate while X calls back into it.
    readonly InterceptProc callback;

    nint controlDisplay;
    nint dataDisplay;
    nuint context;
    Thread? worker;
    volatile bool running;
    bool isPressed;

    public X11HotkeyMonitor(string hotkeyName, bool debug = false)
    {
        keyCode = LinuxKeys.X11(hotkeyName)
            ?? throw new ArgumentException($"unknown hotkey: {hotkeyName}");
        this.debug = debug;
        callback = Intercept;
    }

    public void Start()
    {
        if (running) return;

        // Two connections on purpose: RECORD streams events down a dedicated
        // one, while the other stays usable for control requests.
        controlDisplay = XOpenDisplay(null);
        dataDisplay = XOpenDisplay(null);
        if (controlDisplay == 0 || dataDisplay == 0)
        {
            Cleanup();
            throw new InvalidOperationException(
                "kan geen verbinding met de X-server maken (DISPLAY niet gezet?)");
        }

        nint range = XRecordAllocRange();
        if (range == 0)
        {
            Cleanup();
            throw new InvalidOperationException("X RECORD: kon geen range reserveren");
        }

        // XRecordRange.device_events is a {first,last} byte pair at offset 18;
        // asking for KeyPress..KeyRelease keeps the stream to just key events.
        Marshal.WriteByte(range, 18, KeyPress);
        Marshal.WriteByte(range, 19, KeyRelease);

        nuint clients = XRecordAllClients;
        context = XRecordCreateContext(controlDisplay, 0, ref clients, 1, ref range, 1);
        if (context == 0)
        {
            Cleanup();
            throw new InvalidOperationException("X RECORD: kon geen context maken");
        }
        XFlush(controlDisplay);

        if (XRecordEnableContextAsync(dataDisplay, context, callback, 0) == 0)
        {
            Cleanup();
            throw new InvalidOperationException("X RECORD: kon de context niet activeren");
        }

        running = true;
        worker = new Thread(Loop) { IsBackground = true, Name = "papegaai-xrecord" };
        worker.Start();
    }

    void Loop()
    {
        var fds = new[] { new PollFd { fd = XConnectionNumber(dataDisplay), events = POLLIN } };
        while (running)
        {
            int ready = poll(fds, 1, 200);
            if (ready < 0)
            {
                if (Marshal.GetLastWin32Error() == 4) continue;   // EINTR
                if (running) Console.Error.WriteLine("hotkey: poll op de X-verbinding faalde");
                return;
            }
            if (ready == 0) continue;
            XRecordProcessReplies(dataDisplay);
        }
    }

    void Intercept(nint closure, nint data)
    {
        if (data == 0) return;
        try
        {
            // struct XRecordInterceptData { XID id; Time server_time;
            //   unsigned long client_seq; int category; Bool client_swapped;
            //   unsigned char *data; unsigned long data_len; }
            int category = Marshal.ReadInt32(data, 24);
            nint payload = Marshal.ReadIntPtr(data, 32);
            if (category != XRecordFromServer || payload == 0) return;

            // The payload is a wire-format xEvent: type, then the keycode.
            byte type = Marshal.ReadByte(payload, 0);
            byte code = Marshal.ReadByte(payload, 1);

            if (debug && type is KeyPress or KeyRelease)
                Console.Error.WriteLine($"  [debug] x11 type={type} keycode={code}");

            if (code != keyCode) return;

            if (type == KeyPress && !isPressed)
            {
                isPressed = true;
                OnEvent?.Invoke(HotkeyEventKind.Pressed);
            }
            else if (type == KeyRelease && isPressed)
            {
                isPressed = false;
                OnEvent?.Invoke(HotkeyEventKind.Released);
            }
        }
        finally
        {
            XRecordFreeData(data);
        }
    }

    public void Stop()
    {
        if (!running && controlDisplay == 0) return;
        running = false;

        if (context != 0 && controlDisplay != 0)
        {
            XRecordDisableContext(controlDisplay, context);
            XFlush(controlDisplay);
        }

        try { worker?.Join(500); } catch { }
        worker = null;
        Cleanup();
        isPressed = false;
    }

    void Cleanup()
    {
        if (context != 0 && controlDisplay != 0)
        {
            XRecordFreeContext(controlDisplay, context);
            context = 0;
        }
        if (dataDisplay != 0)
        {
            XCloseDisplay(dataDisplay);
            dataDisplay = 0;
        }
        if (controlDisplay != 0)
        {
            XCloseDisplay(controlDisplay);
            controlDisplay = 0;
        }
    }

    public void Dispose() => Stop();
}
