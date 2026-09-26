using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace ForzaHaptics.Tester;

internal sealed class BezierCurveEditor : Control
{
    private const float AnchorRadius = 7f;
    private const float HandleRadius = 6f;
    private readonly RectangleF _plot = new(50, 18, 430, 205);
    private HitTarget _dragTarget = HitTarget.None;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public BezierCurve Curve { get; private set; } = new();

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedNodeIndex { get; private set; } = -1;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string HorizontalCaption { get; set; } = "Grip";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string VerticalCaption { get; set; } = "Output";

    public event EventHandler? CurveChanged;

    public BezierCurveEditor()
    {
        DoubleBuffered = true;
        Size = new Size(505, 255);
        BackColor = Color.FromArgb(20, 23, 28);
        ForeColor = Color.WhiteSmoke;
        Cursor = Cursors.Cross;
    }

    public void BindCurve(BezierCurve curve)
    {
        Curve = curve;
        SelectedNodeIndex = -1;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using var gridPen = new Pen(Color.FromArgb(52, 57, 66), 1);
        for (var division = 0; division <= 4; division++)
        {
            var x = _plot.Left + _plot.Width * division / 4f;
            var y = _plot.Top + _plot.Height * division / 4f;
            graphics.DrawLine(gridPen, x, _plot.Top, x, _plot.Bottom);
            graphics.DrawLine(gridPen, _plot.Left, y, _plot.Right, y);
        }

        using var axisPen = new Pen(Color.FromArgb(125, 132, 145), 1.5f);
        graphics.DrawRectangle(axisPen, _plot.X, _plot.Y, _plot.Width, _plot.Height);

        using var handlePen = new Pen(Color.FromArgb(110, 120, 140), 1.5f)
        {
            DashStyle = DashStyle.Dash
        };
        using var curvePen = new Pen(Color.FromArgb(73, 137, 255), 4f);

        for (var index = 0; index < Curve.Nodes.Count - 1; index++)
        {
            var start = Curve.Nodes[index];
            var end = Curve.Nodes[index + 1];
            var startPoint = ToScreen(start.Position);
            var startHandle = ToScreen(start.OutHandle);
            var endHandle = ToScreen(end.InHandle);
            var endPoint = ToScreen(end.Position);

            graphics.DrawLine(handlePen, startPoint, startHandle);
            graphics.DrawLine(handlePen, endPoint, endHandle);

            using var path = new GraphicsPath();
            path.AddBezier(startPoint, startHandle, endHandle, endPoint);
            graphics.DrawPath(curvePen, path);
        }

        for (var index = 0; index < Curve.Nodes.Count; index++)
        {
            var node = Curve.Nodes[index];
            if (index > 0)
            {
                DrawPoint(graphics, ToScreen(node.InHandle), HandleRadius, Color.FromArgb(150, 166, 196));
            }

            if (index < Curve.Nodes.Count - 1)
            {
                DrawPoint(graphics, ToScreen(node.OutHandle), HandleRadius, Color.FromArgb(150, 166, 196));
            }
        }

        for (var index = 0; index < Curve.Nodes.Count; index++)
        {
            var color = index == SelectedNodeIndex
                ? Color.FromArgb(255, 116, 84)
                : Color.FromArgb(242, 185, 80);
            DrawPoint(graphics, ToScreen(Curve.Nodes[index].Position), AnchorRadius, color);
        }

        using var textBrush = new SolidBrush(Color.FromArgb(180, 185, 194));
        graphics.DrawString("0%", Font, textBrush, _plot.Left - 9, _plot.Bottom + 5);
        graphics.DrawString("100%", Font, textBrush, _plot.Right - 30, _plot.Bottom + 5);
        graphics.DrawString(HorizontalCaption, Font, textBrush, _plot.Left + _plot.Width / 2 - 14, _plot.Bottom + 5);
        graphics.DrawString(VerticalCaption, Font, textBrush, 3, _plot.Top + _plot.Height / 2 - 8);
        graphics.DrawString("100%", Font, textBrush, 12, _plot.Top - 7);
        graphics.DrawString("0%", Font, textBrush, 22, _plot.Bottom - 10);
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left || !_plot.Contains(e.Location))
        {
            return;
        }

        SelectedNodeIndex = Curve.AddNode(ToNormalized(e.Location));
        NotifyChanged();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        var hit = HitTest(e.Location);

        if (e.Button == MouseButtons.Right &&
            hit.Kind == HitKind.Anchor &&
            Curve.RemoveNode(hit.NodeIndex))
        {
            SelectedNodeIndex = -1;
            NotifyChanged();
            return;
        }

        if (e.Button != MouseButtons.Left || hit.Kind == HitKind.None)
        {
            return;
        }

        _dragTarget = hit;
        SelectedNodeIndex = hit.NodeIndex;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragTarget.Kind == HitKind.None)
        {
            return;
        }

        var node = Curve.Nodes[_dragTarget.NodeIndex];
        var normalized = ToNormalized(e.Location);

        switch (_dragTarget.Kind)
        {
            case HitKind.Anchor:
                MoveAnchor(_dragTarget.NodeIndex, normalized);
                break;
            case HitKind.InHandle:
                node.InHandle = new PointF(
                    Math.Clamp(
                        normalized.X,
                        Curve.Nodes[_dragTarget.NodeIndex - 1].Position.X,
                        node.Position.X),
                    normalized.Y);
                break;
            case HitKind.OutHandle:
                node.OutHandle = new PointF(
                    Math.Clamp(
                        normalized.X,
                        node.Position.X,
                        Curve.Nodes[_dragTarget.NodeIndex + 1].Position.X),
                    normalized.Y);
                break;
        }

        NotifyChanged();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragTarget = HitTarget.None;
        Capture = false;
    }

    public void AddPoint()
    {
        var widestSegment = 0;
        var widestDistance = 0f;
        for (var index = 0; index < Curve.Nodes.Count - 1; index++)
        {
            var distance = Curve.Nodes[index + 1].Position.X - Curve.Nodes[index].Position.X;
            if (distance > widestDistance)
            {
                widestDistance = distance;
                widestSegment = index;
            }
        }

        var x = (Curve.Nodes[widestSegment].Position.X +
                 Curve.Nodes[widestSegment + 1].Position.X) / 2f;
        SelectedNodeIndex = Curve.AddNode(new PointF(x, (float)Curve.Evaluate(x)));
        NotifyChanged();
    }

    public void RemoveSelectedPoint()
    {
        if (Curve.RemoveNode(SelectedNodeIndex))
        {
            SelectedNodeIndex = -1;
            NotifyChanged();
        }
    }

    public void ResetInverted()
    {
        Curve.ResetInverted();
        SelectedNodeIndex = -1;
        NotifyChanged();
    }

    public void ResetLinear()
    {
        Curve.ResetLinear();
        SelectedNodeIndex = -1;
        NotifyChanged();
    }

    private void MoveAnchor(int index, PointF position)
    {
        var node = Curve.Nodes[index];
        var old = node.Position;
        float x;

        if (index == 0)
        {
            x = 0;
        }
        else if (index == Curve.Nodes.Count - 1)
        {
            x = 1;
        }
        else
        {
            x = Math.Clamp(
                position.X,
                Curve.Nodes[index - 1].Position.X + 0.01f,
                Curve.Nodes[index + 1].Position.X - 0.01f);
        }

        var next = new PointF(x, position.Y);
        var delta = new PointF(next.X - old.X, next.Y - old.Y);
        node.Position = next;
        node.InHandle = ClampPoint(
            new PointF(node.InHandle.X + delta.X, node.InHandle.Y + delta.Y));
        node.OutHandle = ClampPoint(
            new PointF(node.OutHandle.X + delta.X, node.OutHandle.Y + delta.Y));

        if (index > 0)
        {
            var previous = Curve.Nodes[index - 1];
            previous.OutHandle = new PointF(
                Math.Min(previous.OutHandle.X, node.Position.X),
                previous.OutHandle.Y);
            node.InHandle = new PointF(
                Math.Clamp(node.InHandle.X, previous.Position.X, node.Position.X),
                node.InHandle.Y);
        }

        if (index < Curve.Nodes.Count - 1)
        {
            var following = Curve.Nodes[index + 1];
            following.InHandle = new PointF(
                Math.Max(following.InHandle.X, node.Position.X),
                following.InHandle.Y);
            node.OutHandle = new PointF(
                Math.Clamp(node.OutHandle.X, node.Position.X, following.Position.X),
                node.OutHandle.Y);
        }
    }

    private HitTarget HitTest(Point point)
    {
        for (var index = 0; index < Curve.Nodes.Count; index++)
        {
            if (Distance(ToScreen(Curve.Nodes[index].Position), point) <= AnchorRadius * 2)
            {
                return new HitTarget(HitKind.Anchor, index);
            }
        }

        for (var index = 0; index < Curve.Nodes.Count; index++)
        {
            var node = Curve.Nodes[index];
            if (index > 0 &&
                Distance(ToScreen(node.InHandle), point) <= HandleRadius * 2)
            {
                return new HitTarget(HitKind.InHandle, index);
            }

            if (index < Curve.Nodes.Count - 1 &&
                Distance(ToScreen(node.OutHandle), point) <= HandleRadius * 2)
            {
                return new HitTarget(HitKind.OutHandle, index);
            }
        }

        return HitTarget.None;
    }

    private void NotifyChanged()
    {
        Invalidate();
        CurveChanged?.Invoke(this, EventArgs.Empty);
    }

    private PointF ToScreen(PointF normalized) =>
        new(
            _plot.Left + normalized.X * _plot.Width,
            _plot.Bottom - normalized.Y * _plot.Height);

    private PointF ToNormalized(Point point) =>
        new(
            Math.Clamp((point.X - _plot.Left) / _plot.Width, 0, 1),
            Math.Clamp((_plot.Bottom - point.Y) / _plot.Height, 0, 1));

    private static PointF ClampPoint(PointF point) =>
        new(Math.Clamp(point.X, 0, 1), Math.Clamp(point.Y, 0, 1));

    private static float Distance(PointF first, PointF second)
    {
        var x = first.X - second.X;
        var y = first.Y - second.Y;
        return MathF.Sqrt(x * x + y * y);
    }

    private static void DrawPoint(Graphics graphics, PointF point, float radius, Color color)
    {
        using var brush = new SolidBrush(color);
        using var outline = new Pen(Color.FromArgb(20, 23, 28), 2);
        var rectangle = new RectangleF(
            point.X - radius,
            point.Y - radius,
            radius * 2,
            radius * 2);
        graphics.FillEllipse(brush, rectangle);
        graphics.DrawEllipse(outline, rectangle);
    }

    private enum HitKind
    {
        None,
        Anchor,
        InHandle,
        OutHandle
    }

    private readonly record struct HitTarget(HitKind Kind, int NodeIndex)
    {
        public static HitTarget None => new(HitKind.None, -1);
    }
}
