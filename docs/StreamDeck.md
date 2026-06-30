# Snapture control protocol (Stream Deck integration)

Snapture exposes a control socket so external clients — primarily a future
**Elgato Stream Deck plugin** — can drive recording. This document is the
contract that plugin would implement against.

## Transport

- **TCP**, bound to `127.0.0.1` only (never exposed off-box).
- Default port **48910** (configurable in Settings → *Stream Deck server*).
- **Newline-delimited JSON (NDJSON):** every message is a single-line JSON
  object terminated by `\n`. The client sends *commands*; the server sends
  *responses* (one per command) and unsolicited *events*.
- Text encoding is UTF-8.

A Stream Deck plugin (Node.js) can connect with a plain `net.Socket` — no
WebSocket framing required:

```js
const net = require("net");
const sock = net.createConnection(48910, "127.0.0.1");
let buf = "";
sock.on("data", (d) => {
  buf += d;
  let i;
  while ((i = buf.indexOf("\n")) >= 0) {
    const msg = JSON.parse(buf.slice(0, i));
    buf = buf.slice(i + 1);
    handle(msg); // {type:"response"|"event", ...}
  }
});
sock.write(JSON.stringify({ id: "1", command: "start", args: { mode: "display" } }) + "\n");
```

## Resilience

- The server's accept loop auto-restarts with backoff if the listener faults.
- Each client connection is isolated; one client erroring never affects others.
- On connect, the server greets the client with a `hello` event carrying the
  current state, so a freshly (re)connected Stream Deck key can render the right
  icon immediately.
- Events are broadcast best-effort; a dead client is reaped, not awaited.

## Commands (client → server)

Every command may include an optional `id` (string); the matching response
echoes it so the client can correlate replies.

| `command` | `args` | Effect |
| --- | --- | --- |
| `getState` | — | Returns the current state. |
| `getSettings` | — | Returns video format / mode / fps / quality / library, plus a nested `snapshot` object. |
| `start` | `{ "mode": "display\|window\|custom" }` | Begin **video** recording. `display`/`window` capture the target **under the cursor instantly** (true one-press). `custom` opens the selection overlay. `mode` is optional; defaults to the configured default mode. |
| `snapshot` | `{ "mode": "display\|window\|custom" }` | Take a **still image**. `display`/`window` capture under the cursor instantly; `custom` opens the selection overlay. `mode` optional; defaults to the configured snapshot mode. |
| `stop` | — | Stop and finalize the current recording. |
| `abort` | — | Cancel selection or discard the in-progress recording. |
| `setFormat` | `{ "format": "mp4\|gif\|webp" }` | Change the video output format. |
| `setMode` | `{ "mode": "display\|window\|custom" }` | Change the default video capture mode. |
| `setSnapshotFormat` | `{ "format": "png\|jpeg\|webp" }` | Change the snapshot image format. |
| `setSnapshotMode` | `{ "mode": "display\|window\|custom" }` | Change the default snapshot capture mode. |

A completed snapshot pushes a `snapshotCompleted` event with `{ ok, path, error }`.

Example:

```json
{"id":"7","command":"start","args":{"mode":"display"}}
```

## Responses (server → client)

One response per command:

```json
{"type":"response","id":"7","ok":true,"state":"recording"}
{"type":"response","id":"8","ok":false,"error":"Not recording.","state":"idle"}
```

`getSettings` includes a `data` object:

```json
{"type":"response","id":"3","ok":true,"state":"idle",
 "data":{"format":"Mp4","mode":"Custom","frameRate":30,"quality":70,
         "library":"C:\\Users\\me\\Videos\\Snapture",
         "snapshot":{"format":"Png","mode":"Display","captureCursor":true,
                     "library":"C:\\Users\\me\\Pictures\\Snapture"}}}
```

## Events (server → client, unsolicited)

```json
{"type":"event","event":"hello","state":"idle"}
{"type":"event","event":"stateChanged","state":"recording"}
{"type":"event","event":"recordingCompleted","state":"idle",
 "data":{"ok":true,"path":"C:\\Users\\me\\Videos\\Snapture\\Snapture_2026-06-30_15-22-01.mp4","error":null}}
```

### States

`idle` → `selecting` → `recording` → `encoding` → `idle`

A Stream Deck key would typically map:
- `idle` → "record" icon, pressing sends `start`.
- `recording` → "stop" icon (often with the elapsed indicator), pressing sends `stop`.
- `encoding` → transient "saving" icon.

## Suggested Stream Deck actions

- **Quick Record (Display)** — `start` with `mode: "display"`; toggles to `stop`.
- **Quick Record (Window)** — `start` with `mode: "window"`.
- **Region Record** — `start` with `mode: "custom"` (opens the overlay).
- **Stop** — `stop`.
- **Cycle Format** — `setFormat` between mp4/gif/webp, reflecting `getSettings`.
