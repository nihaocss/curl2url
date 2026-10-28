using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIcon
{
    private static void Main(string[] args)
    {
        string outPath = args.Length > 0 ? args[0] : "curl2url.ico";
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> pngs = new List<byte[]>();

        foreach (int size in sizes)
        {
            using (Bitmap bmp = Render(size))
            using (MemoryStream ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                pngs.Add(ms.ToArray());
            }
        }

        using (FileStream fs = new FileStream(outPath, FileMode.Create, FileAccess.Write))
        using (BinaryWriter bw = new BinaryWriter(fs))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int size = sizes[i];
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)(size >= 256 ? 0 : size));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(pngs[i].Length);
                bw.Write(offset);
                offset += pngs[i].Length;
            }

            foreach (byte[] png in pngs) bw.Write(png);
        }

        Console.WriteLine("icon written: " + outPath);
    }

    private static Bitmap Render(int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Color.Transparent);

            float pad = size * 0.04f;
            RectangleF rect = new RectangleF(pad, pad, size - pad * 2, size - pad * 2);
            float radius = size * 0.22f;

            using (GraphicsPath path = RoundedRect(rect, radius))
            using (LinearGradientBrush brush = new LinearGradientBrush(
                rect,
                Color.FromArgb(64, 158, 255),
                Color.FromArgb(31, 92, 163),
                45f))
            {
                g.FillPath(brush, path);
            }

            float cy = size * 0.5f;
            float thickness = Math.Max(2f, size * 0.105f);

            using (Pen pen = new Pen(Color.White, thickness))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawLine(pen, size * 0.20f, cy, size * 0.60f, cy);
            }

            PointF[] arrowHead = new PointF[]
            {
                new PointF(size * 0.56f, cy - size * 0.185f),
                new PointF(size * 0.78f, cy),
                new PointF(size * 0.56f, cy + size * 0.185f)
            };
            g.FillPolygon(Brushes.White, arrowHead);
        }
        return bmp;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        float diameter = radius * 2;
        GraphicsPath path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
