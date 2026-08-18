using System.Runtime.InteropServices;
using Parrot.Platform;

namespace Parrot.Input;

/// <summary>
/// Reads the push-to-talk key straight from the kernel's input layer
/// (/dev/input/event*). This is the one mechanism that works identically on
/// X11 and Wayland — Wayland deliberately gives an application no way to see
/// keys meant for another window, so a global hotkey has to come from below
/// the display server.
///
/// The price is a permission: input devices are mode 0660 root:input, so the
/// user must be in the "input" group. <see cref="DescribePermissionProblem"/>
/// turns that failure into instructions instead of an errno.
/// </summary>
sealed class EvdevHotkeyMonitor : IHotkeyMonitor
{
    const int O_RDONLY = 0;
    const int O_NONBLOCK = 0x800;
    const short POLLIN = 0x001;
    const int EV_KEY = 1;
    const int EINTR = 4;

    // struct input_event { struct timeval time; __u16 type, code; __s32 value; }
    // 16 bytes of timeval on 64-bit, then 2 + 2 + 4.
    const int EventSize = 24;

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern nint read(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true)]
    static extern int ioctl(int fd, nuint request, byte[] buffer);

    [StructLayout(LayoutKind.Sequential)]
    struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int poll([In, Out] PollFd[] fds, uint nfds, int timeout);

    // _IOC(dir, type, nr, size) = (dir << 30) | (size << 16) | (type << 8) | nr,
    // with _IOC_READ = 2 and type 'E' for the evdev family.
    static nuint Ioc(uint nr, int size) =>
        (nuint)((2u << 30) | ((uint)size << 16) | ((uint)'E' << 8) | nr);

    /// <summary>EVIOCGBIT(ev, len) — asks a device which keys it can produce.</summary>
    static nuint EviocgBit(int ev, int len) => Ioc((uint)(0x20 + ev), len);

    /// <summary>EVIOCGNAME(len) — the device's own name, for debug output.</summary>
    static nuint EviocgName(int len) => Ioc(0x06, len);

    public event Action<HotkeyEventKind>? OnEvent;

    public string Mechanism => "evdev";

    readonly int keyCode;
    readonly bool debug;
    readonly List<(int Fd, string Name)> devices = new();
    Thread? worker;
    volatile bool running;
    bool isPressed;

    public EvdevHotkeyMonitor(string hotkeyName, bool debug = false)
    {
        keyCode = LinuxKeys.Evdev(hotkeyName)
            ?? throw new ArgumentException($"unknown hotkey: {hotkeyName}");
        this.debug = debug;
    }

    public void Start()
    {
        if (running) return;

        bool sawDevice = false;
        foreach (string path in Directory.EnumerateFiles("/dev/input", "event*").OrderBy(p => p))
        {
            sawDevice = true;
            int fd = open(path, O_RDONLY | O_NONBLOCK);
            if (fd < 0) continue;                    // not ours to read; skip quietly

            if (!ProducesKey(fd, keyCode))
            {
                close(fd);
                continue;
            }
            devices.Add((fd, $"{path} ({DeviceName(fd)})"));
        }

        if (devices.Count == 0)
        {
            foreach (var (fd, _) in devices) close(fd);
            devices.Clear();
            throw new InvalidOperationException(sawDevice
                ? DescribePermissionProblem()
                : "geen invoerapparaten onder /dev/input — draait dit in een container zonder toegang tot de host?");
        }

        if (debug)
            foreach (var (_, name) in devices)
                Console.Error.WriteLine($"  [debug] watching {name}");

        running = true;
        worker = new Thread(Loop) { IsBackground = true, Name = "papegaai-evdev" };
        worker.Start();
    }

    /// <summary>Ask a device whether it can emit our key. Keyboards can; mice,
    /// touchpads and power buttons cannot. That both finds the right devices
    /// and covers having two keyboards attached — a laptop's built-in one and
    /// a USB one are watched together.</summary>
    static bool ProducesKey(int fd, int code)
    {
        const int maxKey = 0x2FF;
        var bits = new byte[(maxKey + 7) / 8];
        if (ioctl(fd, EviocgBit(EV_KEY, bits.Length), bits) < 0) return false;
        int index = code / 8, bit = code % 8;
        return index < bits.Length && (bits[index] & (1 << bit)) != 0;
    }

    static string DeviceName(int fd)
    {
        var buffer = new byte[256];
        int rc = ioctl(fd, EviocgName(buffer.Length), buffer);
        if (rc < 0) return "?";
        int end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.UTF8.GetString(buffer, 0, end < 0 ? rc : end);
    }

    void Loop()
    {
        var fds = devices.Select(d => new PollFd { fd = d.Fd, events = POLLIN }).ToArray();
        var buffer = new byte[EventSize * 64];

        while (running)
        {
            // A 200 ms timeout keeps shutdown snappy without busy-waiting.
            int ready = poll(fds, (uint)fds.Length, 200);
            if (ready < 0)
            {
                if (Marshal.GetLastWin32Error() == EINTR) continue;
                if (running) Console.Error.WriteLine("hotkey: poll failed — stopping");
                return;
            }
            if (ready == 0) continue;

            for (int i = 0; i < fds.Length; i++)
            {
                if ((fds[i].revents & POLLIN) == 0) continue;

                nint got;
                while ((got = read(fds[i].fd, buffer, buffer.Length)) > 0)
                {
                    for (int offset = 0; offset + EventSize <= got; offset += EventSize)
                        Handle(buffer, offset);
                }
            }
        }
    }

    void Handle(byte[] buffer, int offset)
    {
        ushort type = BitConverter.ToUInt16(buffer, offset + 16);
        ushort code = BitConverter.ToUInt16(buffer, offset + 18);
        int value = BitConverter.ToInt32(buffer, offset + 20);

        if (debug && type == EV_KEY)
            Console.Error.WriteLine($"  [debug] key code={code} value={value}");

        if (type != EV_KEY || code != keyCode) return;

        // value: 0 = release, 1 = press, 2 = auto-repeat while held.
        if (value == 1 && !isPressed)
        {
            isPressed = true;
            OnEvent?.Invoke(HotkeyEventKind.Pressed);
        }
        else if (value == 0 && isPressed)
        {
            isPressed = false;
            OnEvent?.Invoke(HotkeyEventKind.Released);
        }
    }

    public void Stop()
    {
        running = false;
        try { worker?.Join(500); } catch { }
        worker = null;
        foreach (var (fd, _) in devices) close(fd);
        devices.Clear();
        isPressed = false;
    }

    /// <summary>True when at least one input device can actually be opened —
    /// what `doctor` checks before blaming the hardware.</summary>
    public static bool CanReadInputDevices()
    {
        if (!Directory.Exists("/dev/input")) return false;
        foreach (string path in Directory.EnumerateFiles("/dev/input", "event*"))
        {
            int fd = open(path, O_RDONLY | O_NONBLOCK);
            if (fd >= 0)
            {
                close(fd);
                return true;
            }
        }
        return false;
    }

    public static string DescribePermissionProblem() =>
        "geen toetsenbord gevonden dat PapegaAI mag uitlezen.\n\n" +
        "Linux geeft /dev/input/event* alleen aan de groep 'input'. Voeg jezelf toe en log\n" +
        "daarna opnieuw in (uitloggen/inloggen is genoeg, herstarten mag ook):\n\n" +
        "    sudo usermod -aG input $USER\n\n" +
        "Controleer daarna met:  ls -l /dev/input/event*   (de groep moet 'input' zijn)";

    public void Dispose() => Stop();
}
