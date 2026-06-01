using System.Text.Json;

namespace DualSenseLab.Core;

public interface ILabLogger
{
    void Info(string eventName, string message, string? deviceId = null);
}

public sealed class JsonLabLogger : ILabLogger
{
    private readonly TextWriter _writer;
    public JsonLabLogger(TextWriter writer) => _writer = writer;

    public void Info(string eventName, string message, string? deviceId = null) =>
        _writer.WriteLine(JsonSerializer.Serialize(new
        {
            timestampUtc = DateTimeOffset.UtcNow,
            level = "Information",
            eventName,
            message,
            deviceId = deviceId is null ? null : DiagnosticsExporter.Redact(deviceId)
        }));
}

public interface IControllerDevice : IDisposable
{
    ControllerInfo Info { get; }
    event EventHandler<InputState>? InputReceived;
    void Start();
    void Stop();
    void ApplyTriggers(TriggerConfiguration left, TriggerConfiguration right);
    void ResetEffects();
    void TestVibration(int strength);
    void SetLightBar(string hexColor);
}

public sealed class SimulatedControllerDevice : IControllerDevice
{
    private readonly System.Timers.Timer _timer = new(40);
    private readonly Random _random = new(4815);
    private double _phase;
    public ControllerInfo Info { get; } = new(
        "DualSense Simulator", "SIM-054C-0CE6-DEMO", ConnectionType.Simulator, 82,
        "Simulator 1.0", true, "Safe simulated output. No HID packets are sent.");
    public event EventHandler<InputState>? InputReceived;
    public TriggerConfiguration LastLeftTrigger { get; private set; } = new();
    public TriggerConfiguration LastRightTrigger { get; private set; } = new();

    public SimulatedControllerDevice() => _timer.Elapsed += (_, _) => PublishFrame();
    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void Dispose() => _timer.Dispose();

    public void ApplyTriggers(TriggerConfiguration left, TriggerConfiguration right)
    {
        TriggerValidator.EnsureValid(left);
        TriggerValidator.EnsureValid(right);
        LastLeftTrigger = Clone(left);
        LastRightTrigger = Clone(right);
    }

    public void ResetEffects()
    {
        LastLeftTrigger = new();
        LastRightTrigger = new();
    }

    public void TestVibration(int strength)
    {
        if (strength is < 0 or > 60) throw new ArgumentOutOfRangeException(nameof(strength), "Conservative test strength must be between 0 and 60.");
    }

    public void SetLightBar(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor) || !hexColor.StartsWith("#") || hexColor.Length != 7)
            throw new ArgumentException("Use a six-digit hex color such as #6C63FF.", nameof(hexColor));
    }

    private void PublishFrame()
    {
        _phase += 0.065;
        var noise = () => (_random.NextDouble() - .5) * .025;
        var left = new StickPoint(Math.Sin(_phase) * .38 + .045 + noise(), Math.Cos(_phase * .9) * .30 + noise());
        var right = new StickPoint(Math.Sin(_phase * .72) * .52 + noise(), Math.Cos(_phase * .63) * .44 + noise());
        var buttons = new HashSet<string>();
        if (((int)(_phase * 3)) % 9 == 0) buttons.Add("Cross");
        if (((int)(_phase * 2)) % 13 == 0) buttons.Add("R1");
        InputReceived?.Invoke(this, new InputState(
            left, right, (.5 + Math.Sin(_phase)) / 2, (.5 + Math.Cos(_phase * .8)) / 2,
            buttons, new StickPoint(Math.Sin(_phase) * 18, Math.Cos(_phase) * 14),
            new StickPoint(Math.Sin(_phase * .4) * .7, Math.Cos(_phase * .4) * .6),
            new StickPoint(.5 + Math.Sin(_phase * .6) * .38, .5 + Math.Cos(_phase * .7) * .32),
            DateTimeOffset.UtcNow));
    }

    private static TriggerConfiguration Clone(TriggerConfiguration source) => new()
    {
        Preset = source.Preset, StartPosition = source.StartPosition,
        EndPosition = source.EndPosition, Strength = source.Strength
    };
}

public static class TriggerValidator
{
    public static void EnsureValid(TriggerConfiguration config)
    {
        if (config.StartPosition is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(config.StartPosition));
        if (config.EndPosition is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(config.EndPosition));
        if (config.StartPosition >= config.EndPosition) throw new ArgumentException("Resistance start must be before its end.");
        if (config.Strength is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(config.Strength));
        if (config.Preset == TriggerPreset.Off && config.Strength > 0) config.Strength = 0;
    }
}

public static class CalibrationAnalyzer
{
    public static CalibrationReport Analyze(IReadOnlyCollection<StickPoint> samples)
    {
        if (samples.Count < 10) throw new ArgumentException("At least 10 samples are needed.", nameof(samples));
        var centerX = samples.Average(p => p.X);
        var centerY = samples.Average(p => p.Y);
        var centerOffset = Math.Sqrt(centerX * centerX + centerY * centerY);
        var radii = samples.Select(p => Math.Sqrt(p.X * p.X + p.Y * p.Y)).ToArray();
        var averageRadius = radii.Average();
        var jitter = Math.Sqrt(samples.Average(p => Math.Pow(p.X - centerX, 2) + Math.Pow(p.Y - centerY, 2)));
        var circularity = averageRadius == 0 ? 1 : Math.Clamp(1 - StandardDeviation(radii) / averageRadius, 0, 1);
        var drift = Math.Clamp(centerOffset + jitter * .2, 0, 1);
        var detected = drift >= .08;
        var confidence = Math.Clamp((int)Math.Round(Math.Min(1, samples.Count / 120d) * 100), 10, 100);
        return new(samples.Count, centerOffset, jitter, circularity, drift, confidence, detected,
            detected ? "Software compensation is recommended. This does not physically repair the stick."
                     : "No significant center drift was detected in this session.");
    }

    private static double StandardDeviation(double[] values)
    {
        var average = values.Average();
        return Math.Sqrt(values.Average(value => Math.Pow(value - average, 2)));
    }
}

public static class StickMapper
{
    public static StickPoint Apply(StickPoint point, StickCompensation config)
    {
        var x = point.X - config.CenterOffsetX;
        var y = point.Y - config.CenterOffsetY;
        var magnitude = Math.Sqrt(x * x + y * y);
        if (magnitude <= config.Deadzone) return new StickPoint(0, 0);
        var normalized = Math.Clamp((magnitude - config.Deadzone) / (1 - config.Deadzone), 0, 1);
        normalized = config.AntiDeadzone + normalized * (1 - config.AntiDeadzone);
        normalized = config.Curve switch
        {
            ResponseCurve.Precision => normalized * normalized,
            ResponseCurve.Aggressive => Math.Sqrt(normalized),
            _ => normalized
        };
        var scale = normalized / magnitude;
        return new StickPoint(Math.Clamp(x * scale, -1, 1), Math.Clamp(y * scale, -1, 1));
    }
}

public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Serialize(ControllerProfile profile) => JsonSerializer.Serialize(profile, Options);
    public static ControllerProfile Deserialize(string json) => JsonSerializer.Deserialize<ControllerProfile>(json, Options)
        ?? throw new InvalidDataException("Profile file is empty or invalid.");
}

public static class DiagnosticsExporter
{
    public static string CreateReport(ControllerInfo info, CalibrationReport? calibration) => JsonSerializer.Serialize(new
    {
        generatedAtUtc = DateTimeOffset.UtcNow,
        controller = new
        {
            info.DisplayName,
            deviceId = Redact(info.DeviceId),
            connection = info.Connection.ToString(),
            info.BatteryPercent,
            info.Firmware,
            info.SupportsOutput,
            info.OutputNote
        },
        calibration,
        note = "Device identifiers are redacted. No raw input history is included."
    }, new JsonSerializerOptions { WriteIndented = true });

    public static string Redact(string identifier) =>
        identifier.Length <= 6 ? "***" : $"{identifier[..3]}***{identifier[^3..]}";
}
