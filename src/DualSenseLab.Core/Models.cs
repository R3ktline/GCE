using System.Text.Json.Serialization;

namespace DualSenseLab.Core;

public enum ConnectionType { Usb, Bluetooth, Simulator }
public enum TriggerPreset { Off, SoftResistance, HardStop, ProgressiveResistance, Pulse, WeaponBreakpoint, Custom }
public enum ResponseCurve { Linear, Precision, Aggressive }
public enum MicrophoneLedMode : byte { Off, On, Pulse }

public sealed record ControllerInfo(
    string DisplayName,
    string DeviceId,
    ConnectionType Connection,
    int? BatteryPercent,
    string Firmware,
    bool SupportsOutput,
    string OutputNote);

public sealed record StickPoint(double X, double Y);

public sealed record InputState(
    StickPoint LeftStick,
    StickPoint RightStick,
    double LeftTrigger,
    double RightTrigger,
    IReadOnlySet<string> Buttons,
    StickPoint Gyroscope,
    StickPoint Accelerometer,
    StickPoint Touchpad,
    DateTimeOffset Timestamp,
    int? BatteryPercent = null,
    bool TouchpadActive = false,
    double GyroscopeZ = 0,
    double AccelerometerZ = 0,
    StickPoint? SecondaryTouchpad = null,
    bool SecondaryTouchpadActive = false,
    bool BatteryCharging = false,
    bool HeadsetConnected = false,
    bool MicrophoneConnected = false,
    bool MicrophoneMuted = false);

public sealed class VirtualControllerConfiguration
{
    public double LeftStickX { get; set; }
    public double LeftStickY { get; set; }
    public double RightStickX { get; set; }
    public double RightStickY { get; set; }
    public double LeftTrigger { get; set; }
    public double RightTrigger { get; set; }
    public HashSet<string> Buttons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public InputState ToInputState() => new(
        new StickPoint(LeftStickX, LeftStickY),
        new StickPoint(RightStickX, RightStickY),
        LeftTrigger,
        RightTrigger,
        new HashSet<string>(Buttons, StringComparer.OrdinalIgnoreCase),
        new StickPoint(0, 0),
        new StickPoint(0, 0),
        new StickPoint(.5, .5),
        DateTimeOffset.UtcNow);
}

public sealed class TriggerConfiguration
{
    public TriggerPreset Preset { get; set; } = TriggerPreset.Off;
    public int StartPosition { get; set; } = 20;
    public int EndPosition { get; set; } = 80;
    public int Strength { get; set; } = 35;
}

public sealed class ControllerAudioConfiguration
{
    public byte HeadphoneVolume { get; set; } = 80;
    public byte SpeakerVolume { get; set; } = 80;
    public byte MicrophoneVolume { get; set; } = 40;
    public bool RouteToInternalSpeaker { get; set; }
    public bool ForceInternalMicrophone { get; set; }
    public bool ForceHeadsetMicrophone { get; set; }
    public bool AudioMuted { get; set; }
    public byte SpeakerPreampGain { get; set; } = 4;
}

public sealed class StickCompensation
{
    public double Deadzone { get; set; } = 0.08;
    public double AntiDeadzone { get; set; }
    public double Smoothing { get; set; } = 0.12;
    public double CenterOffsetX { get; set; }
    public double CenterOffsetY { get; set; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ResponseCurve Curve { get; set; } = ResponseCurve.Linear;
}

public sealed class ControllerProfile
{
    public string Name { get; set; } = "Safe Default";
    public TriggerConfiguration LeftTrigger { get; set; } = new();
    public TriggerConfiguration RightTrigger { get; set; } = new();
    public StickCompensation LeftStick { get; set; } = new();
    public StickCompensation RightStick { get; set; } = new();
    public string LightBarColor { get; set; } = "#6C63FF";
    public int VibrationStrength { get; set; } = 20;
    public string AssignedExecutable { get; set; } = "";
    public bool AutoApply { get; set; }
    public VirtualControllerConfiguration VirtualController { get; set; } = new();

    public static ControllerProfile SafeDefault() => new();
}

public sealed record CalibrationReport(
    int SampleCount,
    double CenterOffset,
    double Jitter,
    double Circularity,
    double Drift,
    int Confidence,
    bool DriftDetected,
    string Summary);
