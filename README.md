# Great Controller Emulator

Great Controller Emulator (`GCE`) is a dark Windows desktop utility MVP for experimenting with Sony
DualSense controller diagnostics, adaptive-trigger profiles, and software stick
drift mitigation. The current build is simulator-first: it is useful without
hardware and does not send unverified HID packets to a physical controller.

## Requirements

- Windows 10 or later
- .NET 7 SDK to build
- .NET Desktop Runtime 7 to run

## Run

```powershell
dotnet run --project .\src\DualSenseLab.App\DualSenseLab.App.csproj
```

## Build and test

```powershell
dotnet build .\DualSenseLab.sln
dotnet test .\DualSenseLab.sln
```

## Included workflows

- Dashboard with connection metadata and live simulator input
- Independent left and right adaptive-trigger editor with validation and reset
- Timed stick-center calibration with drift score and confidence
- Software mapping controls for deadzone, anti-deadzone, and smoothing
- Live stick, button, motion-sensor, trigger, and touchpad visualization
- Profile create, rename, duplicate, import, export, and delete operations
- Light-bar and conservative vibration tests against the simulator
- Redacted compatibility diagnostics export

## Supported connection modes

| Mode | Input | Adaptive triggers | Light bar | Vibration |
| --- | --- | --- | --- | --- |
| Simulator | Yes | Yes, simulated | Yes, simulated | Yes, simulated |
| USB physical controller | Not yet enabled | Not yet enabled | Not yet enabled | Not yet enabled |
| Bluetooth physical controller | Not yet enabled | Not yet enabled | Not yet enabled | Not yet enabled |

Physical HID access is intentionally left behind the `IControllerDevice`
interface. USB and Bluetooth use different report structures. The application
will not guess packet formats or imply that output support is universal.

## Important limitations

- Drift correction is software mapping only. It does not physically repair a
  controller and games do not receive the mapped output in this MVP.
- Virtual-controller output is disabled. A future implementation would require
  an explicit, user-approved dependency such as ViGEmBus.
- Per-game executable assignments are stored in profiles, but background
  process monitoring is not enabled yet.
- Firmware discovery, player LEDs, and physical reconnect handling belong in a
  future verified HID adapter.

## Troubleshooting

- If the app does not launch, install the .NET Desktop Runtime.
- If a profile import fails, confirm the file is valid GCE JSON.
- Use **Reset Effects** in the sidebar whenever an effect should be cleared.
- Export a diagnostics report from **Stick Calibration** for compatibility
  investigation. Device identifiers are redacted.

See [TECHNICAL_NOTES.md](TECHNICAL_NOTES.md) for protocol research and the
hardware-adapter boundary.
