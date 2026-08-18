using System.Diagnostics;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Parrot.Audio;
using Parrot.History;
using Parrot.Input;
using Parrot.Models;
using Parrot.Platform;
using Parrot.Transcription;
using Parrot.UI;

namespace Parrot;

/// <summary>Everything the daemon needs, assembled by the CLI before Avalonia
/// starts up.</summary>
sealed record DaemonOptions(
    WhisperTranscriber Transcriber,
    TranscriptionModel Model,
    string Language,
    Config Config,
    string HotkeyName,
    bool DebugHotkey,
    bool DumpWav,
    bool NoOverlay,
    bool UseGpu);

/// <summary>
/// Wires the daemon together: hotkey → audio capture → transcription → text
/// injection, with the tray icon, overlay and settings window reflecting
/// state. The Linux counterpart of the Windows DaemonContext; the differences
/// are that every backend is chosen at runtime and that injection happens off
/// the UI thread, because a helper process can take a moment.
/// </summary>
sealed class LinuxDaemon : IDisposable
{
    readonly WhisperTranscriber transcriber;
    readonly TranscriptionModel model;
    readonly string language;
    readonly bool debugHotkey;
    readonly bool dumpWav;
    readonly bool useGpu;
    readonly IClassicDesktopStyleApplicationLifetime lifetime;
    readonly IAudioCapture capture;
    readonly TrayController tray;
    readonly HistoryStore history;
    readonly IAutostart autostart = new Autostart();

    LinuxTextInjector injector;
    IHotkeyMonitor monitor;
    OverlayWindow? overlay;
    string hotkeyName;
    Config config;
    SettingsWindow? settingsWindow;

    public LinuxDaemon(DaemonOptions options, IClassicDesktopStyleApplicationLifetime lifetime)
    {
        transcriber = options.Transcriber;
        model = options.Model;
        language = options.Language;
        config = options.Config;
        hotkeyName = options.HotkeyName;
        debugHotkey = options.DebugHotkey;
        dumpWav = options.DumpWav;
        useGpu = options.UseGpu;
        this.lifetime = lifetime;

        history = new HistoryStore(config.ClearHistoryOnReboot ?? false);
        injector = new LinuxTextInjector(config.Injection, config.PasteShortcut);
        if (!injector.HasAnyMethod)
            Console.Error.WriteLine(
                "let op: geen manier gevonden om tekst in te voegen — transcripties komen " +
                "alleen in de geschiedenis terecht. Draai `papegaai doctor` voor de oplossing.");

        overlay = options.NoOverlay ? null : new OverlayWindow();
        capture = LinuxAudio.Create(config.AudioDevice);
        if (overlay is not null)
            capture.OnLevel = level => overlay?.PushLevel(level);

        monitor = HotkeyBackends.Start(hotkeyName, debugHotkey, config.HotkeyBackend);
        monitor.OnEvent += HandleHotkey;

        tray = new TrayController(model.Id, WhisperTranscriber.LoadedRuntime, hotkeyName,
            Quit, () => OpenSettings(showHistory: false), () => OpenSettings(showHistory: true));

        if (LinuxSession.IsGnome)
            Console.Error.WriteLine(
                "let op: GNOME toont standaard geen tray-iconen. Installeer de extensie " +
                "'AppIndicator and KStatusNotifierItem Support' om het papegaai-icoontje te zien.");

        Console.Error.WriteLine(
            $"luistert op {hotkeyName} ({monitor.Mechanism}) · model: {model.Id} · " +
            $"taal: {language} · invoegen: {injector.Mechanism}");
    }

    void HandleHotkey(HotkeyEventKind kind)
    {
        if (kind == HotkeyEventKind.Pressed)
        {
            try
            {
                capture.Start();
                Console.Error.WriteLine("● opnemen");
                overlay?.SetState(OverlayState.Recording);
                tray.SetRecording(true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"opnemen mislukt: {ex.Message}");
                tray.Notify($"Opnemen mislukt: {ex.Message}");
            }
            return;
        }

        float[] samples = capture.Stop();
        overlay?.SetState(OverlayState.Transcribing);
        tray.SetTranscribing();

        double seconds = samples.Length / (double)AudioFormat.SampleRate;
        float rms = Dsp.ComputeRms(samples);
        Console.Error.WriteLine($"○ {seconds:0.00}s opgenomen · rms {rms:0.000}");

        if (dumpWav && samples.Length > 0)
        {
            string path = Paths.TempFile("PapegaAI-last.wav");
            try
            {
                WavWriter.Write(samples, AudioFormat.SampleRate, path);
                Console.Error.WriteLine($"  geschreven: {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  wav schrijven mislukt: {ex.Message}");
            }
        }

        if (samples.Length == 0)
        {
            Idle();
            return;
        }

        // Pure digital silence: the device exists but nothing arrives (muted
        // headset, wireless mic powered off). Whisper hallucinates on silence
        // ("*", phantom subtitles) — warn instead of typing garbage.
        if (rms < 0.0005f)
        {
            Console.Error.WriteLine("  stille opname — niet getranscribeerd");
            tray.Notify("Geen geluid van de microfoon. Staat je headset aan en is de mute-schakelaar uit?");
            Idle();
            return;
        }

        Task.Run(async () =>
        {
            var started = DateTime.UtcNow;
            try
            {
                string text = await transcriber.Transcribe(samples);
                double elapsed = (DateTime.UtcNow - started).TotalSeconds;
                Console.Error.WriteLine($"→ {elapsed:0.00}s · {text}");
                history.Add(seconds, text);

                // Injection runs here, off the UI thread: xdotool and friends
                // are processes, and blocking the dispatcher would freeze the
                // overlay mid-animation.
                injector.Inject(text);

                Dispatcher.UIThread.Post(() =>
                {
                    if (settingsWindow is { } window) window.RefreshHistory();
                });
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"transcriptie mislukt: {ex.Message}");
            }
            finally
            {
                Idle();
            }
        });
    }

    void Idle()
    {
        overlay?.SetState(OverlayState.Hidden);
        tray.SetRecording(false);
    }

    void OpenSettings(bool showHistory) => Dispatcher.UIThread.Post(() =>
    {
        if (settingsWindow is { } existing)
        {
            existing.Activate();
            if (showHistory) existing.ShowHistory();
            return;
        }

        var window = new SettingsWindow(
            EffectiveConfig(), WhisperTranscriber.LoadedRuntime,
            monitor.Mechanism, injector.Mechanism, history, autostart, ApplySettings);
        window.Closed += (_, _) => settingsWindow = null;
        settingsWindow = window;
        window.Show();
        if (showHistory) window.ShowHistory();
    });

    Config EffectiveConfig() => new()
    {
        Model = model.Id,
        CpuModel = config.CpuModel,
        Gpu = useGpu,
        Language = language,
        Hotkey = hotkeyName,
        Overlay = overlay is not null,
        ClearHistoryOnReboot = config.ClearHistoryOnReboot,
        Injection = config.Injection,
        PasteShortcut = config.PasteShortcut,
        HotkeyBackend = config.HotkeyBackend,
        AudioDevice = config.AudioDevice,
    };

    async void ApplySettings(Config updated)
    {
        try
        {
            updated.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"config opslaan mislukt: {ex.Message}");
            if (settingsWindow is { } window)
                await Dialogs.Info(window, $"Opslaan mislukt: {ex.Message}");
            return;
        }

        bool audioDeviceChanged = updated.AudioDevice != config.AudioDevice;

        // Model, taal of GPU-keuze vergen een nieuwe transcriber-pijplijn
        // (de native runtime kan niet herladen worden) — herstart schoon.
        // Hetzelfde geldt voor het opnameapparaat, dat bij de start wordt gekozen.
        if (updated.Model != model.Id
            || (updated.Language ?? "auto") != language
            || (updated.Gpu ?? true) != useGpu
            || audioDeviceChanged)
        {
            config = updated;
            Restart();
            return;
        }

        if (updated.Hotkey is { } newHotkey && newHotkey != hotkeyName
            && LinuxKeys.Evdev(newHotkey) is not null)
        {
            try
            {
                var replacement = HotkeyBackends.Start(newHotkey, debugHotkey, updated.HotkeyBackend);
                monitor.Stop();
                monitor.Dispose();
                monitor = replacement;
                monitor.OnEvent += HandleHotkey;
                hotkeyName = newHotkey;
                tray.UpdateHotkey(hotkeyName);
                Console.Error.WriteLine($"sneltoets gewijzigd naar {hotkeyName} ({monitor.Mechanism})");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"sneltoets wijzigen mislukt: {ex.Message}");
                if (settingsWindow is { } window)
                    await Dialogs.Info(window, $"Sneltoets wijzigen mislukte:\n\n{ex.Message}");
            }
        }
        else if (updated.HotkeyBackend != config.HotkeyBackend)
        {
            config = updated;
            Restart();
            return;
        }

        if (updated.Injection != config.Injection || updated.PasteShortcut != config.PasteShortcut)
        {
            injector.Dispose();
            injector = new LinuxTextInjector(updated.Injection, updated.PasteShortcut);
            Console.Error.WriteLine($"invoegen via: {injector.Mechanism}");
        }

        bool wantOverlay = updated.Overlay ?? true;
        Dispatcher.UIThread.Post(() =>
        {
            if (wantOverlay && overlay is null)
            {
                overlay = new OverlayWindow();
                capture.OnLevel = level => overlay?.PushLevel(level);
            }
            else if (!wantOverlay && overlay is not null)
            {
                capture.OnLevel = null;
                overlay.Close();
                overlay = null;
            }
        });

        config = updated;
    }

    void Restart()
    {
        string exe = Environment.ProcessPath!;
        Console.Error.WriteLine("herstarten met de nieuwe instellingen…");
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                ArgumentList = { "run" },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"herstarten mislukt: {ex.Message}");
            return;
        }
        Quit();
    }

    public void Quit() => Dispatcher.UIThread.Post(() =>
    {
        Dispose();
        lifetime.Shutdown();
    });

    bool disposed;

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        monitor.Stop();
        monitor.Dispose();
        capture.Dispose();
        injector.Dispose();
        tray.Dispose();
        overlay?.Close();
        settingsWindow?.Close();
        transcriber.Dispose();
    }
}
