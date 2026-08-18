using Avalonia.Controls;
using Avalonia.Threading;
using Parrot.Input;

namespace Parrot.UI;

/// <summary>
/// The status-area icon: PapegaAI's permanent control surface, showing at a
/// glance whether the mic is hot.
///
/// Linux delivers this over DBus (StatusNotifierItem). KDE, XFCE, Cinnamon and
/// the tiling compositors show it out of the box; GNOME dropped tray support
/// years ago and needs the AppIndicator extension, so the daemon warns about
/// that rather than appearing to have failed to start.
/// </summary>
sealed class TrayController : IDisposable
{
    readonly TrayIcon icon;
    readonly NativeMenuItem stateLabel;
    readonly NativeMenuItem modelLabel;
    string idleText;
    bool recording;

    public TrayController(string modelId, string runtime, string hotkeyName,
        Action onQuit, Action onOpenSettings)
    {
        idleText = IdleText(hotkeyName);

        stateLabel = new NativeMenuItem(idleText) { IsEnabled = false };
        modelLabel = new NativeMenuItem($"model: {modelId} · runtime: {runtime}") { IsEnabled = false };

        var settings = new NativeMenuItem("Instellingen…");
        settings.Click += (_, _) => onOpenSettings();

        var quit = new NativeMenuItem("PapegaAI afsluiten");
        quit.Click += (_, _) => onQuit();

        var menu = new NativeMenu();
        menu.Items.Add(stateLabel);
        menu.Items.Add(modelLabel);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quit);

        icon = new TrayIcon
        {
            Icon = new WindowIcon(Icons.Idle),
            ToolTipText = "PapegaAI",
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => onOpenSettings();
    }

    static string IdleText(string hotkeyName) =>
        $"inactief · houd {hotkeyName} ingedrukt om te dicteren";

    public void SetRecording(bool nowRecording) => Dispatcher.UIThread.Post(() =>
    {
        recording = nowRecording;
        stateLabel.Header = nowRecording ? "● opnemen" : idleText;
        icon.Icon = new WindowIcon(Icons.For(nowRecording));
    });

    public void SetTranscribing() => Dispatcher.UIThread.Post(() =>
        stateLabel.Header = "transcriberen…");

    public void UpdateHotkey(string hotkeyName) => Dispatcher.UIThread.Post(() =>
    {
        idleText = IdleText(hotkeyName);
        if (!recording) stateLabel.Header = idleText;
    });

    /// <summary>A passing message — the desktop's own notification daemon,
    /// since a status icon has no balloon tips on Linux.</summary>
    public void Notify(string text) => Parrot.Input.Notify.Send("PapegaAI", text);

    public void Dispose()
    {
        icon.IsVisible = false;
        icon.Dispose();
    }
}
