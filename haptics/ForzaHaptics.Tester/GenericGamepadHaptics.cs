namespace ForzaHaptics.Tester;

internal sealed record ControllerOutputTarget(
    string Id,
    string Name,
    bool IsSteamNative,
    bool IsDualSenseNative,
    bool SupportsMainRumble,
    bool SupportsTriggerRumble);

internal sealed class GenericGamepadHaptics : IDisposable
{
    internal const string AllTargetId = "generic:all";
    internal const int LowMotor = 0;
    internal const int HighMotor = 1;
    internal const int BothMotors = 2;
    internal const int FrequencyMix = 3;
    internal const int LeftTrigger = 4;
    internal const int RightTrigger = 5;
    internal const int BothTriggers = 6;

    private const ushort ValveVendorId = 0x28DE;
    private const ushort SteamControllerProductId = 0x1304;
    private readonly List<GamepadDevice> _devices = [];
    private readonly Dictionary<string, DesiredRumble> _desired = [];
    // NUR SENDEN, WAS SICH GEAENDERT HAT (seit 2026-09-28). Bis dahin ging alle 20 ms
    // ein Bericht an den Controller, auch wenn er genau dasselbe sagte wie der vorige --
    // ueber Bluetooth teilen sich diese Berichte die Funkzeit mit den Eingaben des
    // Controllers, und die gehoeren dem Spiel. Gleiche Werte gehen hoechstens einmal je
    // Sekunde erneut hinaus, falls ein anderes Programm den Controller zurueckgesetzt hat.
    internal static readonly TimeSpan Auffrischen = TimeSpan.FromSeconds(1);
    private readonly Dictionary<uint, (ushort Low, ushort High, DateTime Zeit)> _xinputGesendet = [];

    public int DeviceCount => _devices.Count;
    public string? ActiveTargetId { get; set; }
    public string LastError { get; private set; } = string.Empty;

    public IReadOnlyList<ControllerOutputTarget> Targets
    {
        get
        {
            var targets = new List<ControllerOutputTarget>();
            if (_devices.Count > 0)
            {
                targets.Add(new ControllerOutputTarget(
                    AllTargetId,
                    "All standard gamepads",
                    false,
                    false,
                    _devices.Any(device => device.SupportsMainRumble),
                    _devices.Any(device => device.SupportsTriggerRumble)));
            }

            targets.AddRange(_devices.Select(device => new ControllerOutputTarget(
                device.Id,
                device.DisplayName,
                false,
                false,
                device.SupportsMainRumble,
                device.SupportsTriggerRumble)));
            return targets;
        }
    }

    public IReadOnlyList<HapticOutputState> CurrentOutputs =>
        _devices.SelectMany(device =>
        {
            var desired = _desired.GetValueOrDefault(device.Id);
            return new[]
            {
                new HapticOutputState(
                    LowMotor,
                    $"{device.DisplayName} · low motor",
                    desired.Low,
                    0,
                    desired.Low > 0.0001),
                new HapticOutputState(
                    HighMotor,
                    $"{device.DisplayName} · high motor",
                    desired.High,
                    0,
                    desired.High > 0.0001),
                new HapticOutputState(
                    LeftTrigger,
                    $"{device.DisplayName} · left trigger",
                    desired.LeftTrigger,
                    0,
                    desired.LeftTrigger > 0.0001),
                new HapticOutputState(
                    RightTrigger,
                    $"{device.DisplayName} · right trigger",
                    desired.RightTrigger,
                    0,
                    desired.RightTrigger > 0.0001)
            };
        }).ToArray();

    public bool Connect()
    {
        DisposeDevices();
        _desired.Clear();
        LastError = string.Empty;

        for (uint userIndex = 0; userIndex < 4; userIndex++)
        {
            if (XInput.XInputGetState(userIndex, out _) != XInput.Success)
            {
                continue;
            }

            var id = $"xinput:{userIndex}";
            _devices.Add(new GamepadDevice(
                id,
                $"XInput Controller #{userIndex + 1} (Xbox / 8BitDo / Steam Input)",
                GamepadBackend.XInput,
                userIndex,
                IntPtr.Zero,
                true,
                false));
            _desired[id] = default;
        }

        if (_devices.Count > 0 || !Sdl.IsGamepadInitialized)
        {
            return _devices.Count > 0;
        }

        Sdl.SDL_UpdateGamepads();

        var occurrences = new Dictionary<(ushort Vendor, ushort Product), int>();
        foreach (var instanceId in Sdl.GetGamepads())
        {
            var vendor = Sdl.SDL_GetGamepadVendorForID(instanceId);
            var product = Sdl.SDL_GetGamepadProductForID(instanceId);
            if (vendor == ValveVendorId && product == SteamControllerProductId)
            {
                continue;
            }

            var handle = Sdl.SDL_OpenGamepad(instanceId);
            if (handle == IntPtr.Zero)
            {
                LastError = Sdl.GetError();
                continue;
            }

            var supportsMain = Sdl.SDL_RumbleGamepad(handle, 0, 0, 1);
            var supportsTriggers = Sdl.SDL_RumbleGamepadTriggers(handle, 0, 0, 1);
            if (!supportsMain && !supportsTriggers)
            {
                Sdl.SDL_CloseGamepad(handle);
                continue;
            }

            var key = (vendor, product);
            var occurrence = occurrences.GetValueOrDefault(key);
            occurrences[key] = occurrence + 1;
            var namePointer = Sdl.SDL_GetGamepadNameForID(instanceId);
            var name = namePointer == IntPtr.Zero
                ? "Standard gamepad"
                : Sdl.PtrToString(namePointer);
            var id = $"generic:{vendor:X4}:{product:X4}:{occurrence}";
            var displayName = $"{name} [{vendor:X4}:{product:X4}]";
            _devices.Add(new GamepadDevice(
                id,
                displayName,
                GamepadBackend.Sdl,
                0,
                handle,
                supportsMain,
                supportsTriggers));
            _desired[id] = default;
        }

        if (_devices.Count == 0 && string.IsNullOrWhiteSpace(LastError))
        {
            LastError = "No SDL-compatible gamepad with rumble support was found.";
        }

        return _devices.Count > 0;
    }

    public void ApplyOutputs(IReadOnlyList<GraphHapticOutput> outputs)
    {
        foreach (var device in _devices)
        {
            if (ActiveTargetId != AllTargetId && ActiveTargetId != device.Id)
            {
                continue;
            }

            var desired = new DesiredRumble();
            foreach (var output in outputs.Where(output =>
                         output.TargetId == AllTargetId ||
                         output.TargetId == device.Id))
            {
                ApplyOutput(ref desired, output, device.SupportsTriggerRumble);
            }

            _desired[device.Id] = desired;
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
        if (_devices.Any(device => device.Backend == GamepadBackend.Sdl))
        {
            Sdl.SDL_UpdateGamepads();
        }

        var writes = 0;
        LastError = string.Empty;

        foreach (var device in _devices)
        {
            if (ActiveTargetId != AllTargetId && ActiveTargetId != device.Id)
            {
                continue;
            }

            var desired = _desired.GetValueOrDefault(device.Id);
            if (device.SupportsMainRumble)
            {
                // SDL filtert gleiche Werte selbst (es verlaengert dann nur die Dauer);
                // XInput nicht -- also hier.
                var success = device.Backend == GamepadBackend.XInput
                    ? SetXInputRumbleWennNoetig(device.UserIndex, desired.Low, desired.High)
                    : Sdl.SDL_RumbleGamepad(
                        device.Handle,
                        ToIntensity(desired.Low),
                        ToIntensity(desired.High),
                        100);
                if (success)
                {
                    writes++;
                }
                else
                {
                    LastError = Sdl.GetError();
                }
            }

            if (device.Backend == GamepadBackend.Sdl && device.SupportsTriggerRumble)
            {
                if (Sdl.SDL_RumbleGamepadTriggers(
                        device.Handle,
                        ToIntensity(desired.LeftTrigger),
                        ToIntensity(desired.RightTrigger),
                        100))
                {
                    writes++;
                }
                else
                {
                    LastError = Sdl.GetError();
                }
            }
        }

        return writes;
    }

    public void ForceZeroAllDevices()
    {
        if (_devices.Any(device => device.Backend == GamepadBackend.Sdl))
        {
            Sdl.SDL_UpdateGamepads();
        }

        foreach (var device in _devices)
        {
            if (device.SupportsMainRumble)
            {
                if (device.Backend == GamepadBackend.XInput)
                {
                    SetXInputRumble(device.UserIndex, 0, 0);
                    _xinputGesendet.Remove(device.UserIndex);
                }
                else
                {
                    Sdl.SDL_RumbleGamepad(device.Handle, 0, 0, 1);
                }
            }

            if (device.Backend == GamepadBackend.Sdl && device.SupportsTriggerRumble)
            {
                Sdl.SDL_RumbleGamepadTriggers(device.Handle, 0, 0, 1);
            }

            _desired[device.Id] = default;
        }
    }

    public void Dispose()
    {
        StopAll();
        EnforceOwnedOutputs();
        DisposeDevices();
        GC.SuppressFinalize(this);
    }

    internal static (double Low, double High, double LeftTrigger, double RightTrigger)
        MapOutputForTest(int channel, double strength, double frequencyHz, bool triggers)
    {
        var desired = new DesiredRumble();
        ApplyOutput(
            ref desired,
            new GraphHapticOutput(
                Guid.NewGuid(),
                "test",
                AllTargetId,
                channel,
                HapticEffectMode.Rumble,
                strength,
                frequencyHz,
                DualSenseTriggerEffectMode.Resistance,
                0.2,
                0.8,
                0.25,
                0.75),
            triggers);
        return (desired.Low, desired.High, desired.LeftTrigger, desired.RightTrigger);
    }

    private static void ApplyOutput(
        ref DesiredRumble desired,
        GraphHapticOutput output,
        bool supportsTriggerRumble)
    {
        var strength = Math.Clamp(output.Strength, 0, 1);
        switch (output.Channel)
        {
            case LowMotor:
                desired.Low = Math.Max(desired.Low, strength);
                break;
            case HighMotor:
                desired.High = Math.Max(desired.High, strength);
                break;
            case BothMotors:
                desired.Low = Math.Max(desired.Low, strength);
                desired.High = Math.Max(desired.High, strength);
                break;
            case FrequencyMix:
                var mix = Math.Clamp(
                    Math.Log(Math.Clamp(output.FrequencyHz, 20, 800) / 20.0) /
                    Math.Log(800.0 / 20.0),
                    0,
                    1);
                desired.Low = Math.Max(desired.Low, strength * Math.Sqrt(1 - mix));
                desired.High = Math.Max(desired.High, strength * Math.Sqrt(mix));
                break;
            case LeftTrigger when supportsTriggerRumble:
                desired.LeftTrigger = Math.Max(desired.LeftTrigger, strength);
                break;
            case RightTrigger when supportsTriggerRumble:
                desired.RightTrigger = Math.Max(desired.RightTrigger, strength);
                break;
            case BothTriggers when supportsTriggerRumble:
                desired.LeftTrigger = Math.Max(desired.LeftTrigger, strength);
                desired.RightTrigger = Math.Max(desired.RightTrigger, strength);
                break;
        }
    }

    private void DisposeDevices()
    {
        foreach (var device in _devices)
        {
            if (device.Backend == GamepadBackend.Sdl)
            {
                Sdl.SDL_CloseGamepad(device.Handle);
            }
        }

        _devices.Clear();
    }

    private static ushort ToIntensity(double value) =>
        (ushort)Math.Clamp(Math.Round(value * ushort.MaxValue), 0, ushort.MaxValue);

    private bool SetXInputRumbleWennNoetig(uint userIndex, double low, double high)
    {
        var lo = ToIntensity(low);
        var hi = ToIntensity(high);
        var jetzt = DateTime.UtcNow;
        if (_xinputGesendet.TryGetValue(userIndex, out var vorher)
            && vorher.Low == lo && vorher.High == hi && jetzt - vorher.Zeit < Auffrischen)
        {
            return true;
        }
        var ok = SetXInputRumble(userIndex, low, high);
        if (ok) { _xinputGesendet[userIndex] = (lo, hi, jetzt); }
        return ok;
    }

    private static bool SetXInputRumble(uint userIndex, double low, double high)
    {
        var vibration = new XInput.Vibration
        {
            LeftMotorSpeed = ToIntensity(low),
            RightMotorSpeed = ToIntensity(high)
        };
        return XInput.XInputSetState(userIndex, ref vibration) == XInput.Success;
    }

    private enum GamepadBackend
    {
        Sdl,
        XInput
    }

    private sealed record GamepadDevice(
        string Id,
        string DisplayName,
        GamepadBackend Backend,
        uint UserIndex,
        IntPtr Handle,
        bool SupportsMainRumble,
        bool SupportsTriggerRumble);

    private struct DesiredRumble
    {
        public double Low;
        public double High;
        public double LeftTrigger;
        public double RightTrigger;
    }
}
