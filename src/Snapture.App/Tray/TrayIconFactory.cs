using System.Drawing;
using System.Drawing.Drawing2D;
using Drawing = System.Drawing;

namespace Snapture.App.Tray;

/// <summary>
/// Generates the tray icons at runtime so the app ships without binary assets.
/// Idle = hollow red ring; recording = solid red dot.
/// </summary>
internal static class TrayIconFactory
{
    public static Icon CreateIdle() => Create(recording: false);
    public static Icon CreateRecording() => Create(recording: true);

    private static Icon Create(bool recording)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Drawing.Color.Transparent);

            var accent = Drawing.Color.FromArgb(0xE2, 0x3B, 0x3B);
            var rect = new RectangleF(5, 5, size - 10, size - 10);

            if (recording)
            {
                using var fill = new SolidBrush(accent);
                g.FillEllipse(fill, rect);
            }
            else
            {
                using var pen = new Pen(accent, 4f);
                g.DrawEllipse(pen, rect);
            }
        }

        var hicon = bmp.GetHicon();
        // Clone so the icon owns its own handle independent of the bitmap.
        using var temp = Icon.FromHandle(hicon);
        return (Icon)temp.Clone();
    }
}
