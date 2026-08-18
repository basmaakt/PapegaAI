namespace Parrot.Platform;

/// <summary>
/// Types the transcript into whatever window has focus. Windows synthesizes
/// unicode key events with SendInput; Linux picks between xdotool, wtype, a
/// uinput virtual keyboard plus clipboard paste, or the clipboard alone.
/// </summary>
public interface ITextInjector
{
    void Inject(string text);

    /// <summary>Short description of the mechanism in use — shown by `doctor`
    /// and the settings window, since on Linux it varies per session.</summary>
    string Mechanism { get; }
}
