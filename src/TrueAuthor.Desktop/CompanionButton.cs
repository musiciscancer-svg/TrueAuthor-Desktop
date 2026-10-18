using System.Drawing.Drawing2D;
namespace TrueAuthor.Desktop;
sealed class CompanionButton : Button
{
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.Clear(Parent?.BackColor ?? Color.White);
        var rectangle = new Rectangle(1, 3, Width - 3, Height - 6);
        using var path = new GraphicsPath(); const int radius = 16;
        path.AddArc(rectangle.Left, rectangle.Top, radius, radius, 180, 90);
        path.AddArc(rectangle.Right - radius, rectangle.Top, radius, radius, 270, 90);
        path.AddArc(rectangle.Right - radius, rectangle.Bottom - radius, radius, radius, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - radius, radius, radius, 90, 90); path.CloseFigure();
        var hover = Enabled && ClientRectangle.Contains(PointToClient(Cursor.Position));
        using var fill = new SolidBrush(hover ? ControlPaint.Light(BackColor, .08f) : BackColor);
        using var border = new Pen(FlatAppearance.BorderColor);
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, rectangle, Enabled ? ForeColor : Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(rectangle, -5, -5));
    }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }
}
