namespace ForzaHaptics.Tester;

internal enum SignalNodeType
{
    Constant,
    Telemetry,
    Group,
    Curve,
    Output
}

internal sealed class ConstantSignalNode : SignalNode
{
    public ConstantSignalNode(string title, Point location, double value = 0.25)
        : base(title, location)
    {
        Value = value;
    }

    public override SignalNodeType Type => SignalNodeType.Constant;
    public double Value { get; set; }
    public bool Active { get; set; } = true;
}

internal enum SignalGroupMode
{
    Maximum,
    Minimum,
    Average,
    SumClamped
}

internal enum HapticEffectMode
{
    Rumble,
    Beep
}

internal enum HapticModulationMode
{
    StrengthOnly,
    FrequencyOnly,
    StrengthAndFrequency
}

internal enum DualSenseTriggerEffectMode
{
    Resistance,
    TensionSlope,
    WeaponClick,
    Vibration,
    BowSnap
}

internal abstract class SignalNode
{
    protected SignalNode(string title, Point location)
    {
        Title = title;
        Location = location;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Title { get; set; }
    public Point Location { get; set; }
    public abstract SignalNodeType Type { get; }
}

internal sealed class TelemetrySignalNode : SignalNode
{
    public TelemetrySignalNode(
        string title,
        Point location,
        string telemetryKey,
        double minimum,
        double maximum)
        : base(title, location)
    {
        TelemetryKey = telemetryKey;
        Minimum = minimum;
        Maximum = maximum;
    }

    public override SignalNodeType Type => SignalNodeType.Telemetry;
    public string TelemetryKey { get; set; }
    public double Minimum { get; set; }
    public double Maximum { get; set; }
    public bool Absolute { get; set; }
    public bool Invert { get; set; }
}

internal sealed class GroupSignalNode : SignalNode
{
    public GroupSignalNode(string title, Point location, SignalGroupMode mode = SignalGroupMode.Maximum)
        : base(title, location)
    {
        Mode = mode;
    }

    public override SignalNodeType Type => SignalNodeType.Group;
    public SignalGroupMode Mode { get; set; }
}

internal sealed class CurveSignalNode : SignalNode
{
    public CurveSignalNode(string title, Point location, bool inverted = false)
        : base(title, location)
    {
        if (!inverted)
        {
            Curve.ResetLinear();
        }
    }

    public override SignalNodeType Type => SignalNodeType.Curve;
    public BezierCurve Curve { get; } = new();
}

internal sealed class OutputSignalNode : SignalNode
{
    public const string SteamNativeTargetId = "steam-native";

    public OutputSignalNode(
        string title,
        Point location,
        int channel,
        HapticEffectMode effectMode,
        double frequencyHz)
        : base(title, location)
    {
        Channel = channel;
        EffectMode = effectMode;
        MinimumFrequencyHz = frequencyHz;
        MaximumFrequencyHz = frequencyHz;
    }

    public override SignalNodeType Type => SignalNodeType.Output;
    public string TargetId { get; set; } = SteamNativeTargetId;
    public int Channel { get; set; }
    public HapticEffectMode EffectMode { get; set; }
    public SignalGroupMode InputMode { get; set; } = SignalGroupMode.Maximum;
    public HapticModulationMode ModulationMode { get; set; } = HapticModulationMode.StrengthOnly;
    public double MinimumFrequencyHz { get; set; } = 70;
    public double MaximumFrequencyHz { get; set; } = 70;
    public double MinimumStrength { get; set; }
    public double MaximumStrength { get; set; } = 1;
    public double BeepRateHz { get; set; } = 6;
    public double BeepDutyCycle { get; set; } = 0.5;
    public bool SilenceAtZero { get; set; } = true;
    public DualSenseTriggerEffectMode DualSenseTriggerEffect { get; set; } =
        DualSenseTriggerEffectMode.Resistance;
    public double TriggerStartPosition { get; set; } = 0.2;
    public double TriggerEndPosition { get; set; } = 0.8;
    public double TriggerSecondaryStrength { get; set; } = 0.25;
    public double TriggerSnapStrength { get; set; } = 0.75;
}

internal sealed record SignalConnection(Guid FromNodeId, Guid ToNodeId);

internal sealed class SignalGraph
{
    public List<SignalNode> Nodes { get; } = [];
    public List<SignalConnection> Connections { get; } = [];
    public bool Enabled { get; set; }

    public void Connect(Guid fromNodeId, Guid toNodeId)
    {
        if (fromNodeId == toNodeId ||
            Connections.Any(connection =>
                connection.FromNodeId == fromNodeId &&
                connection.ToNodeId == toNodeId))
        {
            return;
        }

        Connections.Add(new SignalConnection(fromNodeId, toNodeId));
    }

    public void RemoveNode(Guid id)
    {
        Nodes.RemoveAll(node => node.Id == id);
        Connections.RemoveAll(connection =>
            connection.FromNodeId == id ||
            connection.ToNodeId == id);
    }

    public static SignalGraph CreateDefault()
    {
        var graph = new SignalGraph();
        var leftGrip = new TelemetrySignalNode(
            "Left wheel grip",
            new Point(45, 70),
            "Derived.GripLeft",
            0,
            1);
        var rightGrip = new TelemetrySignalNode(
            "Right wheel grip",
            new Point(45, 220),
            "Derived.GripRight",
            0,
            1);
        var leftCurve = new CurveSignalNode("Grip response L", new Point(330, 70), inverted: true);
        var rightCurve = new CurveSignalNode("Grip response R", new Point(330, 220), inverted: true);
        var leftOutput = new OutputSignalNode(
            "Left grip rumble",
            new Point(635, 70),
            SteamControllerHaptics.LeftGrip,
            HapticEffectMode.Rumble,
            70);
        var rightOutput = new OutputSignalNode(
            "Right grip rumble",
            new Point(635, 220),
            SteamControllerHaptics.RightGrip,
            HapticEffectMode.Rumble,
            70);

        graph.Nodes.AddRange([leftGrip, rightGrip, leftCurve, rightCurve, leftOutput, rightOutput]);
        graph.Connect(leftGrip.Id, leftCurve.Id);
        graph.Connect(leftCurve.Id, leftOutput.Id);
        graph.Connect(rightGrip.Id, rightCurve.Id);
        graph.Connect(rightCurve.Id, rightOutput.Id);
        return graph;
    }
}

internal sealed record GraphHapticOutput(
    Guid NodeId,
    string NodeTitle,
    string TargetId,
    int Channel,
    HapticEffectMode EffectMode,
    double Strength,
    double FrequencyHz,
    DualSenseTriggerEffectMode DualSenseTriggerEffect,
    double TriggerStartPosition,
    double TriggerEndPosition,
    double TriggerSecondaryStrength,
    double TriggerSnapStrength);

internal sealed record GraphEvaluationResult(
    IReadOnlyDictionary<Guid, double> NodeValues,
    IReadOnlyList<GraphHapticOutput> Outputs);

internal sealed class SignalGraphEvaluator
{
    public GraphEvaluationResult Evaluate(SignalGraph graph, ForzaPacket? packet, DateTime now)
    {
        var values = new Dictionary<Guid, double>();
        var visiting = new HashSet<Guid>();

        double EvaluateNode(SignalNode node)
        {
            if (values.TryGetValue(node.Id, out var cached))
            {
                return cached;
            }

            if (!visiting.Add(node.Id))
            {
                return 0;
            }

            var incomingValues = graph.Connections
                .Where(connection => connection.ToNodeId == node.Id)
                .Select(connection => graph.Nodes.FirstOrDefault(candidate => candidate.Id == connection.FromNodeId))
                .Where(candidate => candidate is not null)
                .Select(candidate => EvaluateNode(candidate!))
                .ToArray();

            var value = node switch
            {
                ConstantSignalNode constant => constant.Active ? constant.Value : 0,
                TelemetrySignalNode telemetry => EvaluateTelemetry(telemetry, packet),
                GroupSignalNode group => EvaluateGroup(group.Mode, incomingValues),
                CurveSignalNode curve => curve.Curve.Evaluate(AggregateInputs(incomingValues)),
                OutputSignalNode output => EvaluateGroup(output.InputMode, incomingValues),
                _ => 0
            };

            visiting.Remove(node.Id);
            values[node.Id] = Math.Clamp(value, 0, 1);
            return values[node.Id];
        }

        foreach (var node in graph.Nodes)
        {
            EvaluateNode(node);
        }

        var outputs = graph.Nodes
            .OfType<OutputSignalNode>()
            .Select(output =>
            {
                var input = values.GetValueOrDefault(output.Id);
                var modulatesStrength =
                    output.ModulationMode is
                        HapticModulationMode.StrengthOnly or
                        HapticModulationMode.StrengthAndFrequency;
                var modulatesFrequency =
                    output.ModulationMode is
                        HapticModulationMode.FrequencyOnly or
                        HapticModulationMode.StrengthAndFrequency;
                var strength = modulatesStrength
                    ? Lerp(output.MinimumStrength, output.MaximumStrength, input)
                    : output.MaximumStrength;
                var frequency = modulatesFrequency
                    ? Lerp(output.MinimumFrequencyHz, output.MaximumFrequencyHz, input)
                    : output.MinimumFrequencyHz;

                if (output.SilenceAtZero && input <= 0.0001)
                {
                    strength = 0;
                }

                if (output.EffectMode == HapticEffectMode.Beep)
                {
                    var phase = now.TimeOfDay.TotalSeconds * Math.Max(0.1, output.BeepRateHz);
                    if (phase % 1.0 >= Math.Clamp(output.BeepDutyCycle, 0.05, 0.95))
                    {
                        strength = 0;
                    }
                }

                return new GraphHapticOutput(
                    output.Id,
                    output.Title,
                    output.TargetId,
                    output.Channel,
                    output.EffectMode,
                    Math.Clamp(strength, 0, 1),
                    Math.Clamp(frequency, 20, 800),
                    output.DualSenseTriggerEffect,
                    Math.Clamp(output.TriggerStartPosition, 0, 1),
                    Math.Clamp(output.TriggerEndPosition, 0, 1),
                    Math.Clamp(output.TriggerSecondaryStrength, 0, 1),
                    Math.Clamp(output.TriggerSnapStrength, 0, 1));
            })
            .ToArray();

        return new GraphEvaluationResult(values, outputs);
    }

    private static double EvaluateTelemetry(TelemetrySignalNode node, ForzaPacket? packet)
    {
        if (packet is null)
        {
            return 0;
        }

        var raw = packet.Get(node.TelemetryKey);
        if (node.Absolute)
        {
            raw = Math.Abs(raw);
        }

        var range = node.Maximum - node.Minimum;
        var normalized = Math.Abs(range) < 0.000001
            ? 0
            : Math.Clamp((raw - node.Minimum) / range, 0, 1);
        return node.Invert ? 1 - normalized : normalized;
    }

    private static double EvaluateGroup(SignalGroupMode mode, IReadOnlyList<double> inputs)
    {
        if (inputs.Count == 0)
        {
            return 0;
        }

        return mode switch
        {
            SignalGroupMode.Maximum => inputs.Max(),
            SignalGroupMode.Minimum => inputs.Min(),
            SignalGroupMode.Average => inputs.Average(),
            SignalGroupMode.SumClamped => Math.Clamp(inputs.Sum(), 0, 1),
            _ => 0
        };
    }

    private static double AggregateInputs(IReadOnlyList<double> inputs) =>
        inputs.Count == 0 ? 0 : inputs.Max();

    private static double Lerp(double minimum, double maximum, double amount) =>
        minimum + (maximum - minimum) * Math.Clamp(amount, 0, 1);
}
