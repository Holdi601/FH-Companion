namespace ForzaHaptics.Tester;

internal sealed class BezierNode
{
    public BezierNode(PointF position, PointF inHandle, PointF outHandle)
    {
        Position = position;
        InHandle = inHandle;
        OutHandle = outHandle;
    }

    public PointF Position { get; set; }

    public PointF InHandle { get; set; }

    public PointF OutHandle { get; set; }
}

internal sealed class BezierCurve
{
    public List<BezierNode> Nodes { get; } = [];

    public BezierCurve()
    {
        ResetInverted();
    }

    public double Evaluate(double input)
    {
        input = Math.Clamp(input, 0, 1);
        if (Nodes.Count < 2)
        {
            return 0;
        }

        var segment = Nodes.Count - 2;
        for (var index = 0; index < Nodes.Count - 1; index++)
        {
            if (input <= Nodes[index + 1].Position.X)
            {
                segment = index;
                break;
            }
        }

        var start = Nodes[segment];
        var end = Nodes[segment + 1];
        var low = 0.0;
        var high = 1.0;

        for (var iteration = 0; iteration < 24; iteration++)
        {
            var middle = (low + high) / 2.0;
            if (Cubic(
                    start.Position.X,
                    start.OutHandle.X,
                    end.InHandle.X,
                    end.Position.X,
                    middle) < input)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        return Math.Clamp(
            Cubic(
                start.Position.Y,
                start.OutHandle.Y,
                end.InHandle.Y,
                end.Position.Y,
                (low + high) / 2.0),
            0,
            1);
    }

    public int AddNode(PointF position)
    {
        position = new PointF(
            Math.Clamp(position.X, 0.01f, 0.99f),
            Math.Clamp(position.Y, 0, 1));

        var insertAt = Nodes.FindIndex(node => node.Position.X > position.X);
        if (insertAt <= 0)
        {
            insertAt = Nodes.Count - 1;
        }

        var previous = Nodes[insertAt - 1];
        var next = Nodes[insertAt];
        var spacing = MathF.Min(
            position.X - previous.Position.X,
            next.Position.X - position.X) / 3f;
        var slope = (next.Position.Y - previous.Position.Y) /
                    MathF.Max(0.001f, next.Position.X - previous.Position.X);

        var inHandle = new PointF(
            position.X - spacing,
            Math.Clamp(position.Y - slope * spacing, 0, 1));
        var outHandle = new PointF(
            position.X + spacing,
            Math.Clamp(position.Y + slope * spacing, 0, 1));

        Nodes.Insert(insertAt, new BezierNode(position, inHandle, outHandle));
        previous.OutHandle = new PointF(
            Math.Min(previous.OutHandle.X, position.X),
            previous.OutHandle.Y);
        next.InHandle = new PointF(
            Math.Max(next.InHandle.X, position.X),
            next.InHandle.Y);
        return insertAt;
    }

    public bool RemoveNode(int index)
    {
        if (index <= 0 || index >= Nodes.Count - 1)
        {
            return false;
        }

        Nodes.RemoveAt(index);
        return true;
    }

    public void ResetInverted()
    {
        Nodes.Clear();
        Nodes.Add(new BezierNode(
            new PointF(0f, 1f),
            new PointF(0f, 1f),
            new PointF(0.15f, 1f)));
        Nodes.Add(new BezierNode(
            new PointF(1f, 0f),
            new PointF(0.65f, 0f),
            new PointF(1f, 0f)));
    }

    public void ResetLinear()
    {
        Nodes.Clear();
        Nodes.Add(new BezierNode(
            new PointF(0f, 0f),
            new PointF(0f, 0f),
            new PointF(0.33f, 0.33f)));
        Nodes.Add(new BezierNode(
            new PointF(1f, 1f),
            new PointF(0.67f, 0.67f),
            new PointF(1f, 1f)));
    }

    private static double Cubic(double p0, double p1, double p2, double p3, double t)
    {
        var inverse = 1.0 - t;
        return inverse * inverse * inverse * p0 +
               3 * inverse * inverse * t * p1 +
               3 * inverse * t * t * p2 +
               t * t * t * p3;
    }
}
