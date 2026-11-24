using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace MotorBench.App.Controls;

/// <summary>Round indicator with a caption, like a pilot lamp on a panel.</summary>
internal sealed class Lamp : Control
{
    private bool _on;

    public Lamp(string text, Color onColor)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Text = text;
        OnColor = onColor;
        Size = new Size(76, 24);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color OnColor { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => _on;
        set
        {
            if (_on == value)
                return;
            _on = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int d = Math.Min(Height - 6, 14);
        var circle = new Rectangle(2, (Height - d) / 2, d, d);
        using (var brush = new SolidBrush(_on ? OnColor : Color.FromArgb(80, 80, 80)))
            g.FillEllipse(brush, circle);
        g.DrawEllipse(Pens.DimGray, circle);

        var textRect = new Rectangle(circle.Right + 4, 0, Width - circle.Right - 4, Height);
        TextRenderer.DrawText(g, Text, Font, textRect, ForeColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
    }
}
