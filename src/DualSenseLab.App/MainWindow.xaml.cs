using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using DualSenseLab.Core;
using Microsoft.Win32;

namespace DualSenseLab.App;

public partial class MainWindow : Window
{
    private readonly IControllerDevice _controller = new SimulatedControllerDevice();
    private readonly ObservableCollection<ControllerProfile> _profiles = new() { ControllerProfile.SafeDefault() };
    private readonly List<StickPoint> _calibrationSamples = new();
    private DateTimeOffset? _calibrationUntil;
    private CalibrationReport? _lastReport;
    private InputState? _lastInput;
    private readonly string[] _pageNames = { "Dashboard", "Adaptive Triggers", "Stick Calibration", "Input Tester", "Profiles", "Settings" };

    public MainWindow()
    {
        InitializeComponent();
        LeftPreset.ItemsSource = Enum.GetValues<TriggerPreset>();
        RightPreset.ItemsSource = Enum.GetValues<TriggerPreset>();
        LeftPreset.SelectedItem = TriggerPreset.SoftResistance;
        RightPreset.SelectedItem = TriggerPreset.Off;
        ProfileList.ItemsSource = _profiles;
        ProfileList.DisplayMemberPath = "Name";
        ProfileList.SelectedIndex = 0;
        _controller.InputReceived += Controller_InputReceived;
        Loaded += (_, _) => _controller.Start();
        Closed += (_, _) => _controller.Dispose();
    }

    private void Controller_InputReceived(object? sender, InputState state) => Dispatcher.Invoke(() =>
    {
        _lastInput = state;
        DashboardLeftStick.Text = FormatStick(state.LeftStick);
        DashboardRightStick.Text = FormatStick(state.RightStick);
        DashboardButtons.Text = state.Buttons.Count == 0 ? "None" : string.Join(", ", state.Buttons);
        ButtonTesterText.Text = DashboardButtons.Text;
        MotionText.Text = $"Gyroscope   X {state.Gyroscope.X,6:F2}   Y {state.Gyroscope.Y,6:F2}\nAccelerometer   X {state.Accelerometer.X,5:F2}   Y {state.Accelerometer.Y,5:F2}\nTriggers   L2 {state.LeftTrigger:P0}   R2 {state.RightTrigger:P0}";
        TouchX.Value = state.Touchpad.X; TouchY.Value = state.Touchpad.Y;
        PositionDot(LeftDot, state.LeftStick); PositionDot(RightDot, state.RightStick);
        if (_calibrationUntil is not null)
        {
            _calibrationSamples.Add(state.LeftStick);
            if (DateTimeOffset.UtcNow >= _calibrationUntil) FinishCalibration();
            else CalibrationStatus.Text = $"Sampling center position... {_calibrationSamples.Count} frames captured";
        }
    });

    private static string FormatStick(StickPoint point) => $"X {point.X,6:F3}    Y {point.Y,6:F3}";
    private static void PositionDot(Ellipse dot, StickPoint point)
    {
        Canvas.SetLeft(dot, 118 + point.X * 118);
        Canvas.SetTop(dot, 118 + point.Y * 118);
    }

    private void Navigate_Click(object sender, RoutedEventArgs e) => Pages.SelectedIndex = int.Parse((string)((Button)sender).Tag);
    private void Pages_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PageTitle is not null && Pages.SelectedIndex >= 0) PageTitle.Text = _pageNames[Pages.SelectedIndex];
    }

    private TriggerConfiguration ReadTrigger(ComboBox preset, Slider start, Slider end, Slider strength) => new()
    {
        Preset = (TriggerPreset)(preset.SelectedItem ?? TriggerPreset.Off),
        StartPosition = (int)start.Value, EndPosition = (int)end.Value, Strength = (int)strength.Value
    };

    private void ApplyTriggers_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.ApplyTriggers(ReadTrigger(LeftPreset, LeftStart, LeftEnd, LeftStrength), ReadTrigger(RightPreset, RightStart, RightEnd, RightStrength));
            TriggerStatus.Text = "Effects applied safely in simulator.";
        }
        catch (Exception exception) { TriggerStatus.Text = exception.Message; }
    }

    private void ResetEffects_Click(object sender, RoutedEventArgs e)
    {
        _controller.ResetEffects();
        TriggerStatus.Text = "Both trigger effects reset.";
    }

    private void Slider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LeftStartLabel is null || LeftEndLabel is null || LeftStrengthLabel is null ||
            RightStartLabel is null || RightEndLabel is null || RightStrengthLabel is null ||
            LeftStart is null || LeftEnd is null || LeftStrength is null ||
            RightStart is null || RightEnd is null || RightStrength is null) return;
        LeftStartLabel.Text = $"{LeftStart.Value:F0}%"; LeftEndLabel.Text = $"{LeftEnd.Value:F0}%"; LeftStrengthLabel.Text = $"{LeftStrength.Value:F0}%";
        RightStartLabel.Text = $"{RightStart.Value:F0}%"; RightEndLabel.Text = $"{RightEnd.Value:F0}%"; RightStrengthLabel.Text = $"{RightStrength.Value:F0}%";
    }

    private void RunCalibration_Click(object sender, RoutedEventArgs e)
    {
        _calibrationSamples.Clear();
        _calibrationUntil = DateTimeOffset.UtcNow.AddSeconds(4);
        RunCalibrationButton.IsEnabled = false;
        CalibrationStatus.Text = "Sampling center position...";
    }

    private void FinishCalibration()
    {
        _calibrationUntil = null;
        RunCalibrationButton.IsEnabled = true;
        _lastReport = CalibrationAnalyzer.Analyze(_calibrationSamples);
        CalibrationMetrics.Text = $"Samples: {_lastReport.SampleCount}\nCenter offset: {_lastReport.CenterOffset:P1}\nJitter: {_lastReport.Jitter:P1}\nCircularity: {_lastReport.Circularity:P0}\nDrift score: {_lastReport.Drift:P1}\nConfidence: {_lastReport.Confidence}%";
        CalibrationStatus.Text = _lastReport.Summary;
    }

    private void Mitigation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeadzoneLabel is null || AntiDeadzoneLabel is null || SmoothingLabel is null ||
            DeadzoneSlider is null || AntiDeadzoneSlider is null || SmoothingSlider is null) return;
        DeadzoneLabel.Text = $"{DeadzoneSlider.Value:P0}"; AntiDeadzoneLabel.Text = $"{AntiDeadzoneSlider.Value:P0}"; SmoothingLabel.Text = $"{SmoothingSlider.Value:P0}";
    }

    private void ExportDiagnostics_Click(object sender, RoutedEventArgs e)
    {
        var path = SelectSavePath("GCE diagnostics|*.json", "gce-diagnostics.json");
        if (path is not null) File.WriteAllText(path, DiagnosticsExporter.CreateReport(_controller.Info, _lastReport));
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        ProfileName.Text = profile.Name; AssignedExecutable.Text = profile.AssignedExecutable; AutoApply.IsChecked = profile.AutoApply;
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Add(new ControllerProfile { Name = $"Profile {_profiles.Count + 1}" });
        ProfileList.SelectedIndex = _profiles.Count - 1;
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        var copy = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));
        copy.Name += " Copy"; _profiles.Add(copy); ProfileList.SelectedItem = copy;
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is ControllerProfile profile && _profiles.Count > 1) _profiles.Remove(profile);
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        profile.Name = string.IsNullOrWhiteSpace(ProfileName.Text) ? "Untitled Profile" : ProfileName.Text.Trim();
        profile.AssignedExecutable = AssignedExecutable.Text.Trim(); profile.AutoApply = AutoApply.IsChecked == true;
        profile.LeftStick.Deadzone = DeadzoneSlider.Value; profile.LeftStick.AntiDeadzone = AntiDeadzoneSlider.Value; profile.LeftStick.Smoothing = SmoothingSlider.Value;
        ProfileList.Items.Refresh();
    }

    private void ExportProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        var path = SelectSavePath("GCE profile|*.json", $"{profile.Name}.json");
        if (path is not null) File.WriteAllText(path, ProfileSerializer.Serialize(profile));
    }

    private void ImportProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "GCE profile|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { var profile = ProfileSerializer.Deserialize(File.ReadAllText(dialog.FileName)); _profiles.Add(profile); ProfileList.SelectedItem = profile; }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Profile import failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string? SelectSavePath(string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ApplyLightBar_Click(object sender, RoutedEventArgs e)
    {
        try { _controller.SetLightBar(LightBarColor.Text); MessageBox.Show("Light-bar color applied in simulator.", "Great Controller Emulator"); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Invalid color", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TestVibration_Click(object sender, RoutedEventArgs e)
    {
        _controller.TestVibration((int)VibrationSlider.Value);
        MessageBox.Show("Conservative vibration test sent to simulator.", "Great Controller Emulator");
    }
}
