using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace MotorBench.App.Controls;

/// <summary>
/// Scrolling line chart for the last N samples. Every series gets one value
/// per Push, so they all share one ring buffer index.
/// </summary>
internal sealed class TrendChart : Control
{
    private const int Capacity = 600; // 60 s at the 100 ms poll rate

    private readonly List<(string Name, Color Color, double[] Values)> _series = [];
    private int _count;
    private int _head;

    public TrendChart()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        BackColor = Color.White;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title { get; set; } = "";

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double YMin { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double YMax { get; set; } = 100;

    public void AddSeries(string name, Color color)
    {
        _series.Add((name, color, new double[Capacity]));
        Invalidate();
    }

    public void Push(params double[] values)
    {
        for (int i = 0; i < _series.Count && i < values.Length; i++)
            _series[i].Values[_head] = values[i];

        _head = (_head + 1) % Capacity;
        _count = Math.Min(_count + 1, Capacity);
        Invalidate();
    }

    public void Clear()
    {
        _count = 0;
        _head = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);

        var plot = new Rectangle(52, 24, Width - 62, Height - 32);
        if (plot.Width < 20 || plot.Height < 20)
            return;

        DrawLegend(g);
        DrawGrid(g, plot);

        if (_count < 2 || YMax <= YMin)
            return;

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.SetClip(plot);

        float dx = plot.Width / (float)(Capacity - 1);
        int first = (_head - _count + Capacity) % Capacity;
        var points = new PointF[_count];

        foreach (var (_, color, values) in _series)
        {
            for (int i = 0; i < _count; i++)
            {
                double v = Math.Clamp(values[(first + i) % Capacity], YMin, YMax);
                float x = plot.Right - (_count - 1 - i) * dx;
                float y = (float)(plot.Bottom - (v - YMin) / (YMax - YMin) * plot.Height);
                points[i] = new PointF(x, y);
            }

            using var pen = new Pen(color, 1.6f);
            g.DrawLines(pen, points);
        }

        g.ResetClip();
    }

    private void DrawLegend(Graphics g)
    {
        TextRenderer.DrawText(g, Title, Font, new Point(4, 4), ForeColor);
        int x = 8 + TextRenderer.MeasureText(Title, Font).Width + 12;

        foreach (var (name, color, _) in _series)
        {
            using (var pen = new Pen(color, 3f))
                g.DrawLine(pen, x, 12, x + 16, 12);
            x += 20;
            TextRenderer.DrawText(g, name, Font, new Point(x, 4), ForeColor);
            x += TextRenderer.MeasureText(name, Font).Width + 12;
        }
    }

    private void DrawGrid(Graphics g, Rectangle plot)
    {
        const int lines = 4;
        using var gridPen = new Pen(Color.Gainsboro);

        for (int i = 0; i <= lines; i++)
        {
            int y = plot.Bottom - plot.Height * i / lines;
            g.DrawLine(gridPen, plot.Left, y, plot.Right, y);

            double value = YMin + (YMax - YMin) * i / lines;
            var labelRect = new Rectangle(0, y - 9, plot.Left - 6, 18);
            TextRenderer.DrawText(g, value.ToString("0", CultureInfo.InvariantCulture), Font, labelRect, ForeColor,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }

        g.DrawRectangle(Pens.Gray, plot);
    }
}
