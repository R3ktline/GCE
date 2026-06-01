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
    void SetMicrophoneLed(MicrophoneLedMode mode);
    void SetMicrophoneMuted(bool muted);
    void SetAudio(ControllerAudioConfiguration configuration);
    void SetLedBrightness(byte brightness);
    void SetEffectPowerReduction(byte reduction);
    void SetLightBarSetup(bool fadeOut);
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
        if (strength is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(strength), "Test strength must be between 0 and 100.");
    }

    public void SetLightBar(string hexColor)
    {
        if (string.IsNullOrWhiteSpace(hexColor) || !hexColor.StartsWith("#") || hexColor.Length != 7)
            throw new ArgumentException("Use a six-digit hex color such as #6C63FF.", nameof(hexColor));
    }
    public void SetPlayerLeds(byte leds) { }
    public void SetMicrophoneLed(MicrophoneLedMode mode) { }
    public void SetMicrophoneMuted(bool muted) { }
    public void SetAudio(ControllerAudioConfiguration configuration) { }
    public void SetLedBrightness(byte brightness) { }
    public void SetEffectPowerReduction(byte reduction) { }
    public void SetLightBarSetup(bool fadeOut) { }

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
    private HidDevice _device;
    private HidStream _stream;
    private readonly byte[] _buffer;
    private DualSenseMotionCalibration _calibration;
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
        _buffer = new byte[78];
        _calibration = ReadCalibration(_stream);
        var connection = device.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase)
            ? ConnectionType.Usb : ConnectionType.Bluetooth;
        var identity = ReadIdentity(_stream);
        Info = new(
            "DualSense Wireless Controller", identity ?? device.DevicePath, connection, null,
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

    public void SetMicrophoneLed(MicrophoneLedMode mode)
    {
        lock (_outputLock) { _output.MicrophoneLed = mode; WriteOutput(); }
    }

    public void SetMicrophoneMuted(bool muted)
    {
        lock (_outputLock) { _output.MicrophoneMuted = muted; WriteOutput(); }
    }

    public void SetAudio(ControllerAudioConfiguration configuration)
    {
        lock (_outputLock)
        {
            _output.Audio = configuration;
            WriteOutput();
        }
    }

    public void SetLedBrightness(byte brightness)
    {
        lock (_outputLock) { _output.LedBrightness = (byte)Math.Clamp(brightness, (byte)0, (byte)2); WriteOutput(); }
    }

    public void SetEffectPowerReduction(byte reduction)
    {
        lock (_outputLock) { _output.EffectPowerReduction = (byte)Math.Clamp(reduction, (byte)0, (byte)7); WriteOutput(); }
    }

    public void SetLightBarSetup(bool fadeOut)
    {
        lock (_outputLock) { _output.LightbarSetup = fadeOut ? (byte)0x02 : (byte)0x00; WriteOutput(); }
    }

    private void WriteOutput()
    {
        var report = IsBluetooth(_device)
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
                    InputReceived?.Invoke(this, IsBluetooth(_device)
                        ? DualSenseBluetoothReportParser.Parse(_buffer.AsSpan(0, length), _calibration)
                        : DualSenseUsbReportParser.Parse(_buffer.AsSpan(0, length), _calibration));
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
            var device = DeviceList.Local.GetHidDevices(0x054C, 0x0CE6)
                .OrderBy(candidate => candidate.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .FirstOrDefault();
            if (device is not null && device.TryOpen(out var stream))
            {
                stream.ReadTimeout = 250;
                _device = device;
                _stream = stream;
                _calibration = ReadCalibration(stream);
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
            var hardware = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(24));
            var firmware = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.AsSpan(28));
            var features = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(report.AsSpan(44));
            return $"Firmware 0x{firmware:X8}; hardware 0x{hardware:X8}; features {features >> 8}.{features & 0xFF}";
        }
        catch
        {
            return "Firmware unavailable";
        }
    }

    private static string? ReadIdentity(HidStream stream)
    {
        try
        {
            var report = new byte[64];
            report[0] = 0x09;
            stream.GetFeature(report);
            return string.Join(":", report.Skip(1).Take(6).Reverse().Select(value => value.ToString("X2")));
        }
        catch { return null; }
    }

    private static DualSenseMotionCalibration ReadCalibration(HidStream stream)
    {
        try
        {
            var report = new byte[64];
            report[0] = 0x05;
            stream.GetFeature(report);
            return DualSenseMotionCalibration.Parse(report);
        }
        catch { return DualSenseMotionCalibration.Identity; }
    }

    private static bool IsBluetooth(HidDevice device) =>
        !device.DevicePath.Contains("mi_03", StringComparison.OrdinalIgnoreCase);
}

public sealed class DualSenseOutputState
{
    public byte RightMotor { get; set; }
    public byte LeftMotor { get; set; }
    public byte[] RightTrigger { get; set; } = DualSenseTriggerEffect.Off();
    public byte[] LeftTrigger { get; set; } = DualSenseTriggerEffect.Off();
    public MicrophoneLedMode MicrophoneLed { get; set; }
    public bool MicrophoneMuted { get; set; }
    public ControllerAudioConfiguration Audio { get; set; } = new();
    public byte LedBrightness { get; set; }
    public byte EffectPowerReduction { get; set; }
    public byte LightbarSetup { get; set; }
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
        report[1] = 0xEF; // rumble, adaptive triggers, and audio controls
        report[2] = 0x57; // mic, audio mute, light bar, player LEDs, and effect power
        report[3] = state.RightMotor;
        report[4] = state.LeftMotor;
        report[5] = state.Audio.HeadphoneVolume;
        report[6] = state.Audio.SpeakerVolume;
        report[7] = state.Audio.MicrophoneVolume;
        report[8] = (byte)((state.Audio.ForceInternalMicrophone ? 0x01 : 0) |
            (state.Audio.ForceHeadsetMicrophone ? 0x02 : 0) |
            (state.Audio.RouteToInternalSpeaker ? 0x20 : 0));
        report[9] = (byte)state.MicrophoneLed;
        report[10] = (byte)((state.MicrophoneMuted ? 0x10 : 0) | (state.Audio.AudioMuted ? 0x40 : 0));
        state.RightTrigger.CopyTo(report, 11);
        state.LeftTrigger.CopyTo(report, 22);
        report[37] = (byte)((state.EffectPowerReduction << 4) | state.EffectPowerReduction);
        report[38] = state.Audio.SpeakerPreampGain;
        report[39] = (byte)(0x01 | (state.LightbarSetup != 0 ? 0x02 : 0));
        report[42] = state.LightbarSetup;
        report[43] = state.LedBrightness;
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

    public static uint ComputeBluetoothInputCrc(ReadOnlySpan<byte> reportWithoutCrc)
    {
        var crc = UpdateCrc(uint.MaxValue, 0xA1);
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
        var start = ToActuatorPosition(configuration.StartPosition);
        var end = ToActuatorPosition(configuration.EndPosition);
        var strength = (byte)Math.Clamp((int)Math.Round(configuration.Strength / 100d * 255), 1, 255);
        return configuration.Preset switch
        {
            TriggerPreset.SoftResistance => UniformResistance(start, strength),
            TriggerPreset.Pulse => Vibration(35, (byte)Math.Clamp(configuration.Strength * 63 / 100, 1, 63), start),
            _ => ResistanceSection(start, end, strength)
        };
    }

    public static byte[] Off() => new byte[11] { 0x05, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] UniformResistance(byte start, byte strength) =>
        new byte[11] { 0x01, start, strength, 0, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] ResistanceSection(byte start, byte end, byte strength) =>
        new byte[11] { 0x02, start, end, strength, 0, 0, 0, 0, 0, 0, 0 };

    public static byte[] Vibration(byte frequency, byte strength, byte start) =>
        new byte[11] { 0x06, frequency, strength, start, 0, 0, 0, 0, 0, 0, 0 };

    private static byte ToActuatorPosition(int percent) =>
        (byte)Math.Clamp((int)Math.Round(percent / 100d * 238), 0, 238);
}

public sealed class DualSenseMotionCalibration
{
    private readonly AxisCalibration[] _gyro;
    private readonly AxisCalibration[] _accelerometer;

    private DualSenseMotionCalibration(AxisCalibration[] gyro, AxisCalibration[] accelerometer)
    {
        _gyro = gyro;
        _accelerometer = accelerometer;
    }

    public static DualSenseMotionCalibration Identity { get; } = new(
        Enumerable.Repeat(new AxisCalibration(0, 1, 1), 3).ToArray(),
        Enumerable.Repeat(new AxisCalibration(0, 1, 1), 3).ToArray());

    public static DualSenseMotionCalibration Parse(byte[] report)
    {
        if (report.Length < 35 || report[0] != 0x05)
            throw new InvalidDataException("Expected a DualSense calibration feature report with ID 0x05.");
        var values = Enumerable.Range(0, 17).Select(index => ReadInt16(report, 1 + index * 2)).ToArray();
        var speed2x = values[9] + values[10];
        var gyro = new[]
        {
            CreateGyro(values[0], values[3], values[4], speed2x),
            CreateGyro(values[1], values[5], values[6], speed2x),
            CreateGyro(values[2], values[7], values[8], speed2x)
        };
        var accelerometer = new[]
        {
            CreateAccelerometer(values[11], values[12]),
            CreateAccelerometer(values[13], values[14]),
            CreateAccelerometer(values[15], values[16])
        };
        return new(gyro, accelerometer);
    }

    public double Gyro(int axis, short raw) => _gyro[axis].Apply(raw);
    public double Accelerometer(int axis, short raw) => _accelerometer[axis].Apply(raw);

    private static AxisCalibration CreateGyro(short bias, short plus, short minus, int speed2x) =>
        Create(0, speed2x * 1024, Math.Abs(plus - bias) + Math.Abs(minus - bias));

    private static AxisCalibration CreateAccelerometer(short plus, short minus)
    {
        var range2g = plus - minus;
        return Create(plus - range2g / 2, 2 * 8192, range2g);
    }

    private static AxisCalibration Create(int bias, int numerator, int denominator) =>
        denominator == 0 ? new(0, 1, 1) : new(bias, numerator, denominator);

    private static short ReadInt16(byte[] report, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(report.AsSpan(offset, 2));

    private sealed record AxisCalibration(int Bias, int Numerator, int Denominator)
    {
        public double Apply(short raw) => Numerator * (raw - Bias) / (double)Denominator;
    }
}

public static class DualSenseUsbReportParser
{
    public static InputState Parse(byte[] report) => Parse(report.AsSpan());
    public static InputState Parse(byte[] report, DualSenseMotionCalibration calibration) => Parse(report.AsSpan(), calibration);

    public static InputState Parse(ReadOnlySpan<byte> report) => Parse(report, DualSenseMotionCalibration.Identity);

    public static InputState Parse(ReadOnlySpan<byte> report, DualSenseMotionCalibration calibration)
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
            new StickPoint(calibration.Gyro(0, ReadInt16(report, 16)), calibration.Gyro(1, ReadInt16(report, 18))),
            new StickPoint(calibration.Accelerometer(0, ReadInt16(report, 22)), calibration.Accelerometer(1, ReadInt16(report, 24))),
            touchpad, DateTimeOffset.UtcNow, Math.Min((report[53] & 0x0F) * 10 + 5, 100), touchActive,
            calibration.Gyro(2, ReadInt16(report, 20)), calibration.Accelerometer(2, ReadInt16(report, 26)),
            secondaryTouchpad, secondaryTouchActive, (report[53] & 0xF0) != 0,
            (report[54] & 0x01) != 0, (report[54] & 0x02) != 0, (report[54] & 0x04) != 0);
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
    public static InputState Parse(byte[] report, DualSenseMotionCalibration calibration) => Parse(report.AsSpan(), calibration);

    public static InputState Parse(ReadOnlySpan<byte> report) => Parse(report, DualSenseMotionCalibration.Identity);

    public static InputState Parse(ReadOnlySpan<byte> report, DualSenseMotionCalibration calibration)
    {
        if (report.Length >= 78 && report[0] == 0x31)
        {
            var expectedCrc = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(report.Slice(74, 4));
            if (DualSenseOutputReport.ComputeBluetoothInputCrc(report.Slice(0, 74)) != expectedCrc)
                throw new InvalidDataException("DualSense Bluetooth input CRC validation failed.");
            var usbLayout = new byte[64];
            usbLayout[0] = 0x01;
            report.Slice(2, 63).CopyTo(usbLayout.AsSpan(1));
            return DualSenseUsbReportParser.Parse(usbLayout, calibration);
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
