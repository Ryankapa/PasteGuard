using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PasteGuard;

/// <summary>Draws the tray icons in code so the repo needs no binary image files.</summary>
internal static class IconFactory
{
    public enum Glyph { Check, Pause, Exclamation }

    public static Icon For(AgentState state) => state switch
    {
        AgentState.Protecting => Create(Color.FromArgb(22, 135, 75), Glyph.Check),
        AgentState.Paused => Create(Color.FromArgb(120, 120, 120), Glyph.Pause),
        _ => Create(Color.FromArgb(214, 150, 0), Glyph.Exclamation),
    };

    public static Icon Create(Color fill, Glyph glyph)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var shield = new GraphicsPath();
            shield.AddLine(16, 1.5f, 29, 6);
            shield.AddBezier(29, 6, 29.5f, 19, 24, 26, 16, 30.5f);
            shield.AddBezier(16, 30.5f, 8, 26, 2.5f, 19, 3, 6);
            shield.CloseFigure();

            using var brush = new SolidBrush(fill);
            g.FillPath(brush, shield);
            using var outline = new Pen(Color.FromArgb(200, 255, 255, 255), 1.5f);
            g.DrawPath(outline, shield);

            using var pen = new Pen(Color.White, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            switch (glyph)
            {
                case Glyph.Check:
                    g.DrawLines(pen, new PointF[] { new(9.5f, 16), new(14, 20.5f), new(22.5f, 11) });
                    break;
                case Glyph.Pause:
                    g.DrawLine(pen, 12.5f, 10, 12.5f, 21);
                    g.DrawLine(pen, 19.5f, 10, 19.5f, 21);
                    break;
                case Glyph.Exclamation:
                    g.DrawLine(pen, 16, 8.5f, 16, 17.5f);
                    g.FillEllipse(Brushes.White, 14, 20.5f, 4, 4);
                    break;
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
