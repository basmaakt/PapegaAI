# PapegaAI

A minimal Windows 11 dictation daemon — port of [digimata/parrot](https://github.com/digimata/parrot) (macOS).
Push-to-talk, on-device transcription, text typed in at the cursor.

> Config, models, transcription and history live in the shared
> [`PapegaAI.Core`](../PapegaAI.Core) project; only the WASAPI/WinForms/Win32
> half is here. There is a Linux build of the same daemon in
> [`PapegaAI.Linux`](../PapegaAI.Linux).

## How to use

1. **Run it:** double-click `PapegaAI.exe` (tray icon only, no window), run `PapegaAI` in a terminal, or `PapegaAI install --launch-at-login` to start on login.
2. **Click into any text field** — a browser address bar, Word, Slack, anywhere a cursor blinks.
3. **Hold `right Ctrl`, speak, release.** A small pill appears at the bottom of the screen while the mic is hot.
4. **The transcript types itself in at the cursor** when you release.

There is no record button, no stop button — the hotkey is the whole interface.

> The macOS original uses the `fn` key, but on Windows keyboards `fn` is handled
> in hardware and never reaches the OS. The default here is `right Ctrl`;
> change it with `--hotkey` (e.g. `caps-lock`, which PapegaAI then swallows so it
> won't toggle caps while you dictate).

## Install / build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
cd PapegaAI
dotnet build -c Release
.\bin\Release\net8.0-windows\PapegaAI.exe setup   # downloads the model, runs checks
.\bin\Release\net8.0-windows\PapegaAI.exe         # run it
```

## Install on another computer

**Recommended: the installer.** Build it once:

```powershell
dotnet publish -c Release -r win-x64 --self-contained -o dist
& "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe" installer.iss
```

That produces `installer-output\PapegaAI-setup.exe` — a single file that
installs per-user (no admin rights), adds a Start-menu entry, optionally
enables launch-at-login and a desktop icon, and registers in Windows'
"Apps & features" for clean uninstall. The uninstaller asks whether to also
remove the downloaded models/settings/history.

**Alternative: portable.** Copy the `dist` folder to the other PC and run:

```powershell
.\PapegaAI.exe setup                       # downloads the model, checks the mic
.\PapegaAI.exe install --launch-at-login   # start automatically on login
```

Models are **not** in the package; they download once from Hugging Face into
`%LOCALAPPDATA%\PapegaAI\models` (base 142 MB · small 466 MB · turbo 1.6 GB) —
on first `setup`/run, or in the background after picking a different model in
the settings window. After that everything runs fully offline. Settings and
history are per-machine (`%LOCALAPPDATA%\PapegaAI`).

## CLI

```
PapegaAI                                 run in the foreground (^C to quit)
PapegaAI setup                           one-time: download model + health checks
PapegaAI install --launch-at-login       start automatically on login (HKCU Run key)
PapegaAI install --uninstall             remove the login entry
PapegaAI doctor                          check mic + model cache
PapegaAI models list                     list available models
PapegaAI models download <id>            pre-download a model
PapegaAI transcribe <file.wav>           transcribe an audio file (debug/test)
PapegaAI --model whisper-small           multilingual — good Dutch, still fast
PapegaAI --language nl                   force a language (skips auto-detect)
PapegaAI --hotkey caps-lock              change the push-to-talk key
PapegaAI --no-overlay                    disable the bottom-of-screen pill
```

Models are cached in `%LOCALAPPDATA%\PapegaAI\models`.

## Settings window

Right-click the tray icon → **Instellingen…** (or double-click the icon):
choose model, language, and hotkey, toggle the overlay and launch-at-login.
Saving applies hotkey/overlay changes immediately; a model or language change
restarts the daemon. **Geschiedenis…** in the same menu opens the window straight on that tab.
It lists your last 100 dictations
(stored in `%LOCALAPPDATA%\PapegaAI\history.json`) with a copy button — handy
when an injection ever misfires.

Only one PapegaAI can run at a time; a second instance exits immediately (two
daemons would both hear the hotkey and type everything twice).

## Config

Defaults live in `%LOCALAPPDATA%\PapegaAI\config.json` (flags override it):

```json
{ "model": "whisper-large-v3-turbo", "cpu_model": "whisper-small", "gpu": true,
  "language": "auto", "hotkey": "right-ctrl", "overlay": true,
  "clear_history_on_reboot": false, "leading_space": true }
```

When a GPU is wanted (`gpu` on) but no GPU runtime loads, models larger than
small automatically fall back to `whisper-small` (or `whisper-small.en` for
English-only models) — big models are GPU-fast but CPU-slow. Explicitly
disabling the GPU keeps your chosen model, however slow: that's a deliberate
choice. `cpu_model` (config-only, optional) overrides the automatic fallback
on any CPU run. The tray menu and settings window show the active runtime.

`gpu` (default `true`; checkbox in the settings window, or `--no-gpu` on the
command line) disables GPU acceleration entirely — PapegaAI then runs on the CPU
and, when set, switches to `cpu_model` automatically.

`leading_space` (default `true`, checkbox in the settings window) types a space
before each transcript, so dictating twice in a row does not run the words
together. The cost is one stray space when you dictate into an empty field —
easier to delete than a missing space is to insert mid-word. The history stores
the text without it.

`clear_history_on_reboot` (also a checkbox in the settings window) wipes the
dictation history on the first PapegaAI start after a Windows reboot — a
PapegaAI-only restart keeps it. Detection compares the boot time stored in
`%LOCALAPPDATA%\PapegaAI\lastboot.txt` against the current uptime counter.

**Dutch / multilingual:** the default `whisper-base.en` model is English-only.
Use a model without `.en` (`whisper-base`, `whisper-small`,
`whisper-large-v3-turbo`) with `"language": "auto"` for mixed Dutch/English,
or `"language": "nl"` for Dutch-only (slightly faster and more robust on
short clips).

## Stack — macOS original vs this port

(Linux equivalents are in [PapegaAI.Linux/README.md](../PapegaAI.Linux/README.md).)

| Concern | macOS (original) | Windows (this port) |
|---|---|---|
| Language | Swift (SPM) | C# (.NET 8) |
| Transcription | WhisperKit / CoreML on the Neural Engine | Whisper.net / whisper.cpp on the CPU |
| Mic capture | AVAudioEngine | WASAPI (NAudio), resampled to 16 kHz |
| Global hotkey | CGEventTap on `fn` (needs Accessibility) | Low-level keyboard hook (no permission) |
| Text injection | CGEvent unicode events (needs Accessibility) | `SendInput` unicode events (no permission) |
| Recording pill | borderless NSPanel + SwiftUI | borderless click-through WinForms window |
| Tray | NSStatusItem | NotifyIcon |
| Launch at login | LaunchAgent plist | HKCU `...\CurrentVersion\Run` |

## Notes

- Transcription is fully local; audio never leaves the machine.
- Windows apps running **as administrator** won't accept injected text from a
  non-elevated PapegaAI (UIPI). Run PapegaAI elevated too if you need to dictate
  into elevated windows.
- If the mic captures silence, check Settings → Privacy & security →
  Microphone → "Let desktop apps access your microphone".
- GPU transcription is built in via the Vulkan runtime (works on any modern
  NVIDIA/AMD/Intel GPU through the normal graphics driver), falling back to
  CPU automatically. On an RTX 4080 SUPER the large-v3-turbo model transcribes
  8s of speech in ~0.2s — with a GPU, turbo is both the most accurate and the
  fastest choice.
