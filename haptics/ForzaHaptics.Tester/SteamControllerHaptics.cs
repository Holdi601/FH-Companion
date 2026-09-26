using HidSharp;

namespace ForzaHaptics.Tester;

internal sealed class SteamControllerHaptics : IDisposable
{
    internal const int RightGrip = 0;
    internal const int LeftGrip = 1;
    internal const int LeftPad = 2;
    internal const int RightPad = 3;

    private const ushort ValveVendorId = 0x28DE;
    private const ushort SteamControllerProductId = 0x1304;
    private const uint ControllerUsage = 0xFF000001;
    private const byte PlayReport = 0x83;
    private const byte StopReport = 0x81;

    private readonly List<HidStream> _devices = [];
    private readonly Dictionary<int, (ushort Frequency, int Gain, DateTime SentAt)> _activeChannels = [];

    public int DevicePathCount => _devices.Count;

    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<HapticOutputState> CurrentOutputs =>
        Enumerable.Range(0, 4)
            .Select(channel =>
            {
                if (_activeChannels.TryGetValue(channel, out var state))
                {
                    return new HapticOutputState(
                        channel,
                        ChannelName(channel),
                        state.Gain / 200.0,
                        state.Frequency,
                        true);
                }

                return new HapticOutputState(channel, ChannelName(channel), 0, 0, false);
            })
            .ToArray();

    public bool Connect()
    {
        DisposeDevices();
        _activeChannels.Clear();
        LastError = string.Empty;

        foreach (var device in DeviceList.Local.GetHidDevices(
                     ValveVendorId,
                     SteamControllerProductId))
        {
            try
            {
                if (device.GetMaxOutputReportLength() < 10 ||
                    !device.GetReportDescriptor().DeviceItems.Any(item =>
                        item.Usages.GetAllValues().Contains(ControllerUsage)))
                {
                    continue;
                }

                if (device.TryOpen(out var stream))
                {
                    stream.WriteTimeout = 100;
                    _devices.Add(stream);
                }
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
            }
        }

        if (_devices.Count == 0)
        {
            LastError = string.IsNullOrWhiteSpace(LastError)
                ? "No writable Steam Controller haptic interface was found."
                : LastError;
        }

        return _devices.Count > 0;
    }

    public int Play(int channel, double frequencyHz, double strength)
    {
        var frequency = (ushort)Math.Clamp(Math.Round(frequencyHz), 20, 800);
        var gain = (int)Math.Clamp(Math.Round(strength * 200), 0, 200);
        if (gain == 0)
        {
            return Stop(channel);
        }

        if (_activeChannels.TryGetValue(channel, out var active) &&
            active.Frequency == frequency &&
            active.Gain == gain &&
            DateTime.UtcNow - active.SentAt < TimeSpan.FromSeconds(5))
        {
            return _devices.Count;
        }

        var writes = WritePlayReport(channel, frequency, gain);
        if (writes > 0)
        {
            _activeChannels[channel] = (frequency, gain, DateTime.UtcNow);
        }

        return writes;
    }

    public int Stop(int channel)
    {
        if (!_activeChannels.Remove(channel))
        {
            return _devices.Count;
        }

        return WriteStopReport(channel);
    }

    public void StopAll()
    {
        for (var channel = 0; channel < 4; channel++)
        {
            Stop(channel);
        }
    }

    public int EnforceOwnedOutputs()
    {
        var writes = 0;
        var now = DateTime.UtcNow;
        for (var channel = 0; channel < 4; channel++)
        {
            if (_activeChannels.TryGetValue(channel, out var active))
            {
                writes += WritePlayReport(channel, active.Frequency, active.Gain);
                _activeChannels[channel] = (active.Frequency, active.Gain, now);
            }
            else
            {
                // Always transmit zero for unused channels. This overwrites
                // game/Steam rumble instead of trusting our local state cache.
                writes += WriteStopReport(channel);
            }
        }

        return writes;
    }

    public void Dispose()
    {
        StopAll();
        EnforceOwnedOutputs();
        DisposeDevices();
        GC.SuppressFinalize(this);
    }

    private int WritePlayReport(int channel, ushort frequency, int gain)
    {
        var report = new byte[10];
        report[0] = PlayReport;
        report[1] = MapChannel(channel);
        report[2] = unchecked((byte)(gain - 128));
        report[3] = (byte)(frequency & 0xFF);
        report[4] = (byte)(frequency >> 8);
        report[5] = 0xFF;
        report[6] = 0x7F;
        return Write(report);
    }

    private int WriteStopReport(int channel)
    {
        if (channel >= LeftPad)
        {
            var lraReport = new byte[10];
            lraReport[0] = PlayReport;
            lraReport[1] = MapChannel(channel);
            return Write(lraReport);
        }

        var rumbleReport = new byte[8];
        rumbleReport[0] = StopReport;
        rumbleReport[1] = MapChannel(channel);
        return Write(rumbleReport);
    }

    private int Write(byte[] report)
    {
        var successfulWrites = 0;
        LastError = string.Empty;

        foreach (var stream in _devices)
        {
            try
            {
                var output = new byte[stream.Device.GetMaxOutputReportLength()];
                Array.Copy(report, output, Math.Min(report.Length, output.Length));
                stream.Write(output);
                successfulWrites++;
            }
            catch (Exception exception)
            {
                LastError = exception.Message;
            }
        }

        return successfulWrites;
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices)
        {
            device.Dispose();
        }

        _devices.Clear();
    }

    private static byte MapChannel(int channel) =>
        channel switch
        {
            RightGrip => 4,
            LeftGrip => 3,
            LeftPad => 0,
            RightPad => 1,
            _ => throw new ArgumentOutOfRangeException(nameof(channel))
        };

    private static string ChannelName(int channel) =>
        channel switch
        {
            RightGrip => "Right grip",
            LeftGrip => "Left grip",
            LeftPad => "Left trackpad",
            RightPad => "Right trackpad",
            _ => $"Channel {channel}"
        };

}

internal sealed record HapticOutputState(
    int Channel,
    string Name,
    double Strength,
    double FrequencyHz,
    bool Active);
