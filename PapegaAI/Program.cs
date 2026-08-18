using System.Runtime.InteropServices;
using Parrot.Input;
using Parrot.Models;
using Parrot.Transcription;

namespace Parrot;

/// <summary>
/// Minimal Windows dictation daemon. Hold the push-to-talk key, speak,
/// release — the transcript is typed at the cursor. Windows port of
/// https://github.com/digimata/parrot (macOS original).
/// </summary>
static class Program
{
    const string Usage = """
        PapegaAI — minimal Windows dictation daemon. Hold the hotkey, speak, release.

        USAGE:
          PapegaAI [run] [options]            run the daemon (default command)
          PapegaAI setup                      one-time setup: download model + checks
          PapegaAI doctor                     check microphone, model cache, hotkey
          PapegaAI models list                list available models
          PapegaAI models download <id>       pre-download a model
          PapegaAI transcribe <file.wav>      transcribe an audio file (debug/test)
          PapegaAI mictest                    record 3s from the mic and report the level
          PapegaAI install --launch-at-login  start PapegaAI automatically on login
          PapegaAI install --uninstall        remove the login entry

        OPTIONS (for run and transcribe):
          --model <id>       model to use (default: recommended, see `models list`)
          --language <code>  force a language ("nl", "en", …) or "auto" to detect.
                             Needs a multilingual model (one without .en)
          --no-gpu           skip GPU acceleration, run on the CPU
          --hotkey <key>     push-to-talk key: right-ctrl (default), left-ctrl,
                             right-alt, right-shift, caps-lock, scroll-lock, f13..f24
          --no-overlay       disable the on-screen recording pill
          --dump-wav         write each capture to %TEMP%\PapegaAI-last.wav
          --skip-doctor      skip startup checks
          --debug-hotkey     print every keyboard event the hook sees

        Defaults can be persisted in %LOCALAPPDATA%\PapegaAI\config.json:
          { "model": "whisper-small", "language": "nl", "hotkey": "right-ctrl", "overlay": true }
        """;

    [DllImport("kernel32.dll")]
    static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll")]
    static extern nint GetConsoleWindow();

    const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>True when stdout/stderr actually reach a terminal. False when
    /// launched by double-click or at login — then errors need a MessageBox.</summary>
    public static bool HasConsole { get; private set; }

    [STAThread]
    static int Main(string[] args)
    {
        // As a WinExe we never own a console window, but when started from a
        // terminal we borrow its console so the CLI commands still print.
        HasConsole = AttachConsole(ATTACH_PARENT_PROCESS) || GetConsoleWindow() != 0;
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
            switch (command)
            {
                case "run": return RunCommand.Run(list);
                case "setup": return SetupCommand.Run(list);
                case "doctor": return Doctor.RunCli();
                case "models": return ModelsCommand.Run(list);
                case "transcribe": return TranscribeCommand.Run(list);
                case "mictest": return MicTestCommand.Run(list);
                case "install": return Install.Run(list);
                default:
                    Console.Error.WriteLine($"unknown command: {command}\n");
                    Console.Error.WriteLine(Usage);
                    return 1;
            }
        }
        catch (Exception ex)
        {
            Fatal($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>Report a fatal error where the user can actually see it:
    /// the terminal when there is one, a dialog when started silently.</summary>
    public static void Fatal(string message)
    {
        Console.Error.WriteLine(message);
        if (!HasConsole)
            System.Windows.Forms.MessageBox.Show(
                message, "PapegaAI",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
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
        args.Remove("--hidden"); // obsolete no-op: there is no console window anymore
        string? modelId = TakeOption(args, "--model") ?? config.Model;
        string? languageArg = TakeOption(args, "--language") ?? config.Language;
        string hotkeyName = TakeOption(args, "--hotkey") ?? config.Hotkey ?? "right-ctrl";

        if (args.Count > 0)
        {
            Console.Error.WriteLine($"unknown argument: {args[0]}");
            return 1;
        }

        // Single instance: two daemons would each hook the key and inject the
        // transcript twice. The short wait lets a self-restart hand over
        // cleanly while the old process is still shutting down.
        using var instanceLock = new Mutex(initiallyOwned: false, @"Local\PapegaAI-daemon");
        bool lockOwned;
        try
        {
            lockOwned = instanceLock.WaitOne(TimeSpan.FromSeconds(5));
        }
        catch (AbandonedMutexException)
        {
            lockOwned = true; // previous instance died without releasing; fine
        }
        if (!lockOwned)
        {
            Console.Error.WriteLine("PapegaAI draait al (tray-icoon rechtsonder). Tweede instantie gestopt.");
            return 1;
        }

        var hotkey = Hotkey.Parse(hotkeyName);
        if (hotkey is null)
        {
            Program.Fatal($"unknown hotkey: {hotkeyName}\n" +
                "valid keys: right-ctrl, left-ctrl, right-alt, right-shift, caps-lock, scroll-lock, f13..f24");
            return 1;
        }

        if (!skipDoctor)
        {
            var checks = Doctor.RunChecks(modelId);
            if (!Doctor.AllOk(checks))
            {
                string report = string.Join("\n", checks.Select(c =>
                    $"  {(c.Ok ? "✓" : "✗")} {c.Name} — {c.Detail}"));
                Program.Fatal($"startup checks failed:\n{report}\n\nfix the above or pass --skip-doctor");
                return 1;
            }
        }

        var resolved = ModelSelection.Resolve(modelId, languageArg);
        if (resolved is null) return 1;
        var (model, language) = resolved.Value;

        var transcriber = new WhisperTranscriber(model, language);
        try
        {
            // Loading the factory reveals which native runtime actually
            // loaded (CUDA/Vulkan/CPU). The automatic small-model fallback
            // applies only when a GPU was WANTED but none loaded — a user who
            // explicitly disabled GPU keeps their chosen model, however slow.
            // An explicit cpu_model config override applies on any CPU run.
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

        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);

        using var context = new DaemonContext(
            transcriber, model, language, hotkey.Value, hotkeyName, debugHotkey, dumpWav, noOverlay,
            clearHistoryOnReboot: config.ClearHistoryOnReboot ?? false,
            leadingSpace: config.LeadingSpace ?? OutputFormatting.LeadingSpaceByDefault,
            cpuModel: config.CpuModel,
            useGpu: useGpu);

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            Console.Error.WriteLine("\nshutting down");
            context.ExitFromAnyThread();
        };

        Console.Error.WriteLine($"listening on {hotkeyName} hold · model: {model.Id} · language: {language} · ^C to quit");
        System.Windows.Forms.Application.Run(context);
        return 0;
    }

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
    public static int Run(List<string> args)
    {
        Console.Error.WriteLine("PapegaAI setup — downloading the recommended model and running checks.\n");
        var model = ModelRegistry.Recommended();
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
        int rc = Doctor.RunCli();
        if (rc == 0)
        {
            Console.Error.WriteLine("\n✓ setup complete. Run `PapegaAI` and hold right-ctrl to dictate.");
            Console.Error.WriteLine("  (unlike macOS, Windows needs no accessibility permission)");
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
                    Console.Error.WriteLine("usage: PapegaAI models download <id>");
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

/// <summary>Record a few seconds from the default microphone and report the
/// signal level — separates "mic is muted/dead" from "transcription broke"
/// when dictation returns garbage.</summary>
static class MicTestCommand
{
    public static int Run(List<string> args)
    {
        using (var enumerator = new NAudio.CoreAudioApi.MMDeviceEnumerator())
        using (var device = enumerator.GetDefaultAudioEndpoint(
            NAudio.CoreAudioApi.DataFlow.Capture, NAudio.CoreAudioApi.Role.Console))
        {
            var vol = device.AudioEndpointVolume;
            Console.Error.WriteLine(
                $"device: {device.FriendlyName} · windows-mute: {(vol.Mute ? "AAN" : "uit")} · niveau: {vol.MasterVolumeLevelScalar:P0}");
        }

        using var capture = new Parrot.Audio.AudioCapture();
        Console.Error.WriteLine("recording 3s from the default microphone — say something…");
        capture.Start();
        Thread.Sleep(3000);
        float[] samples = capture.Stop();

        double seconds = samples.Length / (double)Parrot.Audio.AudioCapture.TargetSampleRate;
        float rms = Parrot.Audio.Dsp.ComputeRms(samples);
        Console.Error.WriteLine($"captured {seconds:0.00}s · rms {rms:0.00000}");

        if (samples.Length > 0)
        {
            string path = Path.Combine(Path.GetTempPath(), "PapegaAI-mictest.wav");
            Parrot.Audio.WavWriter.Write(samples, Parrot.Audio.AudioCapture.TargetSampleRate, path);
            Console.Error.WriteLine($"wrote {path}");
        }

        if (samples.Length == 0 || rms < 0.0005f)
        {
            Console.Error.WriteLine("⚠ (near-)total silence — is the microphone muted? (hardware mute switch, or Settings → Privacy → Microphone)");
            return 1;
        }
        Console.Error.WriteLine("✓ microphone delivers signal");
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
            Console.Error.WriteLine("usage: PapegaAI transcribe <file.wav> [--model id] [--language code]");
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

        float[] samples = LoadMono16k(file);
        Console.Error.WriteLine($"{samples.Length / (double)Parrot.Audio.AudioCapture.TargetSampleRate:0.00}s of audio · model {model.Id} · language {language}");

        using var transcriber = new WhisperTranscriber(model, language);
        transcriber.WarmUp().GetAwaiter().GetResult();
        var started = DateTime.UtcNow;
        string text = transcriber.Transcribe(samples).GetAwaiter().GetResult();
        Console.Error.WriteLine($"→ {(DateTime.UtcNow - started).TotalSeconds:0.00}s");
        Console.WriteLine(text);
        return 0;
    }

    static float[] LoadMono16k(string path)
    {
        using var reader = new NAudio.Wave.AudioFileReader(path);
        NAudio.Wave.ISampleProvider provider = reader;
        if (provider.WaveFormat.Channels == 2)
            provider = new NAudio.Wave.SampleProviders.StereoToMonoSampleProvider(provider);
        if (provider.WaveFormat.SampleRate != Parrot.Audio.AudioCapture.TargetSampleRate)
            provider = new NAudio.Wave.SampleProviders.WdlResamplingSampleProvider(
                provider, Parrot.Audio.AudioCapture.TargetSampleRate);

        var output = new List<float>();
        var chunk = new float[8192];
        int read;
        while ((read = provider.Read(chunk, 0, chunk.Length)) > 0)
            output.AddRange(chunk.Take(read));
        return output.ToArray();
    }
}
