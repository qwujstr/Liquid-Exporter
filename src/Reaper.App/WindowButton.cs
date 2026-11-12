namespace Reaper.App;

internal sealed class WindowButton : Button
{
    private readonly System.Windows.Forms.Timer animation = new() { Interval = 16 };
    private float brightness;
    private bool hovered;
    private readonly string symbol;

    public WindowButton(string symbol)
    {
        this.symbol = symbol;
        Text = symbol;
        AccessibleName = symbol == "×" ? "Close" : symbol == "□" ? "Maximize or restore" : "Minimize";
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        animation.Tick += (_, _) =>
        {
            float target = hovered ? 30 : 0;
            brightness += (target - brightness) * .22f;
            if (Math.Abs(target - brightness) < .5f) { brightness = target; animation.Stop(); }
            Invalidate();
        };
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hovered = true; animation.Start(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hovered = false; animation.Start(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        int level = (int)brightness;
        e.Graphics.Clear(Color.FromArgb(level, level, level));
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        float scale = DeviceDpi / 96f, radius = 6 * scale;
        using var pen = new Pen(Color.Silver, 1.5f * scale);
        float x = Width / 2f, y = Height / 2f;
        if (symbol == "×") { e.Graphics.DrawLine(pen, x - radius, y - radius, x + radius, y + radius); e.Graphics.DrawLine(pen, x + radius, y - radius, x - radius, y + radius); }
        else if (symbol == "□") e.Graphics.DrawRectangle(pen, x - radius, y - radius, radius * 2, radius * 2);
        else e.Graphics.DrawLine(pen, x - radius, y, x + radius, y);
    }
    protected override void Dispose(bool disposing) { if (disposing) animation.Dispose(); base.Dispose(disposing); }
}
