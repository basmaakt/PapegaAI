# PapegaAI for Linux

The Linux build of [PapegaAI](../PapegaAI/README.md) — push-to-talk dictation,
on-device transcription, text typed in at the cursor. Same daemon, same models,
same settings file format as the Windows build; it shares its core with it
(see [`PapegaAI.Core`](../PapegaAI.Core)) and only replaces the parts that
touch the operating system.

## Install

```bash
tar xzf papegaai-linux-x64.tar.gz
cd dist-linux-x64
bash install.sh          # per-user, geen root behalve voor de twee toestemmingen
papegaai setup           # model downloaden + alles controleren
papegaai                 # starten (tray-icoon, geen venster)
```

`install.sh` puts the program in `~/.local/lib/papegaai`, links `papegaai` into
`~/.local/bin`, installs the icon and menu entry, and offers to arrange the two
permissions Linux only hands out as root (see below). `bash install.sh
--uninstall` removes all of that again and leaves your models and settings
alone.

Hold **right Ctrl**, speak, release — the transcript types itself in wherever
the cursor is. There is no record button; the hotkey is the whole interface.

## What Linux needs that Windows does not

Windows hands an application a global key hook and synthetic typing for free.
Linux does not, and what it *does* offer depends on your session, so PapegaAI
detects and falls back rather than demanding one setup.

| | X11 | Wayland |
|---|---|---|
| **Watching the hotkey** | X RECORD extension — no permission needed | evdev (`/dev/input`) — needs the `input` group |
| **Typing the text** | `xdotool` — real unicode, no permission | `wtype` (wlroots/KDE), else `/dev/uinput` + clipboard paste |
| **Recording pill** | positioned exactly | positioned by the compositor (runs through XWayland) |
| **Tray icon** | works | works, except GNOME (needs the AppIndicator extension) |

`papegaai doctor` reports which route it will actually take, and names the fix
for anything missing:

```
  ✓ sessie — Wayland (GNOME)
  ✓ microfoon — ALSA (default)
  ✓ sneltoets — right-ctrl via evdev
  ✓ tekst invoegen — uinput (reserve: clipboard)
  ✓ model whisper-large-v3-turbo — in cache
```

### The two permissions

Both are one-time, both are offered by `install.sh`, and neither needs to stay
enabled for anything else:

- **`input` group** — lets PapegaAI read the push-to-talk key from
  `/dev/input/event*`. Required on Wayland, optional on X11.
  `sudo usermod -aG input $USER`, then log out and back in.
- **`/dev/uinput`** — lets PapegaAI act as a virtual keyboard so it can press
  Ctrl+V for you. Only needed when neither `xdotool` nor `wtype` applies —
  which is exactly GNOME on Wayland. Without it, PapegaAI still puts the
  transcript on the clipboard and tells you so.

### Helpers to install

```bash
sudo apt install xdotool          # X11 sessions
sudo apt install wl-clipboard     # Wayland sessions
sudo apt install libnotify-bin    # desktop notifications (optional)
sudo apt install wtype            # Wayland, wlroots/KDE only (optional, nicest)
```

`ydotool` is used too when it is installed and its daemon is running.

## CLI

```
papegaai                                 run in the foreground (^C to quit)
papegaai setup                           one-time: download model + health checks
papegaai doctor                          check session, mic, hotkey, injection, model
papegaai mictest                         record 3s and report the level
papegaai models list                     list available models
papegaai models download <id>            pre-download a model
papegaai transcribe <file.wav>           transcribe an audio file (debug/test)
papegaai install --launch-at-login       autostart via ~/.config/autostart
papegaai install --uninstall             remove the autostart entry
papegaai --model whisper-small           multilingual — good Dutch, still fast
papegaai --language nl                   force a language (skips auto-detect)
papegaai --hotkey f13                    change the push-to-talk key
papegaai --no-overlay                    disable the on-screen pill
papegaai --debug-hotkey                  print every key event the backend sees
```

## Settings

Right-click the tray icon → **Instellingen…**: model, language, hotkey, GPU,
overlay, autostart, and the two Linux-only choices — how the hotkey is watched
and how text is inserted. **Geschiedenis…** in the same menu opens the window
straight on the history tab, which keeps your last 100 dictations with a copy
button — what saves you when an injection misfires.

Config lives in `~/.config/PapegaAI/config.json`; models and history in
`~/.local/share/PapegaAI/`:

```json
{ "model": "whisper-large-v3-turbo", "gpu": true, "language": "nl",
  "hotkey": "right-ctrl", "overlay": true, "leading_space": true,
  "injection": "auto", "paste_shortcut": "ctrl+v",
  "hotkey_backend": "auto", "audio_device": null }
```

- `leading_space` — type a space before each transcript so back-to-back
  dictations do not run together. On by default; the history keeps the text
  without it.
- `injection` — `auto`, `xdotool`, `wtype`, `ydotool`, `uinput`, `clipboard`.
- `paste_shortcut` — `ctrl+v`, or `ctrl+shift+v` if you mostly dictate into a
  terminal (the clipboard routes press this for you).
- `hotkey_backend` — `auto`, `x11`, `evdev`.
- `audio_device` — an ALSA name (`default`, `pulse`, `hw:1,0`), or prefix a
  helper to bypass ALSA entirely: `parec:alsa_input.usb-Blue_Yeti`.

Out of the box PapegaAI uses `whisper-large-v3-turbo` in Dutch. Everything else
behaves exactly as on Windows, including the automatic fallback to
`whisper-small` when a GPU was wanted but none loaded — so on a machine without
one, set the model to `whisper-small` before the first run to avoid downloading
1.6 GB that is then not used.

## Build

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
Builds on Linux, and cross-compiles from Windows (Git Bash) just as well:

```bash
./build-linux.sh                    # linux-x64, self-contained
./build-linux.sh linux-arm64        # ARM machines
./build-linux.sh linux-x64 --no-gpu # skip the Vulkan runtime (~60 MB smaller)
```

The result is `dist-<rid>/` plus a tarball. Self-contained: the target machine
needs no .NET. About 150 MB, most of it the Vulkan compute kernels.

Models are **not** in the package; they download once from Hugging Face into
`~/.local/share/PapegaAI/models` (base 142 MB · small 466 MB · turbo 1.6 GB).
After that everything runs fully offline — audio never leaves the machine.

## Known limits

- **caps-lock as hotkey still toggles caps.** Windows swallows the key inside
  its hook; on Linux both evdev and X RECORD are passive taps, and suppressing
  a key would mean grabbing the whole keyboard away from the desktop. Use
  right-ctrl, or an f13–f24 key if your keyboard has them.
- **GNOME shows no tray icon** without the *AppIndicator and KStatusNotifierItem
  Support* extension. PapegaAI still runs and dictates; you just cannot reach
  the settings window from the tray. `papegaai doctor` says so at startup.
- **The pill is placed by the compositor on some Wayland setups.** Avalonia
  draws through XWayland, so most compositors honour the position, but a strict
  one may put the pill elsewhere. `--no-overlay` turns it off.
- **The clipboard route replaces your clipboard briefly.** The previous
  contents are put back about half a second after pasting.
