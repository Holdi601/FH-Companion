using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Win32.SafeHandles;

namespace ForzaHaptics.Tester;

internal sealed partial class DualSenseHaptics : IDisposable
{
    internal const int LowBodyMotor = 0;
    internal const int HighBodyMotor = 1;
    internal const int BothBodyMotors = 2;
    internal const int FrequencyMix = 3;
    internal const int LeftAdaptiveTrigger = 10;
    internal const int RightAdaptiveTrigger = 11;
    internal const int BothAdaptiveTriggers = 12;

    private const int SonyVendorId = 0x054C;
    private static readonly int[] ProductIds = [0x0CE6, 0x0DF2];
    private readonly List<DualSenseDevice> _devices = [];
    private readonly Dictionary<string, DesiredState> _desired = [];
    // NUR SENDEN, WAS SICH GEAENDERT HAT (seit 2026-09-28). Bis dahin ging alle 20 ms
    // ein Bericht an den Controller, auch wenn er genau dasselbe sagte wie der vorige --
    // ueber Bluetooth teilen sich diese Berichte die Funkzeit mit den Eingaben des
    // Controllers, und die gehoeren dem Spiel. Gleiche Werte gehen hoechstens einmal je
    // Sekunde erneut hinaus, falls ein anderes Programm den Controller zurueckgesetzt hat.
    internal static readonly TimeSpan Auffrischen = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, (byte[] Bericht, DateTime Zeit)> _gesendet = [];

    public string? ActiveTargetId { get; set; }
    public int DeviceCount => _devices.Count;
    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<ControllerOutputTarget> Targets =>
        _devices.Select(device => new ControllerOutputTarget(
            device.Id,
            device.DisplayName,
            false,
            true,
            true,
            false)).ToArray();

    public IReadOnlyList<HapticOutputState> CurrentOutputs =>
        _devices.SelectMany(device =>
        {
            var state = _desired.GetValueOrDefault(device.Id);
            return new[]
            {
                new HapticOutputState(
                    LowBodyMotor,
                    $"{device.DisplayName} · low body haptic",
                    state.LowMotor,
                    0,
                    state.LowMotor > 0.0001),
                new HapticOutputState(
                    HighBodyMotor,
                    $"{device.DisplayName} · high body haptic",
                    state.HighMotor,
                    0,
                    state.HighMotor > 0.0001),
                new HapticOutputState(
                    LeftAdaptiveTrigger,
                    $"{device.DisplayName} · L2 adaptive trigger",
                    state.LeftTrigger?.Strength ?? 0,
                    state.LeftTrigger?.FrequencyHz ?? 0,
                    state.LeftTrigger is not null),
                new HapticOutputState(
                    RightAdaptiveTrigger,
                    $"{device.DisplayName} · R2 adaptive trigger",
                    state.RightTrigger?.Strength ?? 0,
                    state.RightTrigger?.FrequencyHz ?? 0,
                    state.RightTrigger is not null)
            };
        }).ToArray();

    public bool Connect()
    {
        DisposeDevices();
        _desired.Clear();
        LastError = string.Empty;
        var occurrence = 0;

        foreach (var productId in ProductIds)
        {
            foreach (var device in DeviceList.Local
                         .GetHidDevices(SonyVendorId, productId)
                         .OrderByDescending(item => item.GetMaxOutputReportLength()))
            {
                var outputLength = device.GetMaxOutputReportLength();
                if (outputLength < 48)
                {
                    continue;
                }

                var handle = NativeMethods.CreateFile(
                    device.DevicePath,
                    NativeMethods.GenericRead | NativeMethods.GenericWrite,
                    NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
                    IntPtr.Zero,
                    NativeMethods.OpenExisting,
                    0,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    LastError = $"DualSense open failed: {Marshal.GetLastWin32Error()}";
                    handle.Dispose();
                    continue;
                }

                var bluetooth = outputLength >= 78 ||
                                device.DevicePath.Contains(
                                    "BTH",
                                    StringComparison.OrdinalIgnoreCase);
                var model = productId == 0x0DF2 ? "DualSense Edge" : "DualSense";
                var id = $"dualsense:{productId:X4}:{occurrence++}";
                _devices.Add(new DualSenseDevice(
                    id,
                    $"{model} · native adaptive triggers · {(bluetooth ? "Bluetooth" : "USB")}",
                    handle,
                    bluetooth));
                _desired[id] = default;
            }
        }

        if (_devices.Count == 0 && string.IsNullOrWhiteSpace(LastError))
        {
            LastError = "No directly accessible DualSense or DualSense Edge was found.";
        }

        return _devices.Count > 0;
    }

    public void ApplyOutputs(IReadOnlyList<GraphHapticOutput> outputs)
    {
        foreach (var device in _devices)
        {
            if (device.Id != ActiveTargetId)
            {
                continue;
            }

            var state = new DesiredState();
            var matching = outputs.Where(output => output.TargetId == device.Id).ToArray();
            foreach (var output in matching)
            {
                ApplyBodyOutput(ref state, output);
            }

            state.LeftTrigger = StrongestTrigger(
                matching,
                LeftAdaptiveTrigger,
                BothAdaptiveTriggers);
            state.RightTrigger = StrongestTrigger(
                matching,
                RightAdaptiveTrigger,
                BothAdaptiveTriggers);
            _desired[device.Id] = state;
        }
    }

    public void StopAll()
    {
        foreach (var device in _devices)
        {
            _desired[device.Id] = default;
        }
    }

    public int EnforceOwnedOutputs()
    {
        var writes = 0;
        LastError = string.Empty;
        var jetzt = DateTime.UtcNow;
        foreach (var device in _devices.Where(device => device.Id == ActiveTargetId))
        {
            var bericht = BuildReport(device.Bluetooth, _desired.GetValueOrDefault(device.Id));
            if (_gesendet.TryGetValue(device.Id, out var vorher)
                && jetzt - vorher.Zeit < Auffrischen
                && vorher.Bericht.AsSpan().SequenceEqual(bericht))
            {
                continue;
            }
            if (Schreibe(device, bericht))
            {
                _gesendet[device.Id] = (bericht, jetzt);
                writes++;
            }
        }

        return writes;
    }

    public void ForceZeroAllDevices()
    {
        foreach (var device in _devices)
        {
            WriteState(device, default);
            _desired[device.Id] = default;
        }
    }

    public void Dispose()
    {
        ForceZeroAllDevices();
        DisposeDevices();
        GC.SuppressFinalize(this);
    }

    internal static byte[] BuildReportForTest(
        bool bluetooth,
        double lowMotor,
        double highMotor,
        GraphHapticOutput? leftTrigger,
        GraphHapticOutput? rightTrigger)
    {
        return BuildReport(
            bluetooth,
            new DesiredState
            {
                LowMotor = lowMotor,
                HighMotor = highMotor,
                LeftTrigger = leftTrigger,
                RightTrigger = rightTrigger
            });
    }

    internal static byte[] BuildTriggerEffectForTest(GraphHapticOutput? output) =>
        BuildTriggerEffect(output);

    private bool WriteState(DualSenseDevice device, DesiredState state)
    {
        var ok = Schreibe(device, BuildReport(device.Bluetooth, state));
        _gesendet.Remove(device.Id);
        return ok;
    }

    private bool Schreibe(DualSenseDevice device, byte[] report)
    {
        if (NativeMethods.WriteFile(
                device.Handle,
                report,
                report.Length,
                out var written,
                IntPtr.Zero) &&
            written == report.Length)
        {
            return true;
        }

        LastError = $"DualSense write failed: {Marshal.GetLastWin32Error()}";
        return false;
    }

    private static byte[] BuildReport(bool bluetooth, DesiredState state)
    {
        var report = new byte[bluetooth ? 78 : 48];
        var payloadOffset = bluetooth ? 3 : 1;
        report[0] = bluetooth ? (byte)0x31 : (byte)0x02;
        if (bluetooth)
        {
            report[1] = 0x00;
            report[2] = 0x10;
        }

        // Rumble emulation, disable audio haptics, and allow both trigger FFB blocks.
        report[payloadOffset] = 0x0F;
        report[payloadOffset + 1] = 0x00;
        report[payloadOffset + 2] = ToByte(state.HighMotor);
        report[payloadOffset + 3] = ToByte(state.LowMotor);

        Array.Copy(
            BuildTriggerEffect(state.RightTrigger),
            0,
            report,
            payloadOffset + 10,
            11);
        Array.Copy(
            BuildTriggerEffect(state.LeftTrigger),
            0,
            report,
            payloadOffset + 21,
            11);

        if (bluetooth)
        {
            var crc = ComputeBluetoothCrc(report);
            report[74] = (byte)crc;
            report[75] = (byte)(crc >> 8);
            report[76] = (byte)(crc >> 16);
            report[77] = (byte)(crc >> 24);
        }

        return report;
    }

    private static byte[] BuildTriggerEffect(GraphHapticOutput? output)
    {
        if (output is null || output.Strength <= 0.0001)
        {
            return DualSenseTriggerEffects.Off();
        }

        var start = ToZone(output.TriggerStartPosition, 9);
        var end = ToZone(output.TriggerEndPosition, 9);
        var strength = ToStrength(output.Strength);
        return output.DualSenseTriggerEffect switch
        {
            DualSenseTriggerEffectMode.Resistance =>
                DualSenseTriggerEffects.Feedback(start, strength),
            DualSenseTriggerEffectMode.TensionSlope =>
                DualSenseTriggerEffects.SlopeFeedback(
                    Math.Min(start, (byte)8),
                    Math.Max((byte)(Math.Min(start, (byte)8) + 1), end),
                    Math.Max((byte)1, ToStrength(
                        output.Strength * output.TriggerSecondaryStrength)),
                    strength),
            DualSenseTriggerEffectMode.WeaponClick =>
                DualSenseTriggerEffects.Weapon(
                    Math.Clamp(start, (byte)2, (byte)7),
                    Math.Clamp(end, (byte)3, (byte)8),
                    strength),
            DualSenseTriggerEffectMode.Vibration =>
                DualSenseTriggerEffects.Vibration(
                    start,
                    strength,
                    (byte)Math.Clamp(Math.Round(output.FrequencyHz), 1, 255)),
            DualSenseTriggerEffectMode.BowSnap =>
                DualSenseTriggerEffects.Bow(
                    Math.Min(start, (byte)7),
                    Math.Clamp(end, (byte)1, (byte)8),
                    strength,
                    Math.Max((byte)1, ToStrength(
                        output.Strength * output.TriggerSnapStrength))),
            _ => DualSenseTriggerEffects.Off()
        };
    }

    private static GraphHapticOutput? StrongestTrigger(
        IReadOnlyList<GraphHapticOutput> outputs,
        int sideChannel,
        int bothChannel) =>
        outputs
            .Where(output =>
                output.Channel == sideChannel ||
                output.Channel == bothChannel)
            .OrderByDescending(output => output.Strength)
            .FirstOrDefault();

    private static void ApplyBodyOutput(
        ref DesiredState state,
        GraphHapticOutput output)
    {
        var strength = Math.Clamp(output.Strength, 0, 1);
        switch (output.Channel)
        {
            case LowBodyMotor:
                state.LowMotor = Math.Max(state.LowMotor, strength);
                break;
            case HighBodyMotor:
                state.HighMotor = Math.Max(state.HighMotor, strength);
                break;
            case BothBodyMotors:
                state.LowMotor = Math.Max(state.LowMotor, strength);
                state.HighMotor = Math.Max(state.HighMotor, strength);
                break;
            case FrequencyMix:
                var mix = Math.Clamp(
                    Math.Log(Math.Clamp(output.FrequencyHz, 20, 800) / 20.0) /
                    Math.Log(800.0 / 20.0),
                    0,
                    1);
                state.LowMotor = Math.Max(
                    state.LowMotor,
                    strength * Math.Sqrt(1 - mix));
                state.HighMotor = Math.Max(
                    state.HighMotor,
                    strength * Math.Sqrt(mix));
                break;
        }
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices)
        {
            device.Handle.Dispose();
        }

        _devices.Clear();
    }

    private static byte ToByte(double value) =>
        (byte)Math.Clamp(Math.Round(value * 255), 0, 255);

    private static byte ToZone(double value, int maximum) =>
        (byte)Math.Clamp(Math.Round(value * maximum), 0, maximum);

    private static byte ToStrength(double value) =>
        (byte)Math.Clamp(Math.Ceiling(value * 8), 0, 8);

    private static uint ComputeBluetoothCrc(byte[] report)
    {
        var crc = 0xFFFF_FFFFu;
        crc = StepCrc(crc, 0xA2);
        for (var index = 0; index < 74; index++)
        {
            crc = StepCrc(crc, report[index]);
        }

        return ~crc;
    }

    private static uint StepCrc(uint crc, byte value)
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0
                ? (crc >> 1) ^ 0xEDB8_8320u
                : crc >> 1;
        }

        return crc;
    }

    private sealed record DualSenseDevice(
        string Id,
        string DisplayName,
        SafeFileHandle Handle,
        bool Bluetooth);

    private struct DesiredState
    {
        public double LowMotor;
        public double HighMotor;
        public GraphHapticOutput? LeftTrigger;
        public GraphHapticOutput? RightTrigger;
    }

    private static partial class NativeMethods
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;

        [LibraryImport(
            "kernel32.dll",
            EntryPoint = "CreateFileW",
            SetLastError = true,
            StringMarshalling = StringMarshalling.Utf16)]
        internal static partial SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WriteFile(
            SafeFileHandle file,
            byte[] buffer,
            int bytesToWrite,
            out int bytesWritten,
            IntPtr overlapped);
    }
}
