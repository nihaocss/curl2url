using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

internal static class MakeIconFromImage
{
    private static void Main(string[] args)
    {
        if (args.Length < 2)
        {
            Console.WriteLine("usage: MakeIconFromImage.exe <input image> <output .ico>");
            return;
        }

        string inputPath = args[0];
        string outputPath = args[1];
        int[] sizes = new int[] { 16, 24, 32, 48, 64, 128, 256 };
        List<byte[]> frames = new List<byte[]>();

        using (Image source = Image.FromFile(inputPath))
        {
            foreach (int size in sizes)
            {
                using (Bitmap bmp = Resize(source, size))
                {
                    // 小尺寸用传统 DIB，兼容性最好；128/256 用 PNG 压缩
                    if (size <= 64)
                    {
                        frames.Add(ToDib(bmp, size));
                    }
                    else
                    {
                        using (MemoryStream ms = new MemoryStream())
                        {
                            bmp.Save(ms, ImageFormat.Png);
                            frames.Add(ms.ToArray());
                        }
                    }
                }
            }
        }

        using (FileStream fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
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
                bw.Write(frames[i].Length);
                bw.Write(offset);
                offset += frames[i].Length;
            }

            foreach (byte[] frame in frames) bw.Write(frame);
        }

        Console.WriteLine("icon written: " + outputPath);
    }

    private static byte[] ToDib(Bitmap bmp, int size)
    {
        int xorSize = size * size * 4;
        int maskRowBytes = ((size + 31) / 32) * 4;
        int maskSize = maskRowBytes * size;

        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            // BITMAPINFOHEADER：高度取两倍，包含 XOR 图像和 AND 掩码
            bw.Write(40);
            bw.Write(size);
            bw.Write(size * 2);
            bw.Write((short)1);
            bw.Write((short)32);
            bw.Write(0);
            bw.Write(xorSize + maskSize);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);
            bw.Write(0);

            // XOR 像素：BGRA，自下而上
            for (int y = size - 1; y >= 0; y--)
            {
                for (int x = 0; x < size; x++)
                {
                    Color c = bmp.GetPixel(x, y);
                    bw.Write(c.B);
                    bw.Write(c.G);
                    bw.Write(c.R);
                    bw.Write(c.A);
                }
            }

            // AND 掩码全 0，透明度由 alpha 通道决定
            bw.Write(new byte[maskSize]);
            return ms.ToArray();
        }
    }

    private static Bitmap Resize(Image source, int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.Clear(Color.Transparent);

            // 居中裁成正方形后再缩放
            int side = Math.Min(source.Width, source.Height);
            int sx = (source.Width - side) / 2;
            int sy = (source.Height - side) / 2;
            g.DrawImage(source, new Rectangle(0, 0, size, size), sx, sy, side, side, GraphicsUnit.Pixel);
        }
        return bmp;
    }
}
