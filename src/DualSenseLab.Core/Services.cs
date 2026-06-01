using System.Text.Json;
using HidSharp;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

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
    void SetInputState(InputState state);
    void ApplyTriggers(TriggerConfiguration left, TriggerConfiguration right);
    void ResetEffects();
    void TestVibration(int strength);
    void SetLightBar(string hexColor);
    void SetPlayerLeds(byte leds);
    void SetMicrophoneLed(bool enabled);
    void SetMicrophoneMuted(bool muted);
}

public sealed class SimulatedControllerDevice : IControllerDevice
{
    private readonly System.Timers.Timer _timer = new(40);
    private InputState _state = new VirtualControllerConfiguration().ToInputState();
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
    public void SetInputState(InputState state) => _state = state;

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
    public void SetPlayerLeds(byte leds) { }
    public void SetMicrophoneLed(bool enabled) { }
    public void SetMicrophoneMuted(bool muted) { }

    private void PublishFrame()
    {
        InputReceived?.Invoke(this, _state with { Timestamp = DateTimeOffset.UtcNow });
    }

    private static TriggerConfiguration Clone(TriggerConfiguration source) => new()
    {
        Preset = source.Preset, StartPosition = source.StartPosition,
        EndPosition = source.EndPosition, Strength = source.Strength
    };
}

public static class ControllerDeviceFactory
{
    public static IControllerDevice CreatePreferred()
    {
        var devices = DeviceList.Local.GetHidDevices(0x054C, 0x0CE6).ToArray();
        var device = devices.FirstOrDefault(candidate => candidate.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase))
                     ?? devices.FirstOrDefault();
        if (device is null) return new SimulatedControllerDevice();
        try
        {
            return new DualSenseUsbControllerDevice(device);
        }
        catch
        {
            return new SimulatedControllerDevice();
        }
    }
}

public sealed class DualSenseUsbControllerDevice : IControllerDevice
{
    private readonly HidDevice _device;
    private HidStream _stream;
    private readonly byte[] _buffer;
    private readonly DualSenseOutputState _output = new();
    private readonly object _outputLock = new();
    private CancellationTokenSource? _cancellation;
    private Task? _reader;
    private byte _outputSequence;
    private bool _disposed;

    public DualSenseUsbControllerDevice(HidDevice device)
    {
        _device = device;
        if (!device.TryOpen(out _stream)) throw new IOException("The DualSense HID interface could not be opened.");
        _stream.ReadTimeout = 250;
        _buffer = new byte[device.GetMaxInputReportLength()];
        var connection = device.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase)
            ? ConnectionType.Usb : ConnectionType.Bluetooth;
        Info = new(
            "DualSense Wireless Controller", device.DevicePath, connection, null,
            ReadFirmware(_stream), true, $"{connection} HID input and physical effects are enabled.");
    }

    public ControllerInfo Info { get; }
    public long FramesRead { get; private set; }
    public event EventHandler<InputState>? InputReceived;

    public void Start()
    {
        if (_reader is not null) return;
        _cancellation = new();
        _reader = Task.Run(() => ReadLoop(_cancellation.Token));
    }

    public void Stop()
    {
        _cancellation?.Cancel();
        try { _reader?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }
        _reader = null;
        _cancellation?.Dispose();
        _cancellation = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        _stream.Dispose();
    }

    public void SetInputState(InputState state) { }
    public void ApplyTriggers(TriggerConfiguration left, TriggerConfiguration right)
    {
        TriggerValidator.EnsureValid(left);
        TriggerValidator.EnsureValid(right);
        lock (_outputLock)
        {
            _output.LeftTrigger = DualSenseTriggerEffect.Build(left);
            _output.RightTrigger = DualSenseTriggerEffect.Build(right);
            WriteOutput();
        }
    }

    public void ResetEffects()
    {
        lock (_outputLock)
        {
            _output.LeftTrigger = DualSenseTriggerEffect.Off();
            _output.RightTrigger = DualSenseTriggerEffect.Off();
            _output.RightMotor = 0;
            _output.LeftMotor = 0;
            WriteOutput();
        }
    }

    public void TestVibration(int strength)
    {
        if (strength is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(strength));
        lock (_outputLock)
        {
            _output.RightMotor = (byte)Math.Round(strength / 100d * 255);
            _output.LeftMotor = _output.RightMotor;
            WriteOutput();
        }
        _ = Task.Run(async () =>
        {
            await Task.Delay(450);
            lock (_outputLock)
            {
                if (_disposed) return;
                _output.RightMotor = 0;
                _output.LeftMotor = 0;
                WriteOutput();
            }
        });
    }

    public void SetLightBar(string hexColor)
    {
        var color = DualSenseOutputReport.ParseColor(hexColor);
        lock (_outputLock)
        {
            (_output.LightbarRed, _output.LightbarGreen, _output.LightbarBlue) = color;
            WriteOutput();
        }
    }

    public void SetPlayerLeds(byte leds)
    {
        lock (_outputLock) { _output.PlayerLeds = (byte)(leds & 0x1F); WriteOutput(); }
    }

    public void SetMicrophoneLed(bool enabled)
    {
        lock (_outputLock) { _output.MicrophoneLed = enabled; WriteOutput(); }
    }

    public void SetMicrophoneMuted(bool muted)
    {
        lock (_outputLock) { _output.MicrophoneMuted = muted; WriteOutput(); }
    }

    private void WriteOutput()
    {
        var report = Info.Connection == ConnectionType.Bluetooth
            ? DualSenseOutputReport.BuildBluetooth(_output, _outputSequence++)
            : DualSenseOutputReport.BuildUsb(_output);
        try
        {
            _stream.Write(report);
        }
        catch (IOException)
        {
            if (!TryReconnect(CancellationToken.None)) throw;
            _stream.Write(report);
        }
    }

    private void ReadLoop(CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            try
            {
                var length = _stream.Read(_buffer, 0, _buffer.Length);
                if (length > 0)
                {
                    FramesRead++;
                    InputReceived?.Invoke(this, Info.Connection == ConnectionType.Bluetooth
                        ? DualSenseBluetoothReportParser.Parse(_buffer.AsSpan(0, length))
                        : DualSenseUsbReportParser.Parse(_buffer.AsSpan(0, length)));
                }
            }
            catch (TimeoutException) { }
            catch (InvalidDataException) { }
            catch (IOException) when (cancellation.IsCancellationRequested) { }
            catch (IOException)
            {
                if (!TryReconnect(cancellation)) return;
            }
        }
    }

    private bool TryReconnect(CancellationToken cancellation)
    {
        try { _stream.Dispose(); }
        catch { }
        while (!cancellation.IsCancellationRequested)
        {
            if (_device.TryOpen(out var stream))
            {
                stream.ReadTimeout = 250;
                _stream = stream;
                return true;
            }
            if (cancellation == CancellationToken.None) return false;
            Thread.Sleep(500);
        }
        return false;
    }

    private static string ReadFirmware(HidStream stream)
    {
        try
        {
            var report = new byte[64];
            report[0] = 0x20;
            stream.GetFeature(report);
            return $"Firmware {report[24]}.{report[25]}.{report[26]}";
        }
        catch
        {
            return "Firmware unavailable";
        }
    }
}

public sealed class DualSenseOutputState
{
    public byte RightMotor { get; set; }
    public byte LeftMotor { get; set; }
    public byte[] RightTrigger { get; set; } = DualSenseTriggerEffect.Off();
    public byte[] LeftTrigger { get; set; } = DualSenseTriggerEffect.Off();
    public bool MicrophoneLed { get; set; }
    public bool MicrophoneMuted { get; set; }
    public byte PlayerLeds { get; set; } = 0x04;
    public byte LightbarRed { get; set; } = 0x6C;
    public byte LightbarGreen { get; set; } = 0x63;
    public byte LightbarBlue { get; set; } = 0xFF;
}

public static class DualSenseOutputReport
{
    public static byte[] BuildUsb(DualSenseOutputState state)
    {
        var report = new byte[48];
        report[0] = 0x02;
        report[1] = 0x0F; // compatible vibration and both adaptive triggers
        report[2] = 0x17; // microphone LED/mute, light bar, and player LEDs
        report[3] = state.RightMotor;
        report[4] = state.LeftMotor;
        report[9] = state.MicrophoneLed ? (byte)0x01 : (byte)0x00;
        report[10] = state.MicrophoneMuted ? (byte)0x10 : (byte)0x00;
        state.RightTrigger.CopyTo(report, 11);
        state.LeftTrigger.CopyTo(report, 22);
        report[44] = state.PlayerLeds;
        report[45] = state.LightbarRed;
        report[46] = state.LightbarGreen;
        report[47] = state.LightbarBlue;
        return report;
    }

    public static byte[] BuildBluetooth(DualSenseOutputState state, byte sequence)
    {
        var usb = BuildUsb(state);
        var report = new byte[78];
        report[0] = 0x31;
        report[1] = (byte)((sequence & 0x0F) << 4);
        report[2] = 0x10;
        usb.AsSpan(1, 47).CopyTo(report.AsSpan(3));
        var crc = ComputeBluetoothCrc(report.AsSpan(0, 74));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(report.AsSpan(74), crc);
        return report;
    }

    public static uint ComputeBluetoothCrc(ReadOnlySpan<byte> reportWithoutCrc)
    {
        var crc = UpdateCrc(uint.MaxValue, 0xA2);
        foreach (var value in reportWithoutCrc) crc = UpdateCrc(crc, value);
        return ~crc;
    }

    public static (byte Red, byte Green, byte Blue) ParseColor(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor) || hexColor.Length != 7 || hexColor[0] != '#' ||
            !byte.TryParse(hexColor.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red) ||
            !byte.TryParse(hexColor.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var green) ||
            !byte.TryParse(hexColor.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var blue))
            throw new ArgumentException("Use a six-digit hex color such as #6C63FF.", nameof(hexColor));
        return (red, green, blue);
    }

    private static uint UpdateCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        return crc;
    }
}

public static class DualSenseTriggerEffect
{
    public static byte[] Build(TriggerConfiguration configuration)
    {
        if (configuration.Preset == TriggerPreset.Off || configuration.Strength == 0) return Off();
        var start = (byte)Math.Clamp((int)Math.Round(configuration.StartPosition / 100d * 9), 0, 9);
        var end = (byte)Math.Clamp((int)Math.Round(configuration.EndPosition / 100d * 9), 0, 9);
        var strength = (byte)Math.Clamp((int)Math.Round(configuration.Strength / 100d * 8), 1, 8);
        return configuration.Preset switch
        {
            TriggerPreset.HardStop or TriggerPreset.WeaponBreakpoint => Weapon(
                (byte)Math.Clamp((int)start, 2, 7), (byte)Math.Clamp(Math.Max((int)end, start + 1), 3, 8), strength),
            TriggerPreset.Pulse => Vibration(start, strength, 35),
            _ => Feedback(start, strength)
        };
    }

    public static byte[] Off() => new byte[11] { 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] Feedback(byte position, byte strength)
    {
        if (position > 9 || strength is < 1 or > 8) throw new ArgumentOutOfRangeException();
        uint forceZones = 0;
        ushort activeZones = 0;
        for (var zone = position; zone < 10; zone++)
        {
            forceZones |= (uint)((strength - 1) << (3 * zone));
            activeZones |= (ushort)(1 << zone);
        }
        return new[] { (byte)0x21, (byte)activeZones, (byte)(activeZones >> 8), (byte)forceZones,
            (byte)(forceZones >> 8), (byte)(forceZones >> 16), (byte)(forceZones >> 24), (byte)0, (byte)0, (byte)0, (byte)0 };
    }

    public static byte[] Weapon(byte start, byte end, byte strength)
    {
        if (start is < 2 or > 7 || end <= start || end > 8 || strength is < 1 or > 8)
            throw new ArgumentOutOfRangeException();
        var zones = (ushort)((1 << start) | (1 << end));
        return new[] { (byte)0x25, (byte)zones, (byte)(zones >> 8), (byte)(strength - 1), (byte)0, (byte)0,
            (byte)0, (byte)0, (byte)0, (byte)0, (byte)0 };
    }

    public static byte[] Vibration(byte position, byte amplitude, byte frequency)
    {
        if (position > 9 || amplitude is < 1 or > 8 || frequency == 0) throw new ArgumentOutOfRangeException();
        uint amplitudeZones = 0;
        ushort activeZones = 0;
        for (var zone = position; zone < 10; zone++)
        {
            amplitudeZones |= (uint)((amplitude - 1) << (3 * zone));
            activeZones |= (ushort)(1 << zone);
        }
        return new[] { (byte)0x26, (byte)activeZones, (byte)(activeZones >> 8), (byte)amplitudeZones,
            (byte)(amplitudeZones >> 8), (byte)(amplitudeZones >> 16), (byte)(amplitudeZones >> 24),
            (byte)0, (byte)0, frequency, (byte)0 };
    }
}

public static class DualSenseUsbReportParser
{
    public static InputState Parse(byte[] report) => Parse(report.AsSpan());

    public static InputState Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length < 54 || report[0] != 0x01)
            throw new InvalidDataException("Expected a DualSense USB input report with ID 0x01.");
        var buttons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddButtons(buttons, report[8], report[9], report[10]);
        var touchActive = (report[33] & 0x80) == 0;
        var touchpad = ReadTouchpad(report, 33, touchActive);
        var secondaryTouchActive = (report[37] & 0x80) == 0;
        var secondaryTouchpad = ReadTouchpad(report, 37, secondaryTouchActive);
        return new(
            new StickPoint(ToAxis(report[1]), ToAxis(report[2], invert: true)),
            new StickPoint(ToAxis(report[3]), ToAxis(report[4], invert: true)),
            report[5] / 255d, report[6] / 255d, buttons,
            new StickPoint(ReadInt16(report, 16), ReadInt16(report, 18)),
            new StickPoint(ReadInt16(report, 22), ReadInt16(report, 24)),
            touchpad, DateTimeOffset.UtcNow, Math.Min((report[53] & 0x0F) * 10 + 5, 100), touchActive,
            ReadInt16(report, 20), ReadInt16(report, 26), secondaryTouchpad, secondaryTouchActive,
            (report[53] & 0xF0) != 0);
    }

    private static short ReadInt16(ReadOnlySpan<byte> report, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(report.Slice(offset, 2));

    private static StickPoint ReadTouchpad(ReadOnlySpan<byte> report, int offset, bool active) => active
        ? new StickPoint(((report[offset + 2] & 0x0F) << 8 | report[offset + 1]) / 1920d,
            ((report[offset + 3] << 4) | (report[offset + 2] >> 4)) / 1080d)
        : new StickPoint(.5, .5);

    internal static void AddButtons(HashSet<string> buttons, byte buttons0, byte buttons1, byte buttons2)
    {
        AddDpad(buttons, buttons0 & 0x0F);
        Add(buttons, buttons0, 4, "Square"); Add(buttons, buttons0, 5, "Cross");
        Add(buttons, buttons0, 6, "Circle"); Add(buttons, buttons0, 7, "Triangle");
        Add(buttons, buttons1, 0, "L1"); Add(buttons, buttons1, 1, "R1");
        Add(buttons, buttons1, 2, "L2"); Add(buttons, buttons1, 3, "R2");
        Add(buttons, buttons1, 4, "Create"); Add(buttons, buttons1, 5, "Options");
        Add(buttons, buttons1, 6, "L3"); Add(buttons, buttons1, 7, "R3");
        Add(buttons, buttons2, 0, "PS"); Add(buttons, buttons2, 1, "Touchpad");
        Add(buttons, buttons2, 2, "Mute");
    }

    private static double ToAxis(byte value, bool invert = false)
    {
        var normalized = Math.Clamp((value - 128) / 127d, -1, 1);
        return invert ? -normalized : normalized;
    }

    private static void Add(HashSet<string> buttons, byte value, int bit, string name)
    {
        if ((value & (1 << bit)) != 0) buttons.Add(name);
    }

    private static void AddDpad(HashSet<string> buttons, int hat)
    {
        if (hat is 0 or 1 or 7) buttons.Add("Up");
        if (hat is 1 or 2 or 3) buttons.Add("Right");
        if (hat is 3 or 4 or 5) buttons.Add("Down");
        if (hat is 5 or 6 or 7) buttons.Add("Left");
    }
}

public static class DualSenseBluetoothReportParser
{
    public static InputState Parse(byte[] report) => Parse(report.AsSpan());

    public static InputState Parse(ReadOnlySpan<byte> report)
    {
        if (report.Length >= 78 && report[0] == 0x31)
        {
            var usbLayout = new byte[64];
            usbLayout[0] = 0x01;
            report.Slice(2, 63).CopyTo(usbLayout.AsSpan(1));
            return DualSenseUsbReportParser.Parse(usbLayout);
        }
        if (report.Length < 10 || report[0] != 0x01)
            throw new InvalidDataException("Expected a DualSense Bluetooth input report.");
        var buttons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DualSenseUsbReportParser.AddButtons(buttons, report[5], report[6], report[7]);
        return new(
            new StickPoint(ToAxis(report[1]), ToAxis(report[2], invert: true)),
            new StickPoint(ToAxis(report[3]), ToAxis(report[4], invert: true)),
            report[8] / 255d, report[9] / 255d, buttons,
            new StickPoint(0, 0), new StickPoint(0, 0), new StickPoint(.5, .5), DateTimeOffset.UtcNow);
    }

    private static double ToAxis(byte value, bool invert = false)
    {
        var normalized = Math.Clamp((value - 128) / 127d, -1, 1);
        return invert ? -normalized : normalized;
    }
}

public interface IVirtualControllerOutput : IDisposable
{
    bool IsConnected { get; }
    string Status { get; }
    void Connect();
    void Disconnect();
    void Submit(VirtualControllerConfiguration configuration);
}

public sealed class ViGEmXbox360Output : IVirtualControllerOutput
{
    private ViGEmClient? _client;
    private IXbox360Controller? _controller;

    public bool IsConnected => _controller is not null;
    public string Status { get; private set; } = "Virtual output is disabled.";

    public void Connect()
    {
        if (IsConnected) return;
        try
        {
            _client = new ViGEmClient();
            _controller = _client.CreateXbox360Controller();
            _controller.AutoSubmitReport = false;
            _controller.Connect();
            Status = "ViGEm virtual Xbox 360 controller connected.";
            Submit(new VirtualControllerConfiguration());
        }
        catch (Exception exception)
        {
            DisposeController();
            Status = $"ViGEmBus is unavailable: {exception.Message}";
            throw new InvalidOperationException(Status, exception);
        }
    }

    public void Disconnect()
    {
        DisposeController();
        Status = "Virtual output is disabled.";
    }

    public void Submit(VirtualControllerConfiguration configuration)
    {
        if (_controller is null) return;
        _controller.SetAxisValue(Xbox360Axis.LeftThumbX, ToAxis(configuration.LeftStickX));
        _controller.SetAxisValue(Xbox360Axis.LeftThumbY, ToAxis(configuration.LeftStickY));
        _controller.SetAxisValue(Xbox360Axis.RightThumbX, ToAxis(configuration.RightStickX));
        _controller.SetAxisValue(Xbox360Axis.RightThumbY, ToAxis(configuration.RightStickY));
        _controller.SetSliderValue(Xbox360Slider.LeftTrigger, ToSlider(configuration.LeftTrigger));
        _controller.SetSliderValue(Xbox360Slider.RightTrigger, ToSlider(configuration.RightTrigger));
        foreach (var (name, button) in ButtonMap)
            _controller.SetButtonState(button, configuration.Buttons.Contains(name));
        _controller.SubmitReport();
    }

    public void Dispose() => Disconnect();

    public static short ToAxis(double value) =>
        (short)Math.Round(Math.Clamp(value, -1, 1) * (value < 0 ? 32768 : 32767));

    public static byte ToSlider(double value) =>
        (byte)Math.Round(Math.Clamp(value, 0, 1) * byte.MaxValue);

    private void DisposeController()
    {
        _controller?.Disconnect();
        _controller = null;
        _client?.Dispose();
        _client = null;
    }

    private static readonly IReadOnlyDictionary<string, Xbox360Button> ButtonMap =
        new Dictionary<string, Xbox360Button>(StringComparer.OrdinalIgnoreCase)
        {
            ["A"] = Xbox360Button.A,
            ["B"] = Xbox360Button.B,
            ["X"] = Xbox360Button.X,
            ["Y"] = Xbox360Button.Y,
            ["LB"] = Xbox360Button.LeftShoulder,
            ["RB"] = Xbox360Button.RightShoulder,
            ["Back"] = Xbox360Button.Back,
            ["Start"] = Xbox360Button.Start,
            ["L3"] = Xbox360Button.LeftThumb,
            ["R3"] = Xbox360Button.RightThumb,
            ["Up"] = Xbox360Button.Up,
            ["Down"] = Xbox360Button.Down,
            ["Left"] = Xbox360Button.Left,
            ["Right"] = Xbox360Button.Right
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

public static class VirtualControllerMapper
{
    public static VirtualControllerConfiguration Apply(
        VirtualControllerConfiguration input,
        StickCompensation left,
        StickCompensation right,
        VirtualControllerConfiguration? previous = null)
    {
        var leftPoint = Smooth(StickMapper.Apply(new StickPoint(input.LeftStickX, input.LeftStickY), left),
            previous is null ? null : new StickPoint(previous.LeftStickX, previous.LeftStickY), left.Smoothing);
        var rightPoint = Smooth(StickMapper.Apply(new StickPoint(input.RightStickX, input.RightStickY), right),
            previous is null ? null : new StickPoint(previous.RightStickX, previous.RightStickY), right.Smoothing);
        return new()
        {
            LeftStickX = leftPoint.X,
            LeftStickY = leftPoint.Y,
            RightStickX = rightPoint.X,
            RightStickY = rightPoint.Y,
            LeftTrigger = Math.Clamp(input.LeftTrigger, 0, 1),
            RightTrigger = Math.Clamp(input.RightTrigger, 0, 1),
            Buttons = new HashSet<string>(input.Buttons, StringComparer.OrdinalIgnoreCase)
        };
    }

    private static StickPoint Smooth(StickPoint current, StickPoint? previous, double smoothing)
    {
        if (previous is null) return current;
        var retained = Math.Clamp(smoothing, 0, 1);
        return new(
            current.X * (1 - retained) + previous.X * retained,
            current.Y * (1 - retained) + previous.Y * retained);
    }
}

public static class ProfileSerializer
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Serialize(ControllerProfile profile) => JsonSerializer.Serialize(profile, Options);
    public static ControllerProfile Deserialize(string json) => Normalize(JsonSerializer.Deserialize<ControllerProfile>(json, Options)
        ?? throw new InvalidDataException("Profile file is empty or invalid."));

    public static string SerializeLibrary(IEnumerable<ControllerProfile> profiles) => JsonSerializer.Serialize(profiles, Options);

    public static List<ControllerProfile> DeserializeLibrary(string json) =>
        (JsonSerializer.Deserialize<List<ControllerProfile>>(json, Options)
         ?? throw new InvalidDataException("Profile library is empty or invalid."))
        .Select(Normalize).ToList();

    private static ControllerProfile Normalize(ControllerProfile profile)
    {
        profile.Name = string.IsNullOrWhiteSpace(profile.Name) ? "Untitled Profile" : profile.Name.Trim();
        profile.LeftTrigger ??= new();
        profile.RightTrigger ??= new();
        profile.LeftStick ??= new();
        profile.RightStick ??= new();
        profile.VirtualController ??= new();
        profile.VirtualController.Buttons = new(profile.VirtualController.Buttons ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        return profile;
    }
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
