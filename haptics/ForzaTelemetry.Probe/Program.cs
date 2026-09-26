using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

var port = args.Length > 0 ? int.Parse(args[0]) : 5300;
var durationSeconds = args.Length > 1 ? int.Parse(args[1]) : 30;

using var listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(durationSeconds));

Console.WriteLine($"Listening for Forza UDP telemetry on port {port} for {durationSeconds} seconds...");
Console.WriteLine("Expected FH settings: Data Out=On, IP=127.0.0.1, Port=" + port + ", Format=Sled or Dash");

var stopwatch = Stopwatch.StartNew();
var windowStart = stopwatch.Elapsed;
var packetCount = 0;
var totalPacketCount = 0;
var lastLength = 0;

try
{
    while (!cancellation.IsCancellationRequested)
    {
        var result = await listener.ReceiveAsync(cancellation.Token);
        var packet = result.Buffer;
        totalPacketCount++;
        packetCount++;
        lastLength = packet.Length;

        if (!ForzaPacket.TryParse(packet, out var telemetry))
        {
            Console.WriteLine($"Received unsupported UDP packet length {packet.Length} from {result.RemoteEndPoint}.");
            continue;
        }

        var elapsed = stopwatch.Elapsed;
        if (elapsed - windowStart < TimeSpan.FromMilliseconds(250))
        {
            continue;
        }

        var packetsPerSecond = packetCount / (elapsed - windowStart).TotalSeconds;
        packetCount = 0;
        windowStart = elapsed;

        Console.Write(
            $"\r{packetsPerSecond,5:F1} pkt/s  {packet.Length,3} bytes  race={telemetry.IsRaceOn}  " +
            $"combined slip FL={telemetry.FrontLeft,6:F2} FR={telemetry.FrontRight,6:F2} " +
            $"RL={telemetry.RearLeft,6:F2} RR={telemetry.RearRight,6:F2}  " +
            $"grip L={telemetry.LeftGrip,5:P0} R={telemetry.RightGrip,5:P0}   ");
    }
}
catch (OperationCanceledException)
{
    // Normal timeout.
}

Console.WriteLine();
if (totalPacketCount == 0)
{
    Console.WriteLine("No telemetry arrived. Enable FH6 Data Out and verify the IP address and port.");
    return 1;
}

Console.WriteLine($"Received {totalPacketCount} packets; last packet length was {lastLength} bytes.");
return 0;

internal readonly record struct ForzaPacket(
    bool IsRaceOn,
    float FrontLeft,
    float FrontRight,
    float RearLeft,
    float RearRight)
{
    private const int MinimumPacketLength = 196;
    private const int IsRaceOnOffset = 0;
    private const int CombinedSlipFrontLeftOffset = 180;
    private const int CombinedSlipFrontRightOffset = 184;
    private const int CombinedSlipRearLeftOffset = 188;
    private const int CombinedSlipRearRightOffset = 192;

    public double LeftGrip => GripFromSlip(FrontLeft, RearLeft);

    public double RightGrip => GripFromSlip(FrontRight, RearRight);

    public static bool TryParse(ReadOnlySpan<byte> packet, out ForzaPacket telemetry)
    {
        if (packet.Length < MinimumPacketLength)
        {
            telemetry = default;
            return false;
        }

        telemetry = new ForzaPacket(
            ReadInt32(packet, IsRaceOnOffset) != 0,
            ReadSingle(packet, CombinedSlipFrontLeftOffset),
            ReadSingle(packet, CombinedSlipFrontRightOffset),
            ReadSingle(packet, CombinedSlipRearLeftOffset),
            ReadSingle(packet, CombinedSlipRearRightOffset));

        return AllFinite(
            telemetry.FrontLeft,
            telemetry.FrontRight,
            telemetry.RearLeft,
            telemetry.RearRight);
    }

    private static double GripFromSlip(float first, float second)
    {
        var worstSlip = Math.Max(Math.Abs(first), Math.Abs(second));
        return 1.0 - Math.Clamp(worstSlip, 0.0, 1.0);
    }

    private static bool AllFinite(params float[] values) => values.All(float.IsFinite);

    private static int ReadInt32(ReadOnlySpan<byte> packet, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(packet.Slice(offset, sizeof(int)));

    private static float ReadSingle(ReadOnlySpan<byte> packet, int offset) =>
        BitConverter.Int32BitsToSingle(ReadInt32(packet, offset));
}
