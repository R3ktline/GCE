# Technical Notes

Great Controller Emulator (`GCE`) keeps the original internal project names to
avoid unnecessary namespace churn. The shipped desktop executable is `GCE.exe`.

## Architecture

- `DualSenseLab.App`: WPF desktop interface and workflow orchestration.
- `DualSenseLab.Core`: controller interface, simulator, models, validation,
  calibration analysis, mapping curves, serialization, and redacted export.
- `DualSenseLab.Tests`: unit and simulator integration coverage.

`IControllerDevice` is the hardware boundary. The WPF app depends on the
interface and currently creates `SimulatedControllerDevice`. A future physical
adapter can be added without moving packet assembly into UI code.

## Protocol references

No raw HID output packet assembly is implemented in this MVP. These references
were reviewed to establish the constraints for a future adapter:

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

## Hardware implementation checklist

Before physical writes are enabled:

1. Parse USB and Bluetooth input reports independently.
2. Assemble USB and Bluetooth output reports independently.
3. Confirm CRC handling and transport behavior from a maintained implementation.
4. Add device discovery for Sony VID `0x054C` and DualSense PID `0x0CE6`.
5. Add reconnect tests, unsupported-feature reporting, and redacted logging.
6. Test reset-effect packets on USB hardware before exposing live effects.
7. Keep Bluetooth output opt-in until verified against supported Windows stacks.

## Safety rules

- Validate trigger ranges and strength before calling a controller adapter.
- Keep an always-visible reset action while editing effects.
- Use conservative vibration defaults.
- Do not install drivers or enable virtual-controller dependencies implicitly.
- Describe stick correction as software mitigation, not physical repair.
