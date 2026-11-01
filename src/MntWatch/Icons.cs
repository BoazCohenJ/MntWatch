using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace MntWatch;

/// <summary>Icons drawn at runtime: a folder sending a signal, on a square colored by the current state.</summary>
internal static class Icons
{
    public static readonly Color Active = Color.FromArgb(0x16, 0x7f, 0x6e);
    public static readonly Color Waiting = Color.FromArgb(0x89, 0x87, 0x81);
    public static readonly Color Problem = Color.FromArgb(0xd9, 0x7a, 0x06);

    public static Bitmap Draw(Color background, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size;
        using (var path = RoundedRect(new RectangleF(0.5f, 0.5f, s - 1, s - 1), s * 0.22f))
        using (var brush = new SolidBrush(background))
            g.FillPath(brush, path);

        // Folder: a tab and a body.
        using (var folder = new GraphicsPath())
        {
            float x = 0.13f * s, y = 0.30f * s, w = 0.52f * s, h = 0.42f * s;
            folder.AddLines(new[]
            {
                new PointF(x, y), new PointF(x + w * 0.38f, y), new PointF(x + w * 0.48f, y + h * 0.14f),
                new PointF(x + w, y + h * 0.14f), new PointF(x + w, y + h), new PointF(x, y + h),
            });
            folder.CloseFigure();
            g.FillPath(Brushes.White, folder);
        }

        // Signal: two arcs to the right of the folder, as if it's calling out a change.
        float width = Math.Max(1.3f, s * 0.075f);
        using var pen = new Pen(Color.White, width) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        var center = new PointF(0.60f * s, 0.51f * s);
        foreach (float r in new[] { 0.15f * s, 0.27f * s })
            g.DrawArc(pen, center.X - r, center.Y - r, 2 * r, 2 * r, -45, 90);
        return bmp;
    }

    public static Icon Make(Color background, int size)
    {
        using var bmp = Draw(background, size);
        IntPtr handle = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(handle).Clone();
        Native.DestroyIcon(handle);
        return icon;
    }

    public static Icon AppIcon(Size size)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MntWatch.app.ico");
        return stream != null ? new Icon(stream, size) : SystemIcons.Application;
    }

    /// <summary>Writes app.ico (16-256 px, PNG-compressed entries) and PNGs for the website. Run once when the design changes.</summary>
    public static void Export(string folder)
    {
        Directory.CreateDirectory(folder);
        var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
        var pngs = new List<byte[]>();
        foreach (int size in sizes)
        {
            using var bmp = Draw(Active, size);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }
        using (var ico = new BinaryWriter(File.Create(Path.Combine(folder, "app.ico"))))
        {
            ico.Write((short)0);
            ico.Write((short)1);
            ico.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                ico.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                ico.Write((byte)0);
                ico.Write((byte)0);
                ico.Write((short)1);
                ico.Write((short)32);
                ico.Write(pngs[i].Length);
                ico.Write(offset);
                offset += pngs[i].Length;
            }
            foreach (var png in pngs) ico.Write(png);
        }
        foreach (int size in new[] { 64, 512 })
        {
            using var bmp = Draw(Active, size);
            bmp.Save(Path.Combine(folder, $"icon-{size}.png"), ImageFormat.Png);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
