using System.Runtime.InteropServices;

namespace Parrot.Platform;

/// <summary>
/// Only one daemon at a time: two would both see the hotkey and type the
/// transcript twice. Windows uses a named mutex, but .NET's named mutexes are
/// process-local on Unix, so this takes an advisory flock on a file in the
/// session's runtime directory — released by the kernel even if the process
/// is killed, which a lock file with a PID in it could never promise.
/// </summary>
sealed class SingleInstance : IDisposable
{
    const int O_RDWR = 2;
    const int O_CREAT = 0x40;
    // Without this the lock descriptor is inherited by every helper the
    // daemon starts. xclip is the one that bites: it stays alive in the
    // background to serve the clipboard selection, so it would keep the
    // lock long after PapegaAI itself has gone — and the next start, or a
    // restart after a settings change, would refuse with "draait al".
    const int O_CLOEXEC = 0x80000;
    const int LOCK_EX = 2;
    const int LOCK_NB = 4;
    const int LOCK_UN = 8;

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int flock(int fd, int operation);

    int fd = -1;

    public string Path { get; }

    SingleInstance(int fd, string path)
    {
        this.fd = fd;
        Path = path;
    }

    /// <summary>Take the lock, or return null when another daemon holds it.</summary>
    /// <param name="waitMilliseconds">Grace period so a self-restart (after a
    /// model change) can hand over while the old process is still exiting.</param>
    public static SingleInstance? TryAcquire(int waitMilliseconds = 5000)
    {
        string path = System.IO.Path.Combine(Paths.RuntimeDir, "papegaai.lock");

        int fd = open(path, O_RDWR | O_CREAT | O_CLOEXEC, Convert.ToInt32("600", 8));
        if (fd < 0)
        {
            // Without a lock file we cannot tell; better to run than to refuse.
            Console.Error.WriteLine($"warning: kon {path} niet openen — sla de instantie-controle over");
            return new SingleInstance(-1, path);
        }

        var deadline = DateTime.UtcNow.AddMilliseconds(waitMilliseconds);
        while (true)
        {
            if (flock(fd, LOCK_EX | LOCK_NB) == 0)
                return new SingleInstance(fd, path);

            if (DateTime.UtcNow >= deadline)
            {
                close(fd);
                return null;
            }
            Thread.Sleep(100);
        }
    }

    public void Dispose()
    {
        if (fd >= 0)
        {
            flock(fd, LOCK_UN);
            close(fd);
            fd = -1;
        }
    }
}
