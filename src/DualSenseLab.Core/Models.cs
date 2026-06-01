using System.Text.Json.Serialization;

namespace DualSenseLab.Core;

public enum ConnectionType { Usb, Bluetooth, Simulator }
public enum TriggerPreset { Off, SoftResistance, HardStop, ProgressiveResistance, Pulse, WeaponBreakpoint, Custom }
public enum ResponseCurve { Linear, Precision, Aggressive }

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
    DateTimeOffset Timestamp);

public sealed class TriggerConfiguration
{
    public TriggerPreset Preset { get; set; } = TriggerPreset.Off;
    public int StartPosition { get; set; } = 20;
    public int EndPosition { get; set; } = 80;
    public int Strength { get; set; } = 35;
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
