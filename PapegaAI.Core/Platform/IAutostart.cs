namespace Parrot.Platform;

/// <summary>
/// Launch at login: an HKCU Run value on Windows, an XDG autostart desktop
/// entry on Linux.
/// </summary>
public interface IAutostart
{
    bool IsEnabled { get; }
    void Enable();
    void Disable();

    /// <summary>Where the entry lives, for `install` output and doctor.</summary>
    string Location { get; }
}
