# Snapture

A small Windows system-tray utility to record your screen (a display, a window,
or a custom region) and quick-save it as **MP4**, **animated WebP**, or **GIF**.

The selection experience mirrors the built-in `Win+Shift+S` snip: a dimmed
overlay across all monitors, a floating toolbar at the top, pixel-precise
arrow-key nudging, and `Enter`/`Esc` to confirm/cancel. Once recording starts,
the dim overlay disappears and only a compact recording bar remains so the rest
of your desktop stays fully usable.

It is also designed to be driven by an external client — most notably a future
**Elgato Stream Deck plugin** — through a resilient localhost control socket.

## Solution layout

```
Snapture.slnx
├── src/Snapture.Core/      Headless engine — no UI dependency. The Stream Deck
│   │                       plugin drives exactly this surface.
│   ├── Settings/           AppSettings + SettingsService (%APPDATA%\Snapture\settings.json)
│   ├── Models/             OutputFormat, CaptureMode, CaptureRegion, CaptureTarget, RecordingState
│   ├── Capture/            IFrameSource + GdiFrameSource (BGRA frames), Frame pool
│   ├── Encoding/           FfmpegLocator + FfmpegEncoder (raw frames piped to ffmpeg stdin)
│   ├── Recording/          RecordingController — the state machine + capture loop
│   └── Ipc/                ControlServer + NDJSON protocol (Stream Deck)
└── src/Snapture.App/       WPF tray application
    ├── Tray/               Runtime-generated tray icons
    ├── Views/              OverlayWindow, CaptureToolbarWindow, SelectionModel, Theme
    ├── Interop/            Win32 helpers (virtual-desktop geometry, window hit-test)
    ├── MainWindow          Settings window
    └── AppController       Wires tray + windows + controller + control server
```

### Architectural notes

- **One code path for every trigger.** Tray click, the settings *Start capture*
  button, and the IPC `start` command all funnel through `RecordingController`,
  so behaviour is identical regardless of how a recording begins.
- **Capture backend is swappable.** `GdiFrameSource` (dependency-free
  `BitBlt`) is the v0 backend. It sits behind `IFrameSource`, so a
  `Windows.Graphics.Capture` backend can replace it later without touching the
  encoder or controller. (GDI does not capture hardware-protected/DRM surfaces
  and tops out at moderate frame rates — fine for desktop/window/region clips.)
- **Pixel-exact coordinates.** Everything downstream of the overlay works in
  physical pixels on the virtual desktop. The app is Per-Monitor-V2 DPI aware
  (see `app.manifest`).

## Requirements

- Windows 10 1903+ / Windows 11
- .NET 8 SDK (the projects target `net8.0-windows`)
- **ffmpeg** — see below

### Bundling ffmpeg

`FfmpegEncoder` shells out to `ffmpeg`. At runtime it looks for it in this order:

1. an explicitly configured path,
2. `ffmpeg\ffmpeg.exe` or `ffmpeg.exe` next to `Snapture.exe`,
3. `ffmpeg` on `PATH`.

For distribution, drop `ffmpeg.exe` into a `ffmpeg\` folder beside the built
`Snapture.exe`. During development, any `ffmpeg` on `PATH` works.

## Build & run

```sh
dotnet build Snapture.slnx
dotnet run --project src/Snapture.App
```

The app starts to the system tray (no main window). **Left-click the tray icon**
(or *Start capture* in Settings) to begin an area selection.

### Keyboard / mouse during selection

| Action | Result |
| --- | --- |
| Drag (Custom mode) | Draw the capture rectangle |
| Drag a handle | Resize that edge/corner |
| Drag inside | Move the whole rectangle |
| Arrow keys | Nudge the whole rectangle by 1px |
| Arrow keys *while holding a handle* | Nudge just that edge/corner by 1px |
| Click a monitor/window (Display/Window mode) | Select & start it |
| `Enter` | Start recording the current selection |
| `Esc` | Cancel and restore the desktop |

## Stream Deck / external control

See [`docs/StreamDeck.md`](docs/StreamDeck.md) for the full control protocol.
In short: a localhost TCP server (default port **48910**) speaks
newline-delimited JSON. A Stream Deck plugin (or any client) can `start`,
`stop`, `abort`, query state, and receive live state events.
