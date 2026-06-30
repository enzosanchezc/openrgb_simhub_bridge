# OpenRGB SimHub Bridge

A single [SimHub](https://www.simhubdash.com/) plugin that mirrors the current **race flag**
onto one or more [OpenRGB](https://openrgb.org/)-controlled devices — works with any device
OpenRGB exposes (keyboard, mouse, RAM, …).

```
iRacing / ACC / ...  ─▶  SimHub (Flag_* telemetry)  ─▶  OpenRgbSimhubBridge.dll
                                                              │  OpenRGB SDK (TCP 6742)
                                                              ▼
                                                       OpenRGB  ─▶  device(s)
```

**One plugin, nothing on the OpenRGB side.** OpenRGB already exposes a network SDK server and
already supports a huge range of devices, so the bridge just connects to that server as a client. No
OpenRGB plugin, no serial bridge, no extra dependencies (the OpenRGB SDK client is hand-rolled and
speaks protocol version 0).

## Requirements

- **OpenRGB** running, with **Settings → SDK Server → Start** enabled (default port `6742`).
- **SimHub** installed.
- To build from source: the **.NET SDK** (any recent version — the project targets .NET Framework
  4.8 via the `Microsoft.NETFramework.ReferenceAssemblies` package, so Visual Studio is *not* required).

## Build & install

```sh
dotnet build OpenRgbSimhubBridge.csproj -c Release
```

This builds `bin/Release/OpenRgbSimhubBridge.dll` and tries to copy it into
`C:\Program Files (x86)\SimHub`. If your SimHub is elsewhere, or the copy is skipped (writing to
`Program Files` may need an elevated shell), pass the path and/or copy manually:

```sh
dotnet build OpenRgbSimhubBridge.csproj -c Release -p:SimHubPath="D:\Games\SimHub"
```

Then **restart SimHub**. Enable the plugin if prompted (`Settings → Plugins`).

## Configuration

Configure everything from **SimHub → left menu → "OpenRGB Flag Bridge"**: pick the device(s) to
light (click **Refresh** to detect them while OpenRGB's SDK server is running), set brightness,
flash speed, the idle colour, and each flag's colour/flash. Changes apply live — no restart — and
are saved automatically.

Those settings are still persisted to `OpenRgbSimhubBridge.json` next to the DLL (written on first
run, kept in sync by the UI). You can edit it by hand if you prefer; restart SimHub to pick up
manual edits.

```jsonc
{
  "Devices": [ "My Keyboard" ], // one or more device names (or substrings), case-insensitive; empty = none
  "Host": "127.0.0.1",
  "Port": 6742,
  "Brightness": 1.0,            // 0..1 multiplier on every colour
  "FlashHz": 2.0,              // flashing speed, full on/off cycles per second
  "OnlyWhenGameRunning": true, // leave the devices alone unless a game is running
  "IdleColor": null,           // "#101010" for an idle colour, or null = off when no flag
  "Flags": {
    "Green":     { "Color": "#00FF00", "Flash": false },
    "Yellow":    { "Color": "#FFB400", "Flash": true  },
    "Blue":      { "Color": "#0040FF", "Flash": false },
    "White":     { "Color": "#FFFFFF", "Flash": false },
    "Black":     { "Color": "#FF0000", "Flash": false },
    "Orange":    { "Color": "#FF5000", "Flash": true  }, // meatball / mechanical black
    "Checkered": { "Color": "#FFFFFF", "Flash": true  }
  }
}
```

`Devices` matches every OpenRGB device whose name contains any listed entry (case-insensitive), so
the same flag colour can drive several devices (keyboard, mouse, RAM, …) at once. On first run the
list is empty — pick your device(s) in the settings page.

When several flags are active at once, the highest-priority one wins, in this order:

> Checkered → Black → Orange → Yellow → Blue → White → Green

## How it works

- `DataUpdate()` (runs every SimHub frame) only reads `data.NewData.Flag_*` and records the
  highest-priority active flag. It does no network I/O, so it stays cheap.
- A background thread (~40 Hz) connects to OpenRGB, finds the configured device(s), switches each to
  its Direct/Custom mode, and pushes a single colour to all of their LEDs — re-sending only when the
  colour changes, and driving the flash animation. If OpenRGB isn't running it retries every 2 s.

## Testing

1. Start OpenRGB (SDK server on) and SimHub.
2. Launch a game with good flag data — **iRacing** exposes the full set; **ACC** reports a subset.
3. Trigger flags (e.g. a green start, a local yellow). The selected device(s) should follow.

No game handy? Open `OpenRgbSimhubBridge.log` (next to the DLL) to confirm the plugin bound to your
device(s) — you'll see a line like `Bound 1 device(s): #6 'My Keyboard' (107 LEDs)`.

## Troubleshooting

Check `OpenRgbSimhubBridge.log` first (or the status line in the settings page).

| Symptom | Fix |
|---|---|
| `no device matched` | Click **Refresh** in the settings page and tick the right device; the name must match what OpenRGB detects. |
| Connection errors / retrying | OpenRGB not running, or its **SDK Server** isn't started, or wrong `Port`. |
| A device doesn't change | Make sure no other app (vendor RGB software, SignalRGB) is fighting OpenRGB for that device. |
| Nothing while in menus | `OnlyWhenGameRunning` is on by default — turn it off to always drive the device(s). |

## Notes / limitations

- **Flag availability is game-dependent.** SimHub can only show what the game reports — iRacing is the
  most complete; some titles (e.g. ACC) expose fewer flags. Flags the game never sets simply never light.
- Whole-device single colour by design (per-key/zone effects were intentionally out of scope).
