using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;

namespace Pegline
{
    /// Pegline's own icon, drawn in code: a photo hanging from a line by a
    /// small metal clip, tilted a little, on a deep green tile. It is what
    /// the app shows on screen. The build uses Write() to make the .exe icon;
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
                // Tray sizes drop the tile and let the photo fill the square,
                // with a dark outline so it reads on light and dark taskbars.
                bool tiny = size <= 20;

                if (!tiny)
                {
                    float m = s * 0.06f, r = s * 0.22f;
                    using (var path = Rounded(m, m, s - 2 * m, s - 2 * m, r))
                    using (var bg = new LinearGradientBrush(new PointF(0, m), new PointF(0, s - m),
                                                            Color.FromArgb(36, 122, 98), Color.FromArgb(16, 72, 62)))
                        g.FillPath(bg, path);
                }

                // The line, sagging a little.
                float y0 = s * (tiny ? 0.16f : 0.27f), sag = s * (tiny ? 0.05f : 0.07f);
                using (var pen = new Pen(tiny ? Color.FromArgb(110, 110, 110) : Color.FromArgb(220, 236, 230), Math.Max(1f, s * 0.03f)))
                {
                    pen.StartCap = pen.EndCap = LineCap.Round;
                    float x0 = tiny ? 0.5f : s * 0.13f, x1 = tiny ? s - 0.5f : s * 0.87f;
                    g.DrawBezier(pen, x0, y0, s * 0.38f, y0 + sag * 1.33f, s * 0.62f, y0 + sag * 1.33f, x1, y0);
                }

                // The photo, landscape, hanging from its top middle and tilted
                // the way cards swing on the line.
                float pinY = y0 + sag;
                float pw = tiny ? s - 2 : s * 0.58f, ph = tiny ? (float)Math.Round(pw * 0.72f) : pw * 0.74f;
                var state = g.Save();
                g.TranslateTransform(s / 2, tiny ? (float)Math.Floor(pinY) : pinY);
                // Upright at tray sizes: a tilt only blurs a 16 pixel frame.
                if (!tiny) g.RotateTransform(-7f);
                float top = tiny ? 1 : s * 0.045f;
                var frame = new RectangleF(-pw / 2, top, pw, ph);

                if (!tiny)
                    using (var shadow = Rounded(frame.X + s * 0.012f, frame.Y + s * 0.03f, pw, ph, s * 0.035f))
                    using (var b = new SolidBrush(Color.FromArgb(70, 0, 20, 10)))
                        g.FillPath(b, shadow);

                float fr = tiny ? 1.5f : s * 0.035f;
                using (var path = Rounded(frame.X, frame.Y, pw, ph, fr))
                using (var white = new SolidBrush(Color.FromArgb(250, 252, 251)))
                {
                    g.FillPath(white, path);
                    if (tiny) using (var edge = new Pen(Color.FromArgb(200, 40, 48, 52), 1f)) g.DrawPath(edge, path);
                }

                // The picture inside: sky, sun and two hills.
                float inset = tiny ? 2f : pw * 0.075f;
                var pic = new RectangleF(frame.X + inset, frame.Y + inset, pw - 2 * inset, ph - 2 * inset);
                var clipState = g.Save();
                using (var picPath = Rounded(pic.X, pic.Y, pic.Width, pic.Height, tiny ? 0.5f : s * 0.012f))
                {
                    g.SetClip(picPath, CombineMode.Intersect);
                    using (var sky = new LinearGradientBrush(new PointF(0, pic.Top), new PointF(0, pic.Bottom),
                                                             Color.FromArgb(120, 190, 240), Color.FromArgb(196, 230, 250)))
                        g.FillRectangle(sky, pic);
                    float sun = pic.Height * (tiny ? 0.42f : 0.30f);
                    using (var b = new SolidBrush(Color.FromArgb(255, 205, 80)))
                        g.FillEllipse(b, pic.Right - pic.Width * 0.30f, pic.Top + pic.Height * 0.14f, sun, sun);
                    using (var far = new SolidBrush(Color.FromArgb(70, 128, 176)))
                        g.FillPolygon(far, new[] {
                            new PointF(pic.Left, pic.Bottom), new PointF(pic.Left, pic.Top + pic.Height * 0.62f),
                            new PointF(pic.Left + pic.Width * 0.34f, pic.Top + pic.Height * 0.30f),
                            new PointF(pic.Left + pic.Width * 0.72f, pic.Bottom) });
                    using (var near = new SolidBrush(Color.FromArgb(48, 150, 112)))
                        g.FillPolygon(near, new[] {
                            new PointF(pic.Left + pic.Width * 0.30f, pic.Bottom),
                            new PointF(pic.Left + pic.Width * 0.66f, pic.Top + pic.Height * 0.50f),
                            new PointF(pic.Right, pic.Top + pic.Height * 0.72f), new PointF(pic.Right, pic.Bottom) });
                }
                g.Restore(clipState);

                // The clip: a short steel grip over the top edge, as on screen.
                float cw = tiny ? 4 : s * 0.12f, ch = tiny ? 4 : s * 0.11f;
                float cy = tiny ? -1 : -ch * 0.40f;
                using (var steel = new LinearGradientBrush(new PointF(-cw / 2, 0), new PointF(cw / 2, 0),
                                                           Color.White, Color.Gray))
                using (var path = Rounded(-cw / 2, cy, cw, ch, tiny ? 0.8f : cw * 0.18f))
                {
                    steel.InterpolationColors = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(160, 164, 168), Color.FromArgb(240, 242, 243), Color.FromArgb(206, 210, 212), Color.FromArgb(140, 146, 150) },
                        Positions = new[] { 0f, 0.35f, 0.65f, 1f }
                    };
                    g.FillPath(steel, path);
                    using (var edge = new Pen(Color.FromArgb(tiny ? 200 : 110, 40, 48, 52), Math.Max(1f, s * 0.008f))) g.DrawPath(edge, path);
                }
                g.Restore(state);
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
