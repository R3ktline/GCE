# Great Controller Emulator

Great Controller Emulator (`GCE`) is a dark Windows desktop utility MVP for experimenting with Sony
DualSense controller diagnostics, adaptive-trigger profiles, and software stick
drift mitigation. The current build reads a connected USB or Bluetooth DualSense when one is
available, falls back to manual simulation, and does not send unverified HID
packets to a physical controller.

## Requirements

- Windows 10 or later
- .NET 7 SDK to build
- .NET Desktop Runtime 7 to run from source; the published `dist` build is self-contained
- [ViGEmBus](https://github.com/nefarius/ViGEmBus/releases) for opt-in virtual Xbox 360 controller output

## Run

Launch the self-contained Windows build:

```powershell
.\dist\GCE-win-x64\GCE.exe
```

Or run from source:

```powershell
dotnet run --project .\src\DualSenseLab.App\DualSenseLab.App.csproj
```

## Build and test

```powershell
dotnet build .\DualSenseLab.sln
dotnet test .\DualSenseLab.sln
```

## Included workflows

- Dashboard with connection metadata and live physical or simulator input
- Independent left and right physical adaptive-trigger editor with validation and reset
- Timed stick-center calibration with drift score and confidence
- Software mapping controls for deadzone, anti-deadzone, and smoothing
- Live stick, button, motion-sensor, trigger, and touchpad visualization
- Locally persisted profile create, rename, duplicate, import, export, delete, and per-game auto-apply operations
- Physical light-bar, player LED, microphone LED/mute, vibration, audio routing,
  audio volume, LED brightness, and effect-power controls
- Redacted compatibility diagnostics export
- Opt-in ViGEmBus virtual Xbox 360 output with explicit sticks, triggers, and buttons

## Supported connection modes

| Mode | Input | Adaptive triggers | Light bar | Vibration |
| --- | --- | --- | --- | --- |
| Simulator | Yes | Yes, simulated | Yes, simulated | Yes, simulated |
| USB physical controller | Yes | Yes | Yes | Yes |
| Bluetooth physical controller | Yes | Yes | Yes | Yes |

Physical HID access stays behind the `IControllerDevice` interface. USB and
Bluetooth use their distinct report structures, including Bluetooth output
sequence tags and CRC32 framing.

Motion values are normalized with each controller's calibration feature report.
Pairing identity and firmware, hardware, and feature versions are read from the
controller when available.

## Important limitations

- Drift correction is software mapping only. It does not physically repair a
  controller.
- Virtual-controller output requires ViGEmBus. GCE never installs or enables
  the driver automatically.
- The ViGEm Xbox 360 output path is separate from physical DualSense HID
  effects.
- Profiles are stored under `%LOCALAPPDATA%\GreatControllerEmulator\profiles.json`.

## Troubleshooting

- If the app does not launch, install the .NET Desktop Runtime.
- If a profile import fails, confirm the file is valid GCE JSON.
- Use **Reset Effects** in the sidebar whenever an effect should be cleared.
- Export a diagnostics report from **Stick Calibration** for compatibility
  investigation. Device identifiers are redacted.

See [TECHNICAL_NOTES.md](TECHNICAL_NOTES.md) for protocol research and the
hardware-adapter boundary.
