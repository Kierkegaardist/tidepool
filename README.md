# Tidepool

> **Early access.** Tidepool works, but it's young. Expect rough edges and bugs, and expect things to change between versions.

Tidepool replaces the Windows desktop with a console-style one: glossy Frutiger Aero glass over photo scenes that follow the real sun.
You can drive it with a keyboard, a mouse or a controller.
It runs as a layer on top of Explorer. It changes nothing permanent, and one hotkey always takes you back to stock Windows.

## What it does
- **One menu for everything:** Games (Steam and Epic libraries), Music, Video, Studio, Web, Files and Settings, in a console-style row.
- **Your own apps:** Music works with any player (Spotify, TIDAL, Apple Music, YouTube Music and more). Web uses any browser.
  Studio works with any DAW, and you can turn it off. Video searches and plays YouTube through yt-dlp, with no ads.
- **Marina:** a little goldfish assistant in a reef bowl. She chats through Claude Code or any AI API (OpenAI, Claude API, OpenRouter, Gemini, Ollama, LM Studio and more).
  You can type to her or say "Hey Marina". Ask her to restyle the desktop and the change shows live.
- **Living scenes:** lake, shore, hill, meadow and valley photos tinted by the time of day where you live. Music you play rises as light over the horizon.
- **Winamp skins** for the music player. A window switcher. Works across two screens.

## Install
1. Download `ConsoleDesktop-Setup.exe` from [Releases](../../releases).
2. Run it. It installs for your user only, so no admin is needed. If WebView2 is missing, the installer fetches it.
3. Setup opens next. Pick your player, browser, DAW and chat. You can change these later in Settings › Setup.

Windows may warn that the installer is unsigned. Click **More info › Run anyway**.

**Requirements:** Windows 10 or 11, 64-bit. Optional: [yt-dlp](https://github.com/yt-dlp/yt-dlp) for Video and
[Claude Code](https://claude.com/claude-code) or an API key for Marina. Setup can download yt-dlp for you.

## Getting back to normal Windows
Any of these works:
- **Ctrl+Alt+Win+S**, which works even if Tidepool hangs.
- Start menu › Console Desktop › **Back to stock Windows**.
- Nothing at all: if Tidepool crashes or stops answering for about 3 seconds, the taskbar comes back by itself.

To remove Tidepool, use Start menu › Console Desktop › **Uninstall**, or Settings › Apps.

## Known limits
- Maximised windows stop where the hidden taskbar used to be.
- Desktop icons draw over the scene.
- Read-aloud uses Windows' built-in voice. Kokoro voices (`tts/say.py`) are not bundled.
- So far it has only been tested on the developer's PC. Bug reports are welcome under [Issues](../../issues).

## Build from source
You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).
```
dotnet build src -c Release -o bin
bin\ConsoleDesktop.exe                 (starts the watchdog and the desktop)
bin\ConsoleDesktop.exe --stock         (the escape hatch from a shell)
powershell -ExecutionPolicy Bypass -File installer\build.ps1 -Version 0.2.1   (makes dist\ConsoleDesktop-Setup.exe)
```
- `src/` holds the C#/.NET 8 + WebView2 host, and `web/` holds the page (`web/index.html`).
- `BRIDGE.md` describes the contract between the page and the host.
- `web/live/` holds the restyling files Marina edits, and its `README.md` explains every key.
- Set `CD_DEBUG_PORT=9340` before starting to get a DevTools port.

Image credits and licences are in [`web/assets/CREDITS.md`](web/assets/CREDITS.md). The icons are Crystal Clear (LGPL), the scenes come from Poly Haven HDRIs (CC0), and the Winamp skins are from the Winamp Skin Museum.
