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

    /// <summary>
    /// Der eingebaute Graph, mit dem jede Installation faehrt, die keinen eigenen hat:
    /// Reifen am Limit rumpeln, blockierende Raeder summen.
    /// </summary>
    /// <remarks>
    /// ## Woher die Werte kommen (2026-09-26)
    ///
    /// Aus dem Profil, das der Nutzer im Juni selbst abgestimmt hat
    /// (forza-haptics.fhgraph.json): Grip links/rechts auf die beiden Griffe, 20 Hz,
    /// eine Kurve, die erst unterhalb eines Drittels Grip anspricht und zum Limit hin
    /// steil wird. Das Blockieren kam aus seinem spaeteren Profil: ein Puls, 500 Hz,
    /// 30 Pulse je Sekunde -- vom Rumpeln deutlich zu unterscheiden.
    ///
    /// ## Null heisst Stille
    ///
    /// Die Telemetrie-Knoten stehen auf "Invert" (Griffverlust statt Grip), die Kurve
    /// steigt. Der alte Standard kehrte den Grip erst in der KURVE um -- und ohne
    /// frische Pakete liefert ein Telemetrie-Knoten 0, ohne Invert: Grip 0 hiess dann
    /// volle Staerke, zwischen 0,3 und 2 Sekunden nach dem letzten Paket.
    ///
    /// ## Kanaele, die es ueberall gibt
    ///
    /// 0 und 1 sind auf jedem Controller-Typ zwei getrennte Aktoren (Steam: rechter
    /// und linker Griff, DualSense und Xbox-Pads: der schwere und der leichte Motor),
    /// 2 ist beim Steam Controller das linke Pad und sonst beide Motoren. So bleibt
    /// der Graph sinnvoll, egal welcher Controller gewaehlt ist --
    /// BlueprintEditor.SetActiveController laesst gueltige Kanaele seitdem stehen.
    /// </remarks>
    public static SignalGraph CreateDefault()
    {
        var graph = new SignalGraph();
        var leftGrip = new TelemetrySignalNode(
            "Left wheels: grip loss",
            new Point(45, 60),
            "Derived.GripLeft",
            0,
            1)
        {
            Invert = true
        };
        var rightGrip = new TelemetrySignalNode(
            "Right wheels: grip loss",
            new Point(45, 200),
            "Derived.GripRight",
            0,
            1)
        {
            Invert = true
        };
        var leftCurve = new CurveSignalNode("At the limit L", new Point(330, 60));
        var rightCurve = new CurveSignalNode("At the limit R", new Point(330, 200));
        ToTheLimit(leftCurve.Curve);
        ToTheLimit(rightCurve.Curve);
        var leftOutput = new OutputSignalNode(
            "Left grip rumble",
            new Point(635, 60),
            SteamControllerHaptics.LeftGrip,
            HapticEffectMode.Rumble,
            20);
        var rightOutput = new OutputSignalNode(
            "Right grip rumble",
            new Point(635, 200),
            SteamControllerHaptics.RightGrip,
            HapticEffectMode.Rumble,
            20);

        var lockLeft = new TelemetrySignalNode("Left wheels locking", new Point(45, 360), "Derived.LockLeft", 0, 1);
        var lockRight = new TelemetrySignalNode("Right wheels locking", new Point(45, 470), "Derived.LockRight", 0, 1);
        var anyLock = new GroupSignalNode("Any wheel locking", new Point(330, 400), SignalGroupMode.Maximum);
        var lockOutput = new OutputSignalNode(
            "Brake lock buzz",
            new Point(635, 400),
            SteamControllerHaptics.LeftPad,
            HapticEffectMode.Beep,
            500)
        {
            BeepRateHz = 30,
            BeepDutyCycle = 0.5
        };

        graph.Nodes.AddRange([leftGrip, rightGrip, leftCurve, rightCurve, leftOutput, rightOutput,
                              lockLeft, lockRight, anyLock, lockOutput]);
        graph.Connect(leftGrip.Id, leftCurve.Id);
        graph.Connect(leftCurve.Id, leftOutput.Id);
        graph.Connect(rightGrip.Id, rightCurve.Id);
        graph.Connect(rightCurve.Id, rightOutput.Id);
        graph.Connect(lockLeft.Id, anyLock.Id);
        graph.Connect(lockRight.Id, anyLock.Id);
        graph.Connect(anyLock.Id, lockOutput.Id);
        return graph;
    }

    /// <summary>Still bis etwa 60 % Griffverlust, dann steil bis zum Limit.</summary>
    /// <remarks>
    /// Die Kurve des Nutzers aus dem Juni, an x = 0,5 gespiegelt: sie lief ueber dem
    /// Grip (1 -> 0), diese laeuft ueber dem Griffverlust (0 -> 1).
    /// </remarks>
    private static void ToTheLimit(BezierCurve curve)
    {
        curve.Nodes.Clear();
        curve.Nodes.Add(new BezierNode(new PointF(0f, 0f), new PointF(0f, 0f), new PointF(0.67f, 0f)));
        curve.Nodes.Add(new BezierNode(new PointF(1f, 1f), new PointF(0.87f, 0.112f), new PointF(1f, 1f)));
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
