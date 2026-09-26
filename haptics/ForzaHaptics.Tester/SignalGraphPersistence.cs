using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForzaHaptics.Tester;

internal static class SignalGraphPersistence
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(SignalGraph graph, string path)
    {
        var dto = new GraphDto
        {
            Enabled = graph.Enabled,
            Nodes = graph.Nodes.Select(ToDto).ToList(),
            Connections = graph.Connections
                .Select(connection => new ConnectionDto
                {
                    From = connection.FromNodeId,
                    To = connection.ToNodeId
                })
                .ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(dto, Options));
    }

    public static void LoadInto(SignalGraph graph, string path)
    {
        var dto = JsonSerializer.Deserialize<GraphDto>(File.ReadAllText(path), Options)
                  ?? throw new InvalidDataException("The graph profile is empty.");
        var idMap = new Dictionary<Guid, Guid>();
        var nodes = new List<SignalNode>();

        foreach (var nodeDto in dto.Nodes)
        {
            var node = FromDto(nodeDto);
            idMap[nodeDto.Id] = node.Id;
            nodes.Add(node);
        }

        graph.Nodes.Clear();
        graph.Connections.Clear();
        graph.Nodes.AddRange(nodes);
        // Loading a profile never starts hardware output automatically.
        graph.Enabled = false;
        foreach (var connection in dto.Connections)
        {
            if (idMap.TryGetValue(connection.From, out var from) &&
                idMap.TryGetValue(connection.To, out var to))
            {
                graph.Connect(from, to);
            }
        }
    }

    private static NodeDto ToDto(SignalNode node)
    {
        var dto = new NodeDto
        {
            Id = node.Id,
            Type = node.Type,
            Title = node.Title,
            X = node.Location.X,
            Y = node.Location.Y
        };

        switch (node)
        {
            case ConstantSignalNode constant:
                dto.ConstantValue = constant.Value;
                dto.ConstantActive = constant.Active;
                break;
            case TelemetrySignalNode telemetry:
                dto.TelemetryKey = telemetry.TelemetryKey;
                dto.Minimum = telemetry.Minimum;
                dto.Maximum = telemetry.Maximum;
                dto.Absolute = telemetry.Absolute;
                dto.Invert = telemetry.Invert;
                break;
            case GroupSignalNode group:
                dto.GroupMode = group.Mode;
                break;
            case CurveSignalNode curve:
                dto.CurveNodes = curve.Curve.Nodes.Select(curveNode => new CurveNodeDto
                {
                    X = curveNode.Position.X,
                    Y = curveNode.Position.Y,
                    InX = curveNode.InHandle.X,
                    InY = curveNode.InHandle.Y,
                    OutX = curveNode.OutHandle.X,
                    OutY = curveNode.OutHandle.Y
                }).ToList();
                break;
            case OutputSignalNode output:
                dto.TargetId = output.TargetId;
                dto.Channel = output.Channel;
                dto.EffectMode = output.EffectMode;
                dto.InputMode = output.InputMode;
                dto.ModulationMode = output.ModulationMode;
                dto.MinimumFrequencyHz = output.MinimumFrequencyHz;
                dto.MaximumFrequencyHz = output.MaximumFrequencyHz;
                dto.MinimumStrength = output.MinimumStrength;
                dto.MaximumStrength = output.MaximumStrength;
                dto.BeepRateHz = output.BeepRateHz;
                dto.BeepDutyCycle = output.BeepDutyCycle;
                dto.SilenceAtZero = output.SilenceAtZero;
                dto.DualSenseTriggerEffect = output.DualSenseTriggerEffect;
                dto.TriggerStartPosition = output.TriggerStartPosition;
                dto.TriggerEndPosition = output.TriggerEndPosition;
                dto.TriggerSecondaryStrength = output.TriggerSecondaryStrength;
                dto.TriggerSnapStrength = output.TriggerSnapStrength;
                break;
        }

        return dto;
    }

    private static SignalNode FromDto(NodeDto dto)
    {
        var location = new Point(dto.X, dto.Y);
        return dto.Type switch
        {
            SignalNodeType.Constant => new ConstantSignalNode(
                dto.Title,
                location,
                dto.ConstantValue)
            {
                Active = dto.ConstantActive
            },
            SignalNodeType.Telemetry => new TelemetrySignalNode(
                dto.Title,
                location,
                dto.TelemetryKey ?? "Derived.GripLeft",
                dto.Minimum,
                dto.Maximum)
            {
                Absolute = dto.Absolute,
                Invert = dto.Invert
            },
            SignalNodeType.Group => new GroupSignalNode(dto.Title, location, dto.GroupMode),
            SignalNodeType.Curve => CreateCurve(dto, location),
            SignalNodeType.Output => new OutputSignalNode(
                dto.Title,
                location,
                dto.Channel,
                dto.EffectMode,
                dto.MinimumFrequencyHz ?? dto.FrequencyHz)
            {
                TargetId = dto.TargetId,
                InputMode = dto.InputMode,
                ModulationMode = dto.ModulationMode,
                MinimumFrequencyHz = dto.MinimumFrequencyHz ?? dto.FrequencyHz,
                MaximumFrequencyHz = dto.MaximumFrequencyHz ?? dto.FrequencyHz,
                MinimumStrength = dto.MinimumStrength ?? 0,
                MaximumStrength = dto.MaximumStrength,
                BeepRateHz = dto.BeepRateHz,
                BeepDutyCycle = dto.BeepDutyCycle,
                SilenceAtZero = dto.SilenceAtZero,
                DualSenseTriggerEffect = dto.DualSenseTriggerEffect,
                TriggerStartPosition = dto.TriggerStartPosition,
                TriggerEndPosition = dto.TriggerEndPosition,
                TriggerSecondaryStrength = dto.TriggerSecondaryStrength,
                TriggerSnapStrength = dto.TriggerSnapStrength
            },
            _ => throw new InvalidDataException($"Unknown node type: {dto.Type}")
        };
    }

    private static CurveSignalNode CreateCurve(NodeDto dto, Point location)
    {
        var curve = new CurveSignalNode(dto.Title, location);
        if (dto.CurveNodes.Count >= 2)
        {
            curve.Curve.Nodes.Clear();
            foreach (var node in dto.CurveNodes)
            {
                curve.Curve.Nodes.Add(new BezierNode(
                    new PointF(node.X, node.Y),
                    new PointF(node.InX, node.InY),
                    new PointF(node.OutX, node.OutY)));
            }
        }

        return curve;
    }

    private sealed class GraphDto
    {
        public bool Enabled { get; set; }
        public List<NodeDto> Nodes { get; set; } = [];
        public List<ConnectionDto> Connections { get; set; } = [];
    }

    private sealed class NodeDto
    {
        public Guid Id { get; set; }
        public SignalNodeType Type { get; set; }
        public string Title { get; set; } = "";
        public int X { get; set; }
        public int Y { get; set; }
        public string? TelemetryKey { get; set; }
        public double ConstantValue { get; set; } = 0.25;
        public bool ConstantActive { get; set; } = true;
        public double Minimum { get; set; }
        public double Maximum { get; set; } = 1;
        public bool Absolute { get; set; }
        public bool Invert { get; set; }
        public SignalGroupMode GroupMode { get; set; }
        public List<CurveNodeDto> CurveNodes { get; set; } = [];
        public int Channel { get; set; }
        public string TargetId { get; set; } = OutputSignalNode.SteamNativeTargetId;
        public HapticEffectMode EffectMode { get; set; }
        public SignalGroupMode InputMode { get; set; } = SignalGroupMode.Maximum;
        public HapticModulationMode ModulationMode { get; set; } =
            HapticModulationMode.StrengthOnly;
        // Kept for profiles written before endpoint modulation was added.
        public double FrequencyHz { get; set; } = 70;
        public double? MinimumFrequencyHz { get; set; }
        public double? MaximumFrequencyHz { get; set; }
        public double? MinimumStrength { get; set; }
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

    private sealed class CurveNodeDto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float InX { get; set; }
        public float InY { get; set; }
        public float OutX { get; set; }
        public float OutY { get; set; }
    }

    private sealed class ConnectionDto
    {
        public Guid From { get; set; }
        public Guid To { get; set; }
    }
}
