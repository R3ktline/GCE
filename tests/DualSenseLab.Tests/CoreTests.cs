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
        var result = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));
        Assert.Equal("Racing", result.Name);
        Assert.Equal(.14, result.LeftStick.Deadzone);
    }

    [Fact]
    public void Trigger_validator_rejects_reversed_range() =>
        Assert.Throws<ArgumentException>(() => TriggerValidator.EnsureValid(new TriggerConfiguration { StartPosition = 80, EndPosition = 20 }));

    [Fact]
    public void Mapper_applies_deadzone() =>
        Assert.Equal(new StickPoint(0, 0), StickMapper.Apply(new StickPoint(.02, -.03), new StickCompensation { Deadzone = .08 }));

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
    public void Logger_redacts_device_identifier()
    {
        var writer = new StringWriter();
        new JsonLabLogger(writer).Info("connected", "Controller connected", "SIM-054C-SECRET-DEMO");
        Assert.DoesNotContain("SECRET", writer.ToString());
        Assert.Contains("SIM***EMO", writer.ToString());
    }
}
