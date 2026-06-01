# Technical Notes

Great Controller Emulator (`GCE`) keeps the original internal project names to
avoid unnecessary namespace churn. The shipped desktop executable is `GCE.exe`.

## Architecture

- `DualSenseLab.App`: WPF desktop interface and workflow orchestration.
- `DualSenseLab.Core`: controller interface, USB/Bluetooth DualSense HID
  adapter, neutral manual simulator, ViGEm Xbox 360 output adapter, models,
  validation, calibration analysis, mapping curves, serialization, and
  redacted export.
- `DualSenseLab.Tests`: unit and simulator integration coverage.

`IControllerDevice` is the physical-hardware boundary. The WPF app currently
prefers `DualSenseUsbControllerDevice` when a DualSense HID interface is available
and falls back to `SimulatedControllerDevice`, which publishes explicit
user-selected state and never generates random input. `IVirtualControllerOutput`
is a separate game-facing boundary implemented by `ViGEmXbox360Output`.
Physical DualSense output packet assembly remains isolated from UI code.

## Protocol references

Raw HID output packet assembly follows the maintained references below:

- Sony product overview:
  <https://www.playstation.com/en-us/accessories/dualsense-wireless-controller/>
- Parsed USB and Bluetooth descriptors, input report maps, sample reports, and
  known output capabilities:
  <https://github.com/nondebug/dualsense>
- SDL HIDAPI implementation for Sony controllers:
  <https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_ps5.c>
- SDL maintainers discussing raw DualSense effects support and the Steamworks
  `isteamdualsense.h` structures:
  <https://github.com/libsdl-org/SDL/issues/5125>
- Current DS4Windows fork documenting DualSense support and optional
  virtual-controller driver requirements:
  <https://github.com/ds4windowsapp/DS4Windows>

## Hardware implementation notes

- USB and Bluetooth input reports are parsed independently.
- USB and Bluetooth output reports are assembled independently.
- Bluetooth output uses the Sony/Linux-compatible CRC32 seed and framing.
- Device discovery targets Sony VID `0x054C` and DualSense PID `0x0CE6`.
- HID stream reconnects retry after transient read failures.

## Safety rules

- Validate trigger ranges and strength before calling a controller adapter.
- Keep an always-visible reset action while editing effects.
- Use conservative vibration defaults.
- Do not install drivers or enable virtual-controller dependencies implicitly.
- Describe stick correction as software mitigation, not physical repair.
