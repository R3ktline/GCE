using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using System.Windows.Threading;
using DualSenseLab.Core;
using Microsoft.Win32;

namespace DualSenseLab.App;

public partial class MainWindow : Window
{
    private readonly IControllerDevice _controller = ControllerDeviceFactory.CreatePreferred();
    private readonly IVirtualControllerOutput _virtualOutput = new ViGEmXbox360Output();
    private readonly ObservableCollection<ControllerProfile> _profiles = new() { ControllerProfile.SafeDefault() };
    private readonly List<StickPoint> _calibrationSamples = new();
    private DateTimeOffset? _calibrationUntil;
    private CalibrationReport? _lastReport;
    private InputState? _lastInput;
    private VirtualControllerConfiguration? _lastMappedConfiguration;
    private readonly DispatcherTimer _profileMonitor = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _uiReady;
    private static readonly string ProfileLibraryPath = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "GreatControllerEmulator", "profiles.json");
    private readonly string[] _pageNames = { "Dashboard", "Adaptive Triggers", "Stick Calibration", "Input Tester", "Profiles", "Settings" };

    public MainWindow()
    {
        InitializeComponent();
        _uiReady = true;
        LeftPreset.ItemsSource = Enum.GetValues<TriggerPreset>();
        RightPreset.ItemsSource = Enum.GetValues<TriggerPreset>();
        LeftPreset.SelectedItem = TriggerPreset.SoftResistance;
        RightPreset.SelectedItem = TriggerPreset.Off;
        LoadStoredProfiles();
        ProfileList.ItemsSource = _profiles;
        ProfileList.DisplayMemberPath = "Name";
        ProfileList.SelectedIndex = 0;
        LoadControllerInfo();
        _controller.InputReceived += Controller_InputReceived;
        _profileMonitor.Tick += (_, _) => ApplyMatchingProfile();
        Loaded += (_, _) => { _controller.Start(); _profileMonitor.Start(); };
        Closed += (_, _) =>
        {
            _profileMonitor.Stop();
            SaveStoredProfiles();
            try { _controller.ResetEffects(); } catch { }
            _virtualOutput.Dispose();
            _controller.Dispose();
        };
    }

    private void Controller_InputReceived(object? sender, InputState state) => Dispatcher.Invoke(() =>
    {
        _lastInput = state;
        DashboardLeftStick.Text = FormatStick(state.LeftStick);
        DashboardRightStick.Text = FormatStick(state.RightStick);
        DashboardButtons.Text = state.Buttons.Count == 0 ? "None" : string.Join(", ", state.Buttons);
        ButtonTesterText.Text = DashboardButtons.Text;
        MotionText.Text = $"Gyroscope   X {state.Gyroscope.X,6:F2}   Y {state.Gyroscope.Y,6:F2}   Z {state.GyroscopeZ,6:F2}\nAccelerometer   X {state.Accelerometer.X,5:F2}   Y {state.Accelerometer.Y,5:F2}   Z {state.AccelerometerZ,5:F2}\nTriggers   L2 {state.LeftTrigger:P0}   R2 {state.RightTrigger:P0}\nHeadset {(state.HeadsetConnected ? "connected" : "disconnected")}   Microphone {(state.MicrophoneConnected ? "connected" : "disconnected")}   Muted {(state.MicrophoneMuted ? "yes" : "no")}";
        TouchX.Value = state.Touchpad.X; TouchY.Value = state.Touchpad.Y;
        if (state.BatteryPercent is int battery)
        {
            BatteryText.Text = state.BatteryCharging ? $"{battery}% +" : $"{battery}%";
            BatteryProgress.Value = battery;
        }
        PositionDot(LeftDot, state.LeftStick); PositionDot(RightDot, state.RightStick);
        if (_calibrationUntil is not null)
        {
            _calibrationSamples.Add(state.LeftStick);
            if (DateTimeOffset.UtcNow >= _calibrationUntil) FinishCalibration();
            else CalibrationStatus.Text = $"Sampling center position... {_calibrationSamples.Count} frames captured";
        }
        if (_controller.Info.Connection == ConnectionType.Simulator) ApplyVirtualControllerConfiguration();
        else SubmitPhysicalControllerState(state);
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

    private void Trigger_ValueChanged(object sender, SelectionChangedEventArgs e) => ApplyTriggerEditorEffects();

    private void ApplyTriggerEditorEffects()
    {
        if (!_uiReady || LeftPreset is null || RightPreset is null ||
            LeftStart is null || LeftEnd is null || LeftStrength is null ||
            RightStart is null || RightEnd is null || RightStrength is null ||
            TriggerStatus is null) return;
        try
        {
            _controller.ApplyTriggers(ReadTrigger(LeftPreset, LeftStart, LeftEnd, LeftStrength), ReadTrigger(RightPreset, RightStart, RightEnd, RightStrength));
            TriggerStatus.Text = _controller.Info.Connection == ConnectionType.Simulator
                ? "Live effects updated safely in simulator."
                : "Live adaptive-trigger effects updated on the physical controller.";
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
        ApplyTriggerEditorEffects();
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
        if (ProfileList.SelectedItem is ControllerProfile profile)
        {
            profile.LeftStick.CenterOffsetX = _calibrationSamples.Average(point => point.X);
            profile.LeftStick.CenterOffsetY = _calibrationSamples.Average(point => point.Y);
            SaveStoredProfiles();
            ApplyVirtualControllerConfiguration();
        }
        CalibrationMetrics.Text = $"Samples: {_lastReport.SampleCount}\nCenter offset: {_lastReport.CenterOffset:P1}\nJitter: {_lastReport.Jitter:P1}\nCircularity: {_lastReport.Circularity:P0}\nDrift score: {_lastReport.Drift:P1}\nConfidence: {_lastReport.Confidence}%";
        CalibrationStatus.Text = $"{_lastReport.Summary} The measured left-stick center offset is now applied to mapped output.";
    }

    private void Mitigation_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DeadzoneLabel is null || AntiDeadzoneLabel is null || SmoothingLabel is null ||
            DeadzoneSlider is null || AntiDeadzoneSlider is null || SmoothingSlider is null ||
            RightDeadzoneLabel is null || RightAntiDeadzoneLabel is null || RightSmoothingLabel is null ||
            RightDeadzoneSlider is null || RightAntiDeadzoneSlider is null || RightSmoothingSlider is null) return;
        DeadzoneLabel.Text = $"{DeadzoneSlider.Value:P0}"; AntiDeadzoneLabel.Text = $"{AntiDeadzoneSlider.Value:P0}"; SmoothingLabel.Text = $"{SmoothingSlider.Value:P0}";
        RightDeadzoneLabel.Text = $"{RightDeadzoneSlider.Value:P0}"; RightAntiDeadzoneLabel.Text = $"{RightAntiDeadzoneSlider.Value:P0}"; RightSmoothingLabel.Text = $"{RightSmoothingSlider.Value:P0}";
        if (!_uiReady || ProfileList.SelectedItem is not ControllerProfile profile) return;
        profile.LeftStick.Deadzone = DeadzoneSlider.Value; profile.LeftStick.AntiDeadzone = AntiDeadzoneSlider.Value; profile.LeftStick.Smoothing = SmoothingSlider.Value;
        profile.RightStick.Deadzone = RightDeadzoneSlider.Value; profile.RightStick.AntiDeadzone = RightAntiDeadzoneSlider.Value; profile.RightStick.Smoothing = RightSmoothingSlider.Value;
        ApplyVirtualControllerConfiguration();
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
        LeftPreset.SelectedItem = profile.LeftTrigger.Preset; LeftStart.Value = profile.LeftTrigger.StartPosition; LeftEnd.Value = profile.LeftTrigger.EndPosition; LeftStrength.Value = profile.LeftTrigger.Strength;
        RightPreset.SelectedItem = profile.RightTrigger.Preset; RightStart.Value = profile.RightTrigger.StartPosition; RightEnd.Value = profile.RightTrigger.EndPosition; RightStrength.Value = profile.RightTrigger.Strength;
        DeadzoneSlider.Value = profile.LeftStick.Deadzone; AntiDeadzoneSlider.Value = profile.LeftStick.AntiDeadzone; SmoothingSlider.Value = profile.LeftStick.Smoothing;
        RightDeadzoneSlider.Value = profile.RightStick.Deadzone; RightAntiDeadzoneSlider.Value = profile.RightStick.AntiDeadzone; RightSmoothingSlider.Value = profile.RightStick.Smoothing;
        LightBarColor.Text = profile.LightBarColor; VibrationSlider.Value = profile.VibrationStrength;
        _lastMappedConfiguration = null;
        LoadVirtualController(profile.VirtualController);
        ApplyProfileEffects(profile);
    }

    private void NewProfile_Click(object sender, RoutedEventArgs e)
    {
        _profiles.Add(new ControllerProfile { Name = $"Profile {_profiles.Count + 1}" });
        ProfileList.SelectedIndex = _profiles.Count - 1;
        SaveStoredProfiles();
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        var copy = ProfileSerializer.Deserialize(ProfileSerializer.Serialize(profile));
        copy.Name += " Copy"; _profiles.Add(copy); ProfileList.SelectedItem = copy; SaveStoredProfiles();
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is ControllerProfile profile && _profiles.Count > 1)
        {
            _profiles.Remove(profile);
            ProfileList.SelectedIndex = 0;
            SaveStoredProfiles();
        }
    }

    private void SaveProfile_Click(object sender, RoutedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        profile.Name = string.IsNullOrWhiteSpace(ProfileName.Text) ? "Untitled Profile" : ProfileName.Text.Trim();
        profile.AssignedExecutable = AssignedExecutable.Text.Trim(); profile.AutoApply = AutoApply.IsChecked == true;
        profile.LeftTrigger = ReadTrigger(LeftPreset, LeftStart, LeftEnd, LeftStrength); profile.RightTrigger = ReadTrigger(RightPreset, RightStart, RightEnd, RightStrength);
        profile.LeftStick.Deadzone = DeadzoneSlider.Value; profile.LeftStick.AntiDeadzone = AntiDeadzoneSlider.Value; profile.LeftStick.Smoothing = SmoothingSlider.Value;
        profile.RightStick.Deadzone = RightDeadzoneSlider.Value; profile.RightStick.AntiDeadzone = RightAntiDeadzoneSlider.Value; profile.RightStick.Smoothing = RightSmoothingSlider.Value;
        profile.LightBarColor = LightBarColor.Text.Trim(); profile.VibrationStrength = (int)VibrationSlider.Value;
        profile.VirtualController = ReadVirtualController();
        ProfileList.Items.Refresh();
        SaveStoredProfiles();
        ApplyVirtualControllerConfiguration();
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
        try { var profile = ProfileSerializer.Deserialize(File.ReadAllText(dialog.FileName)); _profiles.Add(profile); ProfileList.SelectedItem = profile; SaveStoredProfiles(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Profile import failed", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string? SelectSavePath(string filter, string fileName)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = fileName };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private void ApplyLightBar_Click(object sender, RoutedEventArgs e)
    {
        try { _controller.SetLightBar(LightBarColor.Text); MessageBox.Show("Light-bar color applied.", "Great Controller Emulator"); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Invalid color", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private void TestVibration_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _controller.TestVibration((int)VibrationSlider.Value);
            MessageBox.Show("Brief vibration test sent.", "Great Controller Emulator");
        }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Vibration unavailable", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void VirtualOutput_Changed(object sender, RoutedEventArgs e)
    {
        if (VirtualOutputEnabled is null || VirtualOutputStatus is null) return;
        if (VirtualOutputEnabled.IsChecked == true)
        {
            try { _virtualOutput.Connect(); }
            catch (Exception exception)
            {
                VirtualOutputEnabled.IsChecked = false;
                VirtualOutputStatus.Text = exception.Message;
                return;
            }
        }
        else
        {
            _virtualOutput.Disconnect();
        }

        VirtualOutputStatus.Text = _virtualOutput.Status;
        ApplyVirtualControllerConfiguration();
    }

    private void ApplyPlayerLeds_Click(object sender, RoutedEventArgs e) =>
        _controller.SetPlayerLeds((byte)PlayerLedsSlider.Value);

    private void MicrophoneLed_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _controller.SetMicrophoneLed(MicrophoneLedPulse.IsChecked == true ? MicrophoneLedMode.Pulse :
            MicrophoneLedEnabled.IsChecked == true ? MicrophoneLedMode.On : MicrophoneLedMode.Off);
    }

    private void MicrophoneMute_Changed(object sender, RoutedEventArgs e)
    {
        if (!_uiReady) return;
        _controller.SetMicrophoneMuted(MicrophoneMuted.IsChecked == true);
    }

    private void ApplyHardwareTuning_Click(object sender, RoutedEventArgs e)
    {
        _controller.SetAudio(new ControllerAudioConfiguration
        {
            HeadphoneVolume = (byte)HeadphoneVolumeSlider.Value,
            SpeakerVolume = (byte)SpeakerVolumeSlider.Value,
            MicrophoneVolume = (byte)MicrophoneVolumeSlider.Value,
            RouteToInternalSpeaker = RouteInternalSpeaker.IsChecked == true,
            ForceInternalMicrophone = ForceInternalMicrophone.IsChecked == true,
            ForceHeadsetMicrophone = ForceHeadsetMicrophone.IsChecked == true,
            AudioMuted = AudioMuted.IsChecked == true,
            SpeakerPreampGain = (byte)SpeakerPreampSlider.Value
        });
        _controller.SetLedBrightness((byte)LedBrightnessSlider.Value);
        _controller.SetEffectPowerReduction((byte)EffectPowerReductionSlider.Value);
        _controller.SetLightBarSetup(FadeLightBar.IsChecked == true);
    }

    private void VirtualController_ValueChanged(object sender, RoutedEventArgs e) => ApplyVirtualControllerConfiguration();
    private void VirtualController_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyVirtualControllerConfiguration();

    private void ApplyVirtualControllerConfiguration()
    {
        if (!_uiReady) return;
        var configuration = ReadVirtualController();
        var profile = ProfileList.SelectedItem as ControllerProfile ?? ControllerProfile.SafeDefault();
        var mapped = VirtualControllerMapper.Apply(configuration, profile.LeftStick, profile.RightStick, _lastMappedConfiguration);
        _lastMappedConfiguration = mapped;
        _controller.SetInputState(mapped.ToInputState());
        _virtualOutput.Submit(mapped);
        VirtualValueSummary.Text =
            $"Raw left ({configuration.LeftStickX:F2}, {configuration.LeftStickY:F2})   mapped ({mapped.LeftStickX:F2}, {mapped.LeftStickY:F2})\n" +
            $"Raw right ({configuration.RightStickX:F2}, {configuration.RightStickY:F2})   mapped ({mapped.RightStickX:F2}, {mapped.RightStickY:F2})\n" +
            $"Triggers L {configuration.LeftTrigger:P0}   R {configuration.RightTrigger:P0}   Buttons {(configuration.Buttons.Count == 0 ? "None" : string.Join(", ", configuration.Buttons))}";
    }

    private void SubmitPhysicalControllerState(InputState state)
    {
        if (ProfileList.SelectedItem is not ControllerProfile profile) return;
        var configuration = new VirtualControllerConfiguration
        {
            LeftStickX = state.LeftStick.X, LeftStickY = state.LeftStick.Y,
            RightStickX = state.RightStick.X, RightStickY = state.RightStick.Y,
            LeftTrigger = state.LeftTrigger, RightTrigger = state.RightTrigger,
            Buttons = new(state.Buttons.Select(ToVirtualButton).OfType<string>(), StringComparer.OrdinalIgnoreCase)
        };
        var mapped = VirtualControllerMapper.Apply(configuration, profile.LeftStick, profile.RightStick, _lastMappedConfiguration);
        _lastMappedConfiguration = mapped;
        _virtualOutput.Submit(mapped);
        VirtualValueSummary.Text =
            $"HID left ({state.LeftStick.X:F2}, {state.LeftStick.Y:F2})   mapped ({mapped.LeftStickX:F2}, {mapped.LeftStickY:F2})\n" +
            $"HID right ({state.RightStick.X:F2}, {state.RightStick.Y:F2})   mapped ({mapped.RightStickX:F2}, {mapped.RightStickY:F2})\n" +
            $"Triggers L {state.LeftTrigger:P0}   R {state.RightTrigger:P0}   Buttons {(mapped.Buttons.Count == 0 ? "None" : string.Join(", ", mapped.Buttons))}";
    }

    private static string? ToVirtualButton(string button) => button switch
    {
        "Cross" => "A", "Circle" => "B", "Square" => "X", "Triangle" => "Y",
        "L1" => "LB", "R1" => "RB", "Create" => "Back", "Options" => "Start",
        "L3" => "L3", "R3" => "R3", "Up" => "Up", "Down" => "Down",
        "Left" => "Left", "Right" => "Right", _ => null
    };

    private void LoadControllerInfo()
    {
        var info = _controller.Info;
        ConnectedControllerName.Text = info.DisplayName;
        ConnectedControllerId.Text = DiagnosticsExporter.Redact(info.DeviceId);
        ConnectionModeText.Text = $"{info.Connection} connection";
        BatteryText.Text = info.BatteryPercent is null ? "N/A" : $"{info.BatteryPercent}%";
        BatteryProgress.Value = info.BatteryPercent ?? 0;
        OutputSupportTitle.Text = info.Connection == ConnectionType.Simulator ? "Manual + ViGEm" : $"{info.Connection} HID + ViGEm";
        OutputSupportText.Text = info.OutputNote;
        ConnectionHeaderText.Text = $"{info.DisplayName} connected";
        SideInputTitle.Text = info.Connection == ConnectionType.Simulator ? "MANUAL INPUT ACTIVE" : $"{info.Connection.ToString().ToUpperInvariant()} INPUT ACTIVE";
        SideInputNote.Text = "ViGEm output is opt-in";
    }

    private void LoadVirtualController(VirtualControllerConfiguration configuration)
    {
        VirtualLeftX.Value = configuration.LeftStickX;
        VirtualLeftY.Value = configuration.LeftStickY;
        VirtualRightX.Value = configuration.RightStickX;
        VirtualRightY.Value = configuration.RightStickY;
        VirtualLeftTrigger.Value = configuration.LeftTrigger;
        VirtualRightTrigger.Value = configuration.RightTrigger;
        foreach (var button in new[] { VirtualA, VirtualB, VirtualX, VirtualY, VirtualLB, VirtualRB, VirtualBack, VirtualStart, VirtualL3, VirtualR3, VirtualUp, VirtualDown, VirtualLeft, VirtualRight })
            button.IsChecked = configuration.Buttons.Contains((string)button.Tag);
        ApplyVirtualControllerConfiguration();
    }

    private void ResetVirtualController_Click(object sender, RoutedEventArgs e) =>
        LoadVirtualController(new VirtualControllerConfiguration());

    private VirtualControllerConfiguration ReadVirtualController()
    {
        var buttons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in new[] { VirtualA, VirtualB, VirtualX, VirtualY, VirtualLB, VirtualRB, VirtualBack, VirtualStart, VirtualL3, VirtualR3, VirtualUp, VirtualDown, VirtualLeft, VirtualRight })
            if (button.IsChecked == true) buttons.Add((string)button.Tag);

        return new VirtualControllerConfiguration
        {
            LeftStickX = VirtualLeftX.Value,
            LeftStickY = VirtualLeftY.Value,
            RightStickX = VirtualRightX.Value,
            RightStickY = VirtualRightY.Value,
            LeftTrigger = VirtualLeftTrigger.Value,
            RightTrigger = VirtualRightTrigger.Value,
            Buttons = buttons
        };
    }

    private void ApplyMatchingProfile()
    {
        var profile = _profiles.FirstOrDefault(candidate =>
            candidate.AutoApply && !string.IsNullOrWhiteSpace(candidate.AssignedExecutable) &&
            IsProcessRunning(candidate.AssignedExecutable));
        if (profile is null)
        {
            AutoApplyStatus.Text = "Monitoring enabled profiles every 2 seconds.";
            return;
        }

        AutoApplyStatus.Text = $"Matched {profile.AssignedExecutable}. Applied {profile.Name}.";
        if (!ReferenceEquals(ProfileList.SelectedItem, profile)) ProfileList.SelectedItem = profile;
    }

    private static bool IsProcessRunning(string executable)
    {
        var processName = System.IO.Path.GetFileNameWithoutExtension(executable.Trim());
        if (string.IsNullOrWhiteSpace(processName)) return false;
        try
        {
            var found = false;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                found = true;
                process.Dispose();
            }
            return found;
        }
        catch
        {
            return false;
        }
    }

    private void ApplyProfileEffects(ControllerProfile profile)
    {
        try
        {
            _controller.ApplyTriggers(profile.LeftTrigger, profile.RightTrigger);
            _controller.SetLightBar(profile.LightBarColor);
            TriggerStatus.Text = $"Applied {profile.Name}.";
        }
        catch (Exception exception)
        {
            TriggerStatus.Text = $"Profile {profile.Name} needs attention: {exception.Message}";
        }
    }

    private void LoadStoredProfiles()
    {
        if (!File.Exists(ProfileLibraryPath)) return;
        try
        {
            var stored = ProfileSerializer.DeserializeLibrary(File.ReadAllText(ProfileLibraryPath));
            if (stored.Count == 0) return;
            _profiles.Clear();
            foreach (var profile in stored) _profiles.Add(profile);
        }
        catch
        {
            // Keep the safe default when a local profile library cannot be read.
        }
    }

    private void SaveStoredProfiles()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ProfileLibraryPath)!);
            File.WriteAllText(ProfileLibraryPath, ProfileSerializer.SerializeLibrary(_profiles));
        }
        catch
        {
            // The app remains usable even if the local profile library is temporarily unavailable.
        }
    }
}
