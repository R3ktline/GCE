using DualSenseLab.Core;

namespace DualSenseLab.Tests;

public class CoreTests
{
    [Fact]
    public void Profile_round_trips()
    {
        var profile = ControllerProfile.SafeDefault();
        profile.Name = "Racing";
        profile.LeftStick.Deadzone = .14;
        profile.VirtualController.LeftStickX = .75;
        profile.VirtualController.Buttons.Add("A");
        var result = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));
        Assert.Equal("Racing", result.Name);
        Assert.Equal(.14, result.LeftStick.Deadzone);
        Assert.Equal(.75, result.VirtualController.LeftStickX);
        Assert.Contains("A", result.VirtualController.Buttons);
    }

    [Fact]
    public void Trigger_validator_rejects_reversed_range() =>
        Assert.Throws<ArgumentException>(() => TriggerValidator.EnsureValid(new TriggerConfiguration { StartPosition = 80, EndPosition = 20 }));

    [Fact]
    public void Mapper_applies_deadzone() =>
        Assert.Equal(new StickPoint(0, 0), StickMapper.Apply(new StickPoint(.02, -.03), new StickCompensation { Deadzone = .08 }));

    [Fact]
    public void Virtual_mapper_applies_center_offset_deadzone_and_smoothing()
    {
        var input = new VirtualControllerConfiguration { LeftStickX = .6 };
        var compensation = new StickCompensation { Deadzone = .1, CenterOffsetX = .1, Smoothing = .5 };
        var previous = new VirtualControllerConfiguration { LeftStickX = .2 };
        var mapped = VirtualControllerMapper.Apply(input, compensation, new(), previous);
        Assert.Equal(.3222, mapped.LeftStickX, 4);
    }

    [Fact]
    public void Profile_library_round_trips_multiple_profiles()
    {
        var profiles = new[] { new ControllerProfile { Name = "One" }, new ControllerProfile { Name = "Two" } };
        var result = ProfileSerializer.DeserializeLibrary(ProfileSerializer.SerializeLibrary(profiles));
        Assert.Equal(new[] { "One", "Two" }, result.Select(profile => profile.Name));
    }

    [Fact]
    public void Analyzer_detects_center_drift()
    {
        var samples = Enumerable.Range(0, 120).Select(index => new StickPoint(.15 + Math.Sin(index) * .005, .01)).ToArray();
        var report = CalibrationAnalyzer.Analyze(samples);
        Assert.True(report.DriftDetected);
        Assert.Equal(100, report.Confidence);
    }

    [Fact]
    public void Simulator_applies_and_resets_effects()
    {
        using var simulator = new SimulatedControllerDevice();
        simulator.ApplyTriggers(new TriggerConfiguration { Preset = TriggerPreset.SoftResistance }, new TriggerConfiguration { Preset = TriggerPreset.HardStop });
        Assert.Equal(TriggerPreset.HardStop, simulator.LastRightTrigger.Preset);
        simulator.ResetEffects();
        Assert.Equal(TriggerPreset.Off, simulator.LastRightTrigger.Preset);
    }

    [Fact]
    public void Simulator_starts_neutral_instead_of_generating_random_input()
    {
        using var simulator = new SimulatedControllerDevice();
        InputState? received = null;
        using var frameReceived = new ManualResetEventSlim();
        simulator.InputReceived += (_, state) => { received = state; frameReceived.Set(); };
        simulator.Start();
        Assert.True(frameReceived.Wait(TimeSpan.FromSeconds(1)));
        Assert.Equal(new StickPoint(0, 0), received!.LeftStick);
        Assert.Equal(new StickPoint(0, 0), received.RightStick);
        Assert.Empty(received.Buttons);
    }

    [Fact]
    public void Usb_parser_reads_sticks_triggers_and_buttons()
    {
        var report = new byte[64];
        report[0] = 0x01; report[1] = 0xFF; report[2] = 0x00; report[3] = 0x80; report[4] = 0x80;
        report[5] = 0x7F; report[6] = 0xFF; report[8] = 0x20; report[9] = 0x02;
        var state = DualSenseUsbReportParser.Parse(report);
        Assert.Equal(1, state.LeftStick.X);
        Assert.Equal(1, state.LeftStick.Y);
        Assert.Equal(0, state.RightStick.X);
        Assert.Equal(1, state.RightTrigger);
        Assert.Contains("Cross", state.Buttons);
        Assert.Contains("R1", state.Buttons);
    }

    [Fact]
    public void Usb_parser_reads_sensors_touchpad_and_battery()
    {
        var report = new byte[64];
        report[0] = 0x01; report[1] = 0x80; report[2] = 0x80; report[3] = 0x80; report[4] = 0x80;
        report[16] = 0x34; report[17] = 0x12; report[22] = 0x78; report[23] = 0x56;
        report[33] = 0x01; report[34] = 0x80; report[35] = 0x73; report[36] = 0x21; report[53] = 0x07;
        var state = DualSenseUsbReportParser.Parse(report);
        Assert.Equal(0x1234, state.Gyroscope.X);
        Assert.Equal(0x5678, state.Accelerometer.X);
        Assert.True(state.TouchpadActive);
        Assert.Equal(75, state.BatteryPercent);
    }

    [Fact]
    public void Usb_output_report_contains_effects_leds_and_vibration()
    {
        var output = new DualSenseOutputState
        {
            RightMotor = 10, LeftMotor = 20, MicrophoneLed = true, MicrophoneMuted = true,
            PlayerLeds = 0x1F, LightbarRed = 1, LightbarGreen = 2, LightbarBlue = 3,
            RightTrigger = DualSenseTriggerEffect.Feedback(2, 4),
            LeftTrigger = DualSenseTriggerEffect.Weapon(2, 5, 6)
        };
        var report = DualSenseOutputReport.BuildUsb(output);
        Assert.Equal(48, report.Length);
        Assert.Equal(0x02, report[0]);
        Assert.Equal(10, report[3]); Assert.Equal(20, report[4]);
        Assert.Equal(0x21, report[11]); Assert.Equal(0x25, report[22]);
        Assert.Equal(0x1F, report[44]);
        Assert.Equal(new byte[] { 1, 2, 3 }, report[45..48]);
    }

    [Fact]
    public void Bluetooth_output_report_has_transport_header_and_crc()
    {
        var report = DualSenseOutputReport.BuildBluetooth(new DualSenseOutputState { LightbarRed = 1 }, 3);
        Assert.Equal(78, report.Length);
        Assert.Equal(0x31, report[0]);
        Assert.Equal(0x30, report[1]);
        Assert.Equal(0x10, report[2]);
        Assert.Equal(DualSenseOutputReport.ComputeBluetoothCrc(report.AsSpan(0, 74)),
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(74)));
    }

    [Fact]
    public void Bluetooth_minimal_parser_reads_buttons_and_triggers()
    {
        var state = DualSenseBluetoothReportParser.Parse(new byte[] { 0x01, 0x80, 0x80, 0x80, 0x80, 0x20, 0x02, 0, 0x7F, 0xFF });
        Assert.Contains("Cross", state.Buttons);
        Assert.Contains("R1", state.Buttons);
        Assert.Equal(1, state.RightTrigger);
    }

    [Theory]
    [InlineData(-1, short.MinValue)]
    [InlineData(0, 0)]
    [InlineData(1, short.MaxValue)]
    public void ViGEm_axis_conversion_uses_full_xinput_range(double input, short expected) =>
        Assert.Equal(expected, ViGEmXbox360Output.ToAxis(input));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 255)]
    public void ViGEm_trigger_conversion_uses_full_xinput_range(double input, byte expected) =>
        Assert.Equal(expected, ViGEmXbox360Output.ToSlider(input));

    [Fact]
    public void Logger_redacts_device_identifier()
    {
        var writer = new StringWriter();
        new JsonLabLogger(writer).Info("connected", "Controller connected", "SIM-054C-SECRET-DEMO");
        Assert.DoesNotContain("SECRET", writer.ToString());
        Assert.Contains("SIM***EMO", writer.ToString());
    }
}
