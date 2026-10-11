using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Pegline
{
    /// Pegline's own icon, drawn in code: a single wooden peg gripping a line
    /// on a deep green tile. The build uses Write() to make the .exe icon;
    /// the tray draws it live so it stays sharp at any DPI.
    static class AppIcon
    {
        public static Bitmap Draw(int size)
        {
            var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float s = size;
                bool tiny = size <= 20;

                // The tile, left out at tray sizes where the peg alone reads better.
                if (!tiny)
                {
                    float m = s * 0.06f, r = s * 0.22f;
                    using (var path = Rounded(m, m, s - 2 * m, s - 2 * m, r))
                    using (var bg = new LinearGradientBrush(new PointF(0, m), new PointF(0, s - m),
                                                            Color.FromArgb(36, 122, 98), Color.FromArgb(16, 72, 62)))
                        g.FillPath(bg, path);
                }

                // The line, sagging a little.
                float y0 = s * (tiny ? 0.30f : 0.36f);
                using (var pen = new Pen(tiny ? Color.FromArgb(90, 90, 90) : Color.FromArgb(220, 236, 230), Math.Max(1f, s * 0.035f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    float x0 = tiny ? 0.5f : s * 0.14f, x1 = tiny ? s - 0.5f : s * 0.86f;
                    g.DrawBezier(pen, x0, y0, s * 0.4f, y0 + s * 0.08f, s * 0.6f, y0 + s * 0.08f, x1, y0);
                }

                // The peg: two wooden prongs and a steel spring.
                float pw = s * (tiny ? 0.30f : 0.22f), ph = s * (tiny ? 0.78f : 0.56f);
                float px = (s - pw) / 2, py = y0 - ph * 0.30f + s * 0.04f;
                using (var wood = new LinearGradientBrush(new PointF(px, 0), new PointF(px + pw, 0),
                                                          Color.FromArgb(242, 196, 120), Color.FromArgb(196, 132, 62)))
                using (var path = Rounded(px, py, pw, ph, pw * 0.42f))
                {
                    g.FillPath(wood, path);
                    using (var edge = new Pen(Color.FromArgb(120, 90, 50, 20), Math.Max(1f, s * 0.012f))) g.DrawPath(edge, path);
                }
                // The split between the two halves.
                using (var split = new Pen(Color.FromArgb(150, 110, 60, 20), Math.Max(1f, s * 0.018f)))
                    g.DrawLine(split, s / 2, py + ph * 0.48f, s / 2, py + ph * 0.96f);
                // The spring.
                float sw = pw * 1.18f, sh = s * (tiny ? 0.12f : 0.08f);
                using (var steel = new LinearGradientBrush(new PointF(0, py + ph * 0.36f), new PointF(0, py + ph * 0.36f + sh),
                                                           Color.FromArgb(235, 238, 240), Color.FromArgb(130, 136, 140)))
                using (var path = Rounded((s - sw) / 2, py + ph * 0.36f, sw, sh, sh / 2))
                    g.FillPath(steel, path);
            }
            return bmp;
        }

        static GraphicsPath Rounded(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = Math.Min(r * 2, Math.Min(w, h));
            p.AddArc(x, y, d, d, 180, 90);
            p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90);
            p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static Icon Tray(double scale)
        {
            int size = Math.Max(16, (int)Math.Round(16 * scale));
            using (var bmp = Draw(size)) return Icon.FromHandle(bmp.GetHicon());
        }

        /// Writes a multi-size .ico (PNG entries, Vista and later).
        public static void Write(string path)
        {
            int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
            var images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Draw(sizes[i]))
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    images[i] = ms.ToArray();
                }
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0); w.Write((byte)0);
                    w.Write((short)1); w.Write((short)32);
                    w.Write(images[i].Length); w.Write(offset);
                    offset += images[i].Length;
                }
                foreach (var img in images) w.Write(img);
            }
        }
    }
}
