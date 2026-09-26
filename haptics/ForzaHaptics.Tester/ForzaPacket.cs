using System.Buffers.Binary;

namespace ForzaHaptics.Tester;

internal enum TelemetryValueType
{
    Signed32,
    Unsigned32,
    Float32,
    Unsigned16,
    Unsigned8,
    Signed8
}

internal sealed record TelemetryDescriptor(
    string Key,
    string DisplayName,
    string Category,
    int Offset,
    TelemetryValueType Type,
    string Unit = "",
    double DefaultMinimum = 0,
    double DefaultMaximum = 1);

internal sealed class ForzaPacket
{
    private const int PacketLength = 324;
    private const double LockDeadzone = 0.04;
    private readonly Dictionary<string, double> _values;

    private ForzaPacket(Dictionary<string, double> values)
    {
        _values = values;
    }

    public static IReadOnlyList<TelemetryDescriptor> Descriptors { get; } =
    [
        D("IsRaceOn", "Race active", "Race", 0, TelemetryValueType.Signed32),
        D("TimestampMS", "Timestamp", "Race", 4, TelemetryValueType.Unsigned32, "ms", 0, 600000),
        D("EngineMaxRpm", "Maximum RPM", "Engine", 8, TelemetryValueType.Float32, "rpm", 0, 12000),
        D("EngineIdleRpm", "Idle RPM", "Engine", 12, TelemetryValueType.Float32, "rpm", 0, 2000),
        D("CurrentEngineRpm", "Current RPM", "Engine", 16, TelemetryValueType.Float32, "rpm", 0, 12000),
        D("AccelerationX", "Acceleration X", "Motion", 20, TelemetryValueType.Float32, "m/s²", -30, 30),
        D("AccelerationY", "Acceleration Y", "Motion", 24, TelemetryValueType.Float32, "m/s²", -30, 30),
        D("AccelerationZ", "Acceleration Z", "Motion", 28, TelemetryValueType.Float32, "m/s²", -30, 30),
        D("VelocityX", "Velocity X", "Motion", 32, TelemetryValueType.Float32, "m/s", -100, 100),
        D("VelocityY", "Velocity Y", "Motion", 36, TelemetryValueType.Float32, "m/s", -100, 100),
        D("VelocityZ", "Velocity Z", "Motion", 40, TelemetryValueType.Float32, "m/s", -100, 100),
        D("AngularVelocityX", "Angular velocity X", "Motion", 44, TelemetryValueType.Float32, "rad/s", -10, 10),
        D("AngularVelocityY", "Angular velocity Y", "Motion", 48, TelemetryValueType.Float32, "rad/s", -10, 10),
        D("AngularVelocityZ", "Angular velocity Z", "Motion", 52, TelemetryValueType.Float32, "rad/s", -10, 10),
        D("Yaw", "Yaw", "Orientation", 56, TelemetryValueType.Float32, "rad", -3.142, 3.142),
        D("Pitch", "Pitch", "Orientation", 60, TelemetryValueType.Float32, "rad", -1.571, 1.571),
        D("Roll", "Roll", "Orientation", 64, TelemetryValueType.Float32, "rad", -3.142, 3.142),
        D("NormalizedSuspensionTravelFrontLeft", "Suspension FL", "Suspension", 68, TelemetryValueType.Float32),
        D("NormalizedSuspensionTravelFrontRight", "Suspension FR", "Suspension", 72, TelemetryValueType.Float32),
        D("NormalizedSuspensionTravelRearLeft", "Suspension RL", "Suspension", 76, TelemetryValueType.Float32),
        D("NormalizedSuspensionTravelRearRight", "Suspension RR", "Suspension", 80, TelemetryValueType.Float32),
        D("TireSlipRatioFrontLeft", "Slip ratio FL", "Tires", 84, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipRatioFrontRight", "Slip ratio FR", "Tires", 88, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipRatioRearLeft", "Slip ratio RL", "Tires", 92, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipRatioRearRight", "Slip ratio RR", "Tires", 96, TelemetryValueType.Float32, "", -2, 2),
        D("WheelRotationSpeedFrontLeft", "Wheel speed FL", "Wheels", 100, TelemetryValueType.Float32, "rad/s", -300, 300),
        D("WheelRotationSpeedFrontRight", "Wheel speed FR", "Wheels", 104, TelemetryValueType.Float32, "rad/s", -300, 300),
        D("WheelRotationSpeedRearLeft", "Wheel speed RL", "Wheels", 108, TelemetryValueType.Float32, "rad/s", -300, 300),
        D("WheelRotationSpeedRearRight", "Wheel speed RR", "Wheels", 112, TelemetryValueType.Float32, "rad/s", -300, 300),
        D("WheelOnRumbleStripFrontLeft", "Rumble strip FL", "Surface", 116, TelemetryValueType.Signed32),
        D("WheelOnRumbleStripFrontRight", "Rumble strip FR", "Surface", 120, TelemetryValueType.Signed32),
        D("WheelOnRumbleStripRearLeft", "Rumble strip RL", "Surface", 124, TelemetryValueType.Signed32),
        D("WheelOnRumbleStripRearRight", "Rumble strip RR", "Surface", 128, TelemetryValueType.Signed32),
        D("WheelInPuddleFrontLeft", "Puddle FL", "Surface", 132, TelemetryValueType.Signed32),
        D("WheelInPuddleFrontRight", "Puddle FR", "Surface", 136, TelemetryValueType.Signed32),
        D("WheelInPuddleRearLeft", "Puddle RL", "Surface", 140, TelemetryValueType.Signed32),
        D("WheelInPuddleRearRight", "Puddle RR", "Surface", 144, TelemetryValueType.Signed32),
        D("SurfaceRumbleFrontLeft", "Surface rumble FL", "Surface", 148, TelemetryValueType.Float32),
        D("SurfaceRumbleFrontRight", "Surface rumble FR", "Surface", 152, TelemetryValueType.Float32),
        D("SurfaceRumbleRearLeft", "Surface rumble RL", "Surface", 156, TelemetryValueType.Float32),
        D("SurfaceRumbleRearRight", "Surface rumble RR", "Surface", 160, TelemetryValueType.Float32),
        D("TireSlipAngleFrontLeft", "Slip angle FL", "Tires", 164, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipAngleFrontRight", "Slip angle FR", "Tires", 168, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipAngleRearLeft", "Slip angle RL", "Tires", 172, TelemetryValueType.Float32, "", -2, 2),
        D("TireSlipAngleRearRight", "Slip angle RR", "Tires", 176, TelemetryValueType.Float32, "", -2, 2),
        D("TireCombinedSlipFrontLeft", "Combined slip FL", "Tires", 180, TelemetryValueType.Float32, "", -2, 2),
        D("TireCombinedSlipFrontRight", "Combined slip FR", "Tires", 184, TelemetryValueType.Float32, "", -2, 2),
        D("TireCombinedSlipRearLeft", "Combined slip RL", "Tires", 188, TelemetryValueType.Float32, "", -2, 2),
        D("TireCombinedSlipRearRight", "Combined slip RR", "Tires", 192, TelemetryValueType.Float32, "", -2, 2),
        D("SuspensionTravelMetersFrontLeft", "Travel FL", "Suspension", 196, TelemetryValueType.Float32, "m", 0, 0.5),
        D("SuspensionTravelMetersFrontRight", "Travel FR", "Suspension", 200, TelemetryValueType.Float32, "m", 0, 0.5),
        D("SuspensionTravelMetersRearLeft", "Travel RL", "Suspension", 204, TelemetryValueType.Float32, "m", 0, 0.5),
        D("SuspensionTravelMetersRearRight", "Travel RR", "Suspension", 208, TelemetryValueType.Float32, "m", 0, 0.5),
        D("CarOrdinal", "Car ordinal", "Vehicle", 212, TelemetryValueType.Signed32, "", 0, 4000),
        D("CarClass", "Car class", "Vehicle", 216, TelemetryValueType.Signed32, "", 0, 7),
        D("CarPerformanceIndex", "Performance index", "Vehicle", 220, TelemetryValueType.Signed32, "", 100, 999),
        D("DrivetrainType", "Drivetrain", "Vehicle", 224, TelemetryValueType.Signed32, "", 0, 2),
        D("NumCylinders", "Cylinders", "Vehicle", 228, TelemetryValueType.Signed32, "", 0, 16),
        D("CarGroup", "Car group", "Vehicle", 232, TelemetryValueType.Unsigned32, "", 0, 1000),
        D("SmashableVelDiff", "Smashable velocity loss", "Collision", 236, TelemetryValueType.Float32, "m/s", 0, 50),
        D("SmashableMass", "Smashable mass", "Collision", 240, TelemetryValueType.Float32, "kg", 0, 5000),
        D("PositionX", "World position X", "Position", 244, TelemetryValueType.Float32, "m", -100000, 100000),
        D("PositionY", "World position Y", "Position", 248, TelemetryValueType.Float32, "m", -100000, 100000),
        D("PositionZ", "World position Z", "Position", 252, TelemetryValueType.Float32, "m", -100000, 100000),
        D("Speed", "Speed", "Vehicle", 256, TelemetryValueType.Float32, "m/s", 0, 150),
        D("Power", "Power", "Engine", 260, TelemetryValueType.Float32, "W", -1500000, 1500000),
        D("Torque", "Torque", "Engine", 264, TelemetryValueType.Float32, "N·m", -3000, 3000),
        D("TireTempFrontLeft", "Tire temperature FL", "Tires", 268, TelemetryValueType.Float32, "°C", 0, 200),
        D("TireTempFrontRight", "Tire temperature FR", "Tires", 272, TelemetryValueType.Float32, "°C", 0, 200),
        D("TireTempRearLeft", "Tire temperature RL", "Tires", 276, TelemetryValueType.Float32, "°C", 0, 200),
        D("TireTempRearRight", "Tire temperature RR", "Tires", 280, TelemetryValueType.Float32, "°C", 0, 200),
        D("Boost", "Boost", "Engine", 284, TelemetryValueType.Float32, "psi", 0, 50),
        D("Fuel", "Fuel", "Vehicle", 288, TelemetryValueType.Float32),
        D("DistanceTraveled", "Distance traveled", "Race", 292, TelemetryValueType.Float32, "m", 0, 100000),
        D("BestLap", "Best lap", "Race", 296, TelemetryValueType.Float32, "s", 0, 1000),
        D("LastLap", "Last lap", "Race", 300, TelemetryValueType.Float32, "s", 0, 1000),
        D("CurrentLap", "Current lap", "Race", 304, TelemetryValueType.Float32, "s", 0, 1000),
        D("CurrentRaceTime", "Current race time", "Race", 308, TelemetryValueType.Float32, "s", 0, 100000),
        D("LapNumber", "Lap number", "Race", 312, TelemetryValueType.Unsigned16, "", 0, 100),
        D("RacePosition", "Race position", "Race", 314, TelemetryValueType.Unsigned8, "", 0, 24),
        D("Accel", "Accelerator", "Inputs", 315, TelemetryValueType.Unsigned8, "", 0, 255),
        D("Brake", "Brake", "Inputs", 316, TelemetryValueType.Unsigned8, "", 0, 255),
        D("Clutch", "Clutch", "Inputs", 317, TelemetryValueType.Unsigned8, "", 0, 255),
        D("HandBrake", "Handbrake", "Inputs", 318, TelemetryValueType.Unsigned8, "", 0, 255),
        D("Gear", "Gear", "Inputs", 319, TelemetryValueType.Unsigned8, "", 0, 10),
        D("Steer", "Steering", "Inputs", 320, TelemetryValueType.Signed8, "", -127, 127),
        D("NormalizedDrivingLine", "Driving line", "Inputs", 321, TelemetryValueType.Signed8, "", -127, 127),
        D("NormalizedAIBrakeDifference", "AI brake difference", "Inputs", 322, TelemetryValueType.Signed8, "", -127, 127)
    ];

    public static IReadOnlyList<TelemetryDescriptor> DerivedDescriptors { get; } =
    [
        D("Derived.GripFrontLeft", "Grip FL", "Derived / Grip", -1, TelemetryValueType.Float32),
        D("Derived.GripFrontRight", "Grip FR", "Derived / Grip", -1, TelemetryValueType.Float32),
        D("Derived.GripRearLeft", "Grip RL", "Derived / Grip", -1, TelemetryValueType.Float32),
        D("Derived.GripRearRight", "Grip RR", "Derived / Grip", -1, TelemetryValueType.Float32),
        D("Derived.GripLeft", "Grip left wheels", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.GripRight", "Grip right wheels", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.GripFront", "Grip front wheels", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.GripRear", "Grip rear wheels", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.LockFrontLeft", "Wheel lock FL", "Derived / Lock", -1, TelemetryValueType.Float32),
        D("Derived.LockFrontRight", "Wheel lock FR", "Derived / Lock", -1, TelemetryValueType.Float32),
        D("Derived.LockRearLeft", "Wheel lock RL", "Derived / Lock", -1, TelemetryValueType.Float32),
        D("Derived.LockRearRight", "Wheel lock RR", "Derived / Lock", -1, TelemetryValueType.Float32),
        D("Derived.LockLeft", "Wheel lock left", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.LockRight", "Wheel lock right", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.LockFront", "Wheel lock front", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.LockRear", "Wheel lock rear", "Derived / Groups", -1, TelemetryValueType.Float32),
        D("Derived.SpeedKmh", "Speed", "Derived / Vehicle", -1, TelemetryValueType.Float32, "km/h", 0, 500),
        D("Derived.RpmNormalized", "RPM normalized", "Derived / Engine", -1, TelemetryValueType.Float32)
    ];

    public static IReadOnlyList<TelemetryDescriptor> AllDescriptors { get; } =
        Descriptors.Concat(DerivedDescriptors).ToArray();

    public IReadOnlyDictionary<string, double> Values => _values;

    public bool IsRaceOn => Get("IsRaceOn") != 0;
    public double Brake => Get("Brake") / 255.0;
    public double CombinedSlipFrontLeft => Get("TireCombinedSlipFrontLeft");
    public double CombinedSlipFrontRight => Get("TireCombinedSlipFrontRight");
    public double CombinedSlipRearLeft => Get("TireCombinedSlipRearLeft");
    public double CombinedSlipRearRight => Get("TireCombinedSlipRearRight");
    public double SlipRatioFrontLeft => Get("TireSlipRatioFrontLeft");
    public double SlipRatioFrontRight => Get("TireSlipRatioFrontRight");
    public double SlipRatioRearLeft => Get("TireSlipRatioRearLeft");
    public double SlipRatioRearRight => Get("TireSlipRatioRearRight");
    public double LeftGrip => Get("Derived.GripLeft");
    public double RightGrip => Get("Derived.GripRight");
    public double LeftLock => Get("Derived.LockLeft");
    public double RightLock => Get("Derived.LockRight");

    public double Get(string key) => _values.GetValueOrDefault(key);

    public static bool TryParse(ReadOnlySpan<byte> packet, out ForzaPacket telemetry)
    {
        if (packet.Length < PacketLength)
        {
            telemetry = null!;
            return false;
        }

        var values = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in Descriptors)
        {
            var value = ReadValue(packet, descriptor);
            if (!double.IsFinite(value))
            {
                telemetry = null!;
                return false;
            }

            values[descriptor.Key] = value;
        }

        AddDerived(values);
        telemetry = new ForzaPacket(values);
        return true;
    }

    private static void AddDerived(Dictionary<string, double> values)
    {
        var gripFl = Grip(values["TireCombinedSlipFrontLeft"]);
        var gripFr = Grip(values["TireCombinedSlipFrontRight"]);
        var gripRl = Grip(values["TireCombinedSlipRearLeft"]);
        var gripRr = Grip(values["TireCombinedSlipRearRight"]);
        var brake = values["Brake"] / 255.0;
        var lockFl = Lock(values["TireSlipRatioFrontLeft"], brake);
        var lockFr = Lock(values["TireSlipRatioFrontRight"], brake);
        var lockRl = Lock(values["TireSlipRatioRearLeft"], brake);
        var lockRr = Lock(values["TireSlipRatioRearRight"], brake);

        values["Derived.GripFrontLeft"] = gripFl;
        values["Derived.GripFrontRight"] = gripFr;
        values["Derived.GripRearLeft"] = gripRl;
        values["Derived.GripRearRight"] = gripRr;
        values["Derived.GripLeft"] = Math.Min(gripFl, gripRl);
        values["Derived.GripRight"] = Math.Min(gripFr, gripRr);
        values["Derived.GripFront"] = Math.Min(gripFl, gripFr);
        values["Derived.GripRear"] = Math.Min(gripRl, gripRr);
        values["Derived.LockFrontLeft"] = lockFl;
        values["Derived.LockFrontRight"] = lockFr;
        values["Derived.LockRearLeft"] = lockRl;
        values["Derived.LockRearRight"] = lockRr;
        values["Derived.LockLeft"] = Math.Max(lockFl, lockRl);
        values["Derived.LockRight"] = Math.Max(lockFr, lockRr);
        values["Derived.LockFront"] = Math.Max(lockFl, lockFr);
        values["Derived.LockRear"] = Math.Max(lockRl, lockRr);
        values["Derived.SpeedKmh"] = values["Speed"] * 3.6;
        values["Derived.RpmNormalized"] = values["EngineMaxRpm"] <= 1
            ? 0
            : Math.Clamp(values["CurrentEngineRpm"] / values["EngineMaxRpm"], 0, 1);
    }

    private static double Grip(double combinedSlip) =>
        1.0 - Math.Clamp(Math.Abs(combinedSlip), 0.0, 1.0);

    private static double Lock(double slipRatio, double brake)
    {
        if (brake < 0.02)
        {
            return 0;
        }

        return Math.Clamp((Math.Max(0, -slipRatio) - LockDeadzone) / (1 - LockDeadzone), 0, 1);
    }

    private static double ReadValue(ReadOnlySpan<byte> packet, TelemetryDescriptor descriptor) =>
        descriptor.Type switch
        {
            TelemetryValueType.Signed32 => BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(descriptor.Offset, 4)),
            TelemetryValueType.Unsigned32 => BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(descriptor.Offset, 4)),
            TelemetryValueType.Float32 => BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(descriptor.Offset, 4))),
            TelemetryValueType.Unsigned16 => BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(descriptor.Offset, 2)),
            TelemetryValueType.Unsigned8 => packet[descriptor.Offset],
            TelemetryValueType.Signed8 => unchecked((sbyte)packet[descriptor.Offset]),
            _ => 0
        };

    private static TelemetryDescriptor D(
        string key,
        string name,
        string category,
        int offset,
        TelemetryValueType type,
        string unit = "",
        double minimum = 0,
        double maximum = 1) =>
        new(key, name, category, offset, type, unit, minimum, maximum);
}
