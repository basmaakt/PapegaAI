using System.Diagnostics;
using System.Windows.Forms;
using Parrot.Audio;
using Parrot.History;
using Parrot.Input;
using Parrot.Models;
using Parrot.Platform;
using Parrot.Transcription;
using Parrot.UI;

namespace Parrot;

/// <summary>
/// Wires the daemon together on the UI thread: hotkey hook → audio capture →
/// transcription → text injection, with the tray icon, overlay and settings
/// window reflecting state. Owns all components; disposed when the message
/// loop ends.
/// </summary>
sealed class DaemonContext : ApplicationContext
{
    readonly WhisperTranscriber transcriber;
    readonly TranscriptionModel model;
    readonly string language;
    readonly bool debugHotkey;
    readonly bool dumpWav;
    readonly IAudioCapture capture;
    readonly ITextInjector injector = new TextInjector();
    readonly TrayController tray;
    readonly HistoryStore history;
    readonly SynchronizationContext ui;

    HotkeyMonitor monitor;
    RecordingOverlay? overlay;
    string hotkeyName;
    bool clearHistoryOnReboot;
    bool leadingSpace;
    string? cpuModel;
    readonly bool useGpu;
    SettingsForm? settingsForm;

    public DaemonContext(
        WhisperTranscriber transcriber,
        TranscriptionModel model,
        string language,
        Hotkey hotkey,
        string hotkeyName,
        bool debugHotkey,
        bool dumpWav,
        bool noOverlay,
        bool clearHistoryOnReboot,
        bool leadingSpace,
        string? cpuModel,
        bool useGpu)
    {
        this.leadingSpace = leadingSpace;
        this.cpuModel = cpuModel;
        this.useGpu = useGpu;
        this.transcriber = transcriber;
        this.model = model;
        this.language = language;
        this.hotkeyName = hotkeyName;
        this.debugHotkey = debugHotkey;
        this.dumpWav = dumpWav;
        this.clearHistoryOnReboot = clearHistoryOnReboot;
        history = new HistoryStore(clearHistoryOnReboot);

        ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        overlay = noOverlay ? null : new RecordingOverlay();
        tray = new TrayController(model.Id, WhisperTranscriber.LoadedRuntime, hotkeyName,
            Quit, () => OpenSettings(showHistory: false), () => OpenSettings(showHistory: true));
        capture = new AudioCapture();
        if (overlay is not null)
            capture.OnLevel = level => overlay.PushLevel(level);

        monitor = new HotkeyMonitor(hotkey, debugHotkey);
        monitor.OnEvent += HandleHotkey;
        monitor.Start();
    }

    void HandleHotkey(HotkeyEventKind kind)
    {
        if (kind == HotkeyEventKind.Pressed)
        {
            try
            {
                capture.Start();
                Console.Error.WriteLine("● recording");
                overlay?.Show(OverlayState.Recording);
                tray.SetRecording(true);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"capture failed: {ex.Message}");
            }
            return;
        }

        float[] samples = capture.Stop();
        overlay?.Show(OverlayState.Transcribing);
        tray.SetTranscribing();

        double seconds = samples.Length / (double)AudioCapture.TargetSampleRate;
        float rms = Dsp.ComputeRms(samples);
        Console.Error.WriteLine($"○ captured {seconds:0.00}s · rms {rms:0.000}");

        if (dumpWav && samples.Length > 0)
        {
            string path = Path.Combine(Path.GetTempPath(), "PapegaAI-last.wav");
            try
            {
                WavWriter.Write(samples, AudioCapture.TargetSampleRate, path);
                Console.Error.WriteLine($"  wrote {path}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  wav write failed: {ex.Message}");
            }
        }

        if (samples.Length == 0)
        {
            overlay?.Hide();
            tray.SetRecording(false);
            return;
        }

        // Pure digital silence: the device exists but nothing arrives (muted
        // headset, wireless mic powered off). Whisper hallucinates on silence
        // ("*", phantom subtitles) — warn instead of typing garbage.
        if (rms < 0.0005f)
        {
            Console.Error.WriteLine("  silent capture — skipping transcription");
            tray.Notify("Geen geluid van de microfoon. Staat je headset aan en is de mute-schakelaar uit?");
            overlay?.Hide();
            tray.SetRecording(false);
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
                ui.Post(_ =>
                {
                    injector.Inject(OutputFormatting.ForInjection(text, leadingSpace));
                    overlay?.Hide();
                    tray.SetRecording(false);
                    if (settingsForm is { IsDisposed: false })
                        settingsForm.RefreshHistory();
                }, null);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"transcription failed: {ex.Message}");
                ui.Post(_ =>
                {
                    overlay?.Hide();
                    tray.SetRecording(false);
                }, null);
            }
        });
    }

    void OpenSettings(bool showHistory)
    {
        if (settingsForm is { IsDisposed: false })
        {
            settingsForm.Activate();
            if (showHistory) settingsForm.ShowHistory();
            return;
        }
        settingsForm = new SettingsForm(
            EffectiveConfig(), WhisperTranscriber.LoadedRuntime, history, ApplySettings);
        settingsForm.Show();
        if (showHistory) settingsForm.ShowHistory();
    }

    Config EffectiveConfig() => new()
    {
        Model = model.Id,
        CpuModel = cpuModel,
        Gpu = useGpu,
        Language = language,
        Hotkey = hotkeyName,
        Overlay = overlay is not null,
        ClearHistoryOnReboot = clearHistoryOnReboot,
        LeadingSpace = leadingSpace,
    };

    void ApplySettings(Config config)
    {
        try
        {
            config.Save();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"could not save config: {ex.Message}");
            MessageBox.Show($"Opslaan mislukt: {ex.Message}", "PapegaAI",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        // Geldt vanaf de eerstvolgende start; geen herstart nodig.
        clearHistoryOnReboot = config.ClearHistoryOnReboot ?? false;

        // Geldt meteen: de spatie wordt per dictaat bepaald.
        leadingSpace = config.LeadingSpace ?? OutputFormatting.LeadingSpaceByDefault;

        // Alleen relevant bij een CPU-start; op een GPU-machine volstaat opslaan.
        bool cpuModelChanged = config.CpuModel != cpuModel;
        cpuModel = config.CpuModel;

        // Model, taal of GPU-keuze vergen een nieuwe transcriber-pijplijn
        // (de native runtime kan niet herladen worden) — herstart schoon.
        if (config.Model != model.Id || (config.Language ?? "auto") != language
            || (config.Gpu ?? true) != useGpu
            || (cpuModelChanged && !WhisperTranscriber.IsGpuRuntime))
        {
            Restart();
            return;
        }

        if (config.Hotkey is not null && config.Hotkey != hotkeyName
            && Hotkey.Parse(config.Hotkey) is { } newHotkey)
        {
            monitor.Stop();
            monitor.Dispose();
            monitor = new HotkeyMonitor(newHotkey, debugHotkey);
            monitor.OnEvent += HandleHotkey;
            monitor.Start();
            hotkeyName = config.Hotkey;
            tray.UpdateHotkey(hotkeyName);
            Console.Error.WriteLine($"hotkey changed to {hotkeyName}");
        }

        bool wantOverlay = config.Overlay ?? true;
        if (wantOverlay && overlay is null)
        {
            overlay = new RecordingOverlay();
            capture.OnLevel = level => overlay.PushLevel(level);
        }
        else if (!wantOverlay && overlay is not null)
        {
            capture.OnLevel = null;
            overlay.Dispose();
            overlay = null;
        }
    }

    void Restart()
    {
        string exe = Environment.ProcessPath!;
        Console.Error.WriteLine("restarting with new model/language…");
        Process.Start(new ProcessStartInfo(exe)
        {
            ArgumentList = { "run", "--hidden" },
            UseShellExecute = false,
        });
        Quit();
    }

    public void ExitFromAnyThread() => ui.Post(_ => Quit(), null);

    void Quit()
    {
        monitor.Stop();
        capture.Dispose();
        tray.Dispose();
        overlay?.Dispose();
        settingsForm?.Dispose();
        transcriber.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            monitor.Dispose();
            capture.Dispose();
            tray.Dispose();
            overlay?.Dispose();
            settingsForm?.Dispose();
        }
        base.Dispose(disposing);
    }
}
