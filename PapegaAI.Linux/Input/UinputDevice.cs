using System.Runtime.InteropServices;

namespace Parrot.Input;

/// <summary>
/// A virtual keyboard registered with the kernel through /dev/uinput. Keys it
/// emits are indistinguishable from a real keyboard's, so they reach any
/// window on any compositor — the only injection route that works on GNOME
/// under Wayland, where an application is otherwise forbidden to type into
/// someone else's window.
///
/// It can only press keys, not "type unicode" (the character a keycode
/// produces depends on the user's layout, which lives in the compositor), so
/// callers pair it with the clipboard: put the text there, then press Ctrl+V.
///
/// Needs write access to /dev/uinput — see <see cref="DescribePermissionProblem"/>.
/// </summary>
sealed class UinputDevice : IDisposable
{
    const string Node = "/dev/uinput";
    const int O_WRONLY = 1;
    const int O_NONBLOCK = 0x800;

    const int EV_SYN = 0;
    const int EV_KEY = 1;
    const int SYN_REPORT = 0;

    public const int KEY_LEFTCTRL = 29;
    public const int KEY_LEFTSHIFT = 42;
    public const int KEY_V = 47;

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern nint write(int fd, byte[] buffer, nint count);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    static extern int ioctl_int(int fd, nuint request, int value);

    [DllImport("libc", SetLastError = true, EntryPoint = "ioctl")]
    static extern int ioctl_buf(int fd, nuint request, byte[] value);

    // _IO('U', nr) and _IOW('U', nr, size), the uinput ioctl family.
    static nuint Io(uint nr) => (nuint)(((uint)'U' << 8) | nr);
    static nuint Iow(uint nr, int size) =>
        (nuint)((1u << 30) | ((uint)size << 16) | ((uint)'U' << 8) | nr);

    static nuint UI_DEV_CREATE => Io(1);
    static nuint UI_DEV_DESTROY => Io(2);
    static nuint UI_DEV_SETUP => Iow(3, 92);    // sizeof(struct uinput_setup)
    static nuint UI_SET_EVBIT => Iow(100, 4);
    static nuint UI_SET_KEYBIT => Iow(101, 4);

    int fd = -1;

    public static bool IsAvailable()
    {
        if (!File.Exists(Node)) return false;
        int probe = open(Node, O_WRONLY | O_NONBLOCK);
        if (probe < 0) return false;
        close(probe);
        return true;
    }

    /// <param name="keys">Every key this device will ever press. The kernel
    /// rejects events for keys not declared up front.</param>
    public UinputDevice(IEnumerable<int> keys)
    {
        fd = open(Node, O_WRONLY | O_NONBLOCK);
        if (fd < 0)
            throw new InvalidOperationException(DescribePermissionProblem());

        if (ioctl_int(fd, UI_SET_EVBIT, EV_KEY) < 0)
            throw Fail("kon EV_KEY niet inschakelen");

        foreach (int key in keys)
            if (ioctl_int(fd, UI_SET_KEYBIT, key) < 0)
                throw Fail($"kon toets {key} niet registreren");

        // struct uinput_setup { struct input_id id; char name[80]; __u32 ff; }
        var setup = new byte[92];
        BitConverter.GetBytes((ushort)0x06).CopyTo(setup, 0);   // BUS_VIRTUAL
        BitConverter.GetBytes((ushort)0x1209).CopyTo(setup, 2); // generic vendor
        BitConverter.GetBytes((ushort)0x0001).CopyTo(setup, 4);
        BitConverter.GetBytes((ushort)0x0001).CopyTo(setup, 6);
        byte[] name = System.Text.Encoding.UTF8.GetBytes("PapegaAI virtual keyboard");
        Array.Copy(name, 0, setup, 8, Math.Min(name.Length, 79));

        if (ioctl_buf(fd, UI_DEV_SETUP, setup) < 0)
            throw Fail("UI_DEV_SETUP werd geweigerd");
        if (ioctl_int(fd, UI_DEV_CREATE, 0) < 0)
            throw Fail("UI_DEV_CREATE werd geweigerd");

        // udev and the compositor need a moment to notice a new keyboard;
        // typing before they do simply goes nowhere. Paid once, at startup.
        Thread.Sleep(300);
    }

    InvalidOperationException Fail(string what)
    {
        int err = Marshal.GetLastWin32Error();
        Dispose();
        return new InvalidOperationException($"/dev/uinput: {what} (errno {err})");
    }

    /// <summary>Press and release a key while the given modifiers are held.</summary>
    public void Tap(int key, params int[] modifiers)
    {
        foreach (int m in modifiers) Emit(EV_KEY, m, 1);
        Sync();
        Emit(EV_KEY, key, 1);
        Sync();
        Thread.Sleep(12);
        Emit(EV_KEY, key, 0);
        Sync();
        foreach (int m in modifiers.Reverse()) Emit(EV_KEY, m, 0);
        Sync();
    }

    void Sync()
    {
        Emit(EV_SYN, SYN_REPORT, 0);
        Thread.Sleep(2);
    }

    void Emit(int type, int code, int value)
    {
        // struct input_event { struct timeval time; __u16 type, code; __s32 value; }
        // A zeroed timestamp tells the kernel to stamp it for us.
        var ev = new byte[24];
        BitConverter.GetBytes((ushort)type).CopyTo(ev, 16);
        BitConverter.GetBytes((ushort)code).CopyTo(ev, 18);
        BitConverter.GetBytes(value).CopyTo(ev, 20);
        if (write(fd, ev, ev.Length) < 0)
            throw new IOException($"/dev/uinput: schrijven mislukt (errno {Marshal.GetLastWin32Error()})");
    }

    public static string DescribePermissionProblem() =>
        "geen schrijfrechten op /dev/uinput.\n\n" +
        "Laad de module en geef de groep 'input' toegang (eenmalig, als root):\n\n" +
        "    sudo modprobe uinput\n" +
        "    echo uinput | sudo tee /etc/modules-load.d/uinput.conf\n" +
        "    echo 'KERNEL==\"uinput\", GROUP=\"input\", MODE=\"0660\", OPTIONS+=\"static_node=uinput\"' \\\n" +
        "        | sudo tee /etc/udev/rules.d/99-papegaai-uinput.rules\n" +
        "    sudo udevadm control --reload-rules && sudo udevadm trigger\n\n" +
        "Zorg ook dat je in de groep 'input' zit:  sudo usermod -aG input $USER\n" +
        "(install.sh doet dit allemaal voor je)";

    public void Dispose()
    {
        if (fd >= 0)
        {
            ioctl_int(fd, UI_DEV_DESTROY, 0);
            close(fd);
            fd = -1;
        }
    }
}
