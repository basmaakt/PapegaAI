using Avalonia;
using Parrot.Audio;
using Parrot.Input;
using Parrot.Models;
using Parrot.Platform;
using Parrot.Transcription;
using Parrot.UI;

namespace Parrot;

/// <summary>
/// Minimal Linux dictation daemon. Hold the push-to-talk key, speak, release —
/// the transcript is typed at the cursor. Linux port of
/// https://github.com/digimata/parrot (macOS original), sharing its core with
/// the Windows build.
/// </summary>
static class Program
{
    const string Usage = """
        PapegaAI — minimal Linux dictation daemon. Hold the hotkey, speak, release.

        USAGE:
          papegaai [run] [options]            run the daemon (default command)
          papegaai setup                      one-time setup: download model + checks
          papegaai doctor                     check session, mic, hotkey, injection, model
          papegaai models list                list available models
          papegaai models download <id>       pre-download a model
          papegaai transcribe <file.wav>      transcribe an audio file (debug/test)
          papegaai mictest                    record 3s from the mic and report the level
          papegaai install --launch-at-login  start PapegaAI automatically on login
          papegaai install --uninstall        remove the autostart entry
          papegaai export-icons [dir]         unpack the app icons into an icon theme

        OPTIONS (for run and transcribe):
          --model <id>       model to use (default: recommended, see `models list`)
          --language <code>  force a language ("nl", "en", …) or "auto" to detect.
                             Needs a multilingual model (one without .en)
          --no-gpu           skip GPU acceleration, run on the CPU
          --hotkey <key>     push-to-talk key: right-ctrl (default), left-ctrl,
                             right-alt, right-shift, left-shift, right-super,
                             caps-lock, scroll-lock, f13..f24
          --no-overlay       disable the on-screen recording pill
          --dump-wav         write each capture to /tmp/PapegaAI-last.wav
          --skip-doctor      skip startup checks
          --debug-hotkey     print every key event the backend sees

        Defaults can be persisted in ~/.config/PapegaAI/config.json:
          { "model": "whisper-small", "language": "nl", "hotkey": "right-ctrl",
            "overlay": true, "injection": "auto", "hotkey_backend": "auto" }
        """;

    static int Main(string[] args)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

        var list = new List<string>(args);
        string command = "run";
        if (list.Count > 0 && !list[0].StartsWith('-'))
        {
            command = list[0];
            list.RemoveAt(0);
        }

        if (list.Contains("--help") || list.Contains("-h") || command == "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }

        try
        {
            return command switch
            {
                "run" => RunCommand.Run(list),
                "setup" => SetupCommand.Run(),
                "doctor" => Doctor.RunCli(Config.Load()),
                "models" => ModelsCommand.Run(list),
                "transcribe" => TranscribeCommand.Run(list),
                "mictest" => MicTestCommand.Run(),
                "install" => Autostart.RunCli(list),
                "export-icons" => ExportIconsCommand.Run(list),
                _ => Unknown(command),
            };
        }
        catch (Exception ex)
        {
            Fatal($"error: {ex.Message}");
            return 1;
        }
    }

    static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command: {command}\n");
        Console.Error.WriteLine(Usage);
        return 1;
    }

    /// <summary>Report a fatal error where the user can actually see it. When
    /// PapegaAI is started from the autostart entry there is no terminal, so
    /// the desktop's notification daemon stands in for the Windows MessageBox.</summary>
    public static void Fatal(string message)
    {
        Console.Error.WriteLine(message);
        if (!Console.IsErrorRedirected && Environment.GetEnvironmentVariable("TERM") is { Length: > 0 })
            return;
        Notify.Send("PapegaAI", message.Split('\n')[0]);
    }
}

static class RunCommand
{
    public static int Run(List<string> args)
    {
        var config = Config.Load();

        bool useGpu = !args.Remove("--no-gpu") && config.Gpu != false;
        if (!useGpu)
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            [
                Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
                Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx,
            ];
            Console.Error.WriteLine("GPU acceleration disabled — CPU runtime only");
        }

        bool skipDoctor = args.Remove("--skip-doctor");
        bool debugHotkey = args.Remove("--debug-hotkey");
        bool dumpWav = args.Remove("--dump-wav");
        bool noOverlay = args.Remove("--no-overlay") || config.Overlay == false;
        string? modelId = TakeOption(args, "--model") ?? config.Model;
        string? languageArg = TakeOption(args, "--language") ?? config.Language;
        string hotkeyName = HotkeyNames.Normalize(
            TakeOption(args, "--hotkey") ?? config.Hotkey ?? HotkeyNames.Default);

        if (args.Count > 0)
        {
            Console.Error.WriteLine($"unknown argument: {args[0]}");
            return 1;
        }

        // Single instance: two daemons would each see the hotkey and inject
        // the transcript twice. The short wait lets a self-restart hand over
        // cleanly while the old process is still shutting down.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Console.Error.WriteLine("PapegaAI draait al (kijk in je systeemvak). Tweede instantie gestopt.");
            return 1;
        }

        if (LinuxKeys.Evdev(hotkeyName) is null)
        {
            Program.Fatal($"onbekende sneltoets: {hotkeyName}\nkeuzes: {HotkeyNames.Describe()}");
            return 1;
        }

        if (!skipDoctor)
        {
            var checks = Doctor.RunChecks(config, modelId);
            // A failed injection check is a warning, not a stop: dictating into
            // the history and the clipboard is still worth something, and the
            // user may be about to fix the permission.
            var fatal = checks.Where(c => !c.Ok && c.Name != "tekst invoegen").ToList();
            if (fatal.Count > 0)
            {
                Doctor.Print(checks);
                Program.Fatal("startcontroles mislukt — los het bovenstaande op of gebruik --skip-doctor");
                return 1;
            }
            if (checks.Any(c => !c.Ok)) Doctor.Print(checks);
        }

        var resolved = ModelSelection.Resolve(modelId, languageArg);
        if (resolved is null) return 1;
        var (model, language) = resolved.Value;

        var transcriber = new WhisperTranscriber(model, language);
        try
        {
            // Loading the factory reveals which native runtime actually loaded
            // (Vulkan, CUDA, CPU). The automatic small-model fallback applies
            // only when a GPU was WANTED but none loaded — a user who
            // explicitly disabled GPU keeps their chosen model, however slow.
            transcriber.LoadFactory().GetAwaiter().GetResult();
            if (!WhisperTranscriber.IsGpuRuntime
                && (config.CpuModel ?? (useGpu ? ModelSelection.AutoCpuFallback(model) : null)) is { } cpuModelId
                && cpuModelId != model.Id)
            {
                var cpuResolved = ModelSelection.Resolve(cpuModelId, languageArg);
                if (cpuResolved is null)
                {
                    Console.Error.WriteLine($"cpu_model unusable — keeping {model.Id}");
                }
                else
                {
                    Console.Error.WriteLine(
                        $"no GPU runtime (got {WhisperTranscriber.LoadedRuntime}) — falling back to cpu_model {cpuResolved.Value.Model.Id}");
                    transcriber.Dispose();
                    (model, language) = cpuResolved.Value;
                    transcriber = new WhisperTranscriber(model, language);
                }
            }
            transcriber.WarmUp().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Program.Fatal($"warmup failed: {ex.Message}");
            return 1;
        }

        App.Options = new DaemonOptions(
            transcriber, model, language, config, hotkeyName,
            debugHotkey, dumpWav, noOverlay, useGpu);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("\nafsluiten");
            App.Daemon?.Quit();
        };

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(
            [], Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    internal static string? TakeOption(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        if (i < 0) return null;
        if (i + 1 >= args.Count)
            throw new ArgumentException($"{name} requires a value");
        string value = args[i + 1];
        args.RemoveRange(i, 2);
        return value;
    }
}

static class SetupCommand
{
    public static int Run()
    {
        Console.Error.WriteLine("PapegaAI setup — downloading the recommended model and running checks.\n");
        var config = Config.Load();
        var model = ModelRegistry.Find(config.Model ?? "") ?? ModelRegistry.Recommended();
        var transcriber = new WhisperTranscriber(model);
        try
        {
            transcriber.WarmUp().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"model download failed: {ex.Message}");
            return 1;
        }
        transcriber.Dispose();
        Console.Error.WriteLine();

        int rc = Doctor.RunCli(config);
        if (rc == 0)
        {
            Console.Error.WriteLine("\n✓ klaar. Start `papegaai` en houd right-ctrl ingedrukt om te dicteren.");
        }
        return rc;
    }
}

static class ModelsCommand
{
    public static int Run(List<string> args)
    {
        string sub = args.Count > 0 ? args[0] : "list";
        switch (sub)
        {
            case "list":
                foreach (var m in ModelRegistry.All)
                {
                    string star = m.Recommended ? "★" : " ";
                    string cached = ModelDownloader.IsCached(m) ? "cached" : "      ";
                    string langs = $"[{string.Join(",", m.Languages)}]";
                    Console.WriteLine($"{star} {m.Id,-26} {m.SizeMB,5} MB  {cached}  {langs,-9}  {m.DisplayName}");
                }
                return 0;

            case "download":
                if (args.Count < 2)
                {
                    Console.Error.WriteLine("usage: papegaai models download <id>");
                    return 1;
                }
                var model = ModelRegistry.Find(args[1]);
                if (model is null)
                {
                    Console.Error.WriteLine($"unknown model: {args[1]}");
                    return 1;
                }
                ModelDownloader.Ensure(model).GetAwaiter().GetResult();
                return 0;

            default:
                Console.Error.WriteLine($"unknown models subcommand: {sub}");
                return 1;
        }
    }
}

/// <summary>Record a few seconds from the microphone and report the signal
/// level — separates "mic is muted/dead" from "transcription broke" when
/// dictation returns garbage.</summary>
static class MicTestCommand
{
    public static int Run()
    {
        var config = Config.Load();
        Console.Error.WriteLine($"backend: {LinuxAudio.Describe(config.AudioDevice)}");

        using var capture = LinuxAudio.Create(config.AudioDevice);
        Console.Error.WriteLine("3 seconden opnemen — zeg eens iets…");
        capture.Start();
        Thread.Sleep(3000);
        float[] samples = capture.Stop();

        double seconds = samples.Length / (double)AudioFormat.SampleRate;
        float rms = Dsp.ComputeRms(samples);
        Console.Error.WriteLine($"{seconds:0.00}s opgenomen · rms {rms:0.00000}");

        if (samples.Length > 0)
        {
            string path = Paths.TempFile("PapegaAI-mictest.wav");
            WavWriter.Write(samples, AudioFormat.SampleRate, path);
            Console.Error.WriteLine($"geschreven: {path}");
        }

        if (samples.Length == 0 || rms < 0.0005f)
        {
            Console.Error.WriteLine("⚠ (bijna) volledige stilte — staat de microfoon gemute of uit?");
            Console.Error.WriteLine("  controleer met: pactl list sources short   /   arecord -l");
            return 1;
        }
        Console.Error.WriteLine("✓ de microfoon levert signaal");
        return 0;
    }
}

/// <summary>Unpack the embedded icons into an icon-theme directory. Used by
/// install.sh so the desktop entry and window manager can find the macaw by
/// name, without the artwork having to be shipped as loose files.</summary>
static class ExportIconsCommand
{
    public static int Run(List<string> args)
    {
        string target = args.FirstOrDefault(a => !a.StartsWith('-'))
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "icons", "hicolor");

        Icons.ExportTo(target);
        Console.Error.WriteLine($"✓ iconen geschreven naar {target}");
        return 0;
    }
}

/// <summary>Transcribe an audio file from disk — a debug/test path that
/// exercises the exact pipeline dictation uses, minus the microphone.</summary>
static class TranscribeCommand
{
    public static int Run(List<string> args)
    {
        if (args.Remove("--no-gpu"))
        {
            Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder =
            [
                Whisper.net.LibraryLoader.RuntimeLibrary.Cpu,
                Whisper.net.LibraryLoader.RuntimeLibrary.CpuNoAvx,
            ];
        }
        string? modelId = RunCommand.TakeOption(args, "--model");
        string? languageArg = RunCommand.TakeOption(args, "--language");
        string? file = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (file is null)
        {
            Console.Error.WriteLine("usage: papegaai transcribe <file.wav> [--model id] [--language code]");
            return 1;
        }
        if (!File.Exists(file))
        {
            Console.Error.WriteLine($"no such file: {file}");
            return 1;
        }

        var resolved = ModelSelection.Resolve(modelId, languageArg);
        if (resolved is null) return 1;
        var (model, language) = resolved.Value;

        var (raw, rate) = WavWriter.Read(file);
        float[] samples = Dsp.Resample(raw, rate, AudioFormat.SampleRate);
        Console.Error.WriteLine(
            $"{samples.Length / (double)AudioFormat.SampleRate:0.00}s of audio · model {model.Id} · language {language}");

        using var transcriber = new WhisperTranscriber(model, language);
        transcriber.WarmUp().GetAwaiter().GetResult();
        var started = DateTime.UtcNow;
        string text = transcriber.Transcribe(samples).GetAwaiter().GetResult();
        Console.Error.WriteLine($"→ {(DateTime.UtcNow - started).TotalSeconds:0.00}s");
        Console.WriteLine(text);
        return 0;
    }
}
