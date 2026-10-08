using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace Pegline
{
    /// The capture lifting off the screen and flying up to the line. It turns
    /// into the hanging card on the way: it shrinks, tilts into place and
    /// grows its glass frame and clip, so there is nothing left to change on
    /// landing. The same window draws a discarded card falling.
    class Flight : Window
    {
        const double Arc = 30;

        readonly Canvas canvas = new Canvas();
        readonly Grid container = new Grid();
        readonly Border glass = new Border();
        readonly Image photo = new Image();
        readonly UIElement clip = Frame.Clothespin();
        readonly RotateTransform rotate = new RotateTransform();
        readonly Rect from, to, screen;
        readonly double tilt;
        readonly bool falling;
        readonly double duration;
        readonly Action completion;
        TimeSpan start;
        double fadeOut = -1;

        /// from and to are in screen units; to is the card's frame on the line.
        public static void Fly(BitmapSource image, Rect from, Rect to, double tilt, Forms.Screen on, Action completion)
        {
            new Flight(image, from, to, tilt, on, false, completion).Show();
        }

        /// A discarded card falling off the line, drawn over the whole screen
        /// so it is never cut by the line's strip: 520 points down, tilting
        /// further, fading, 0.55 s ease in.
        public static void Fall(BitmapSource image, Rect card, double tilt, Forms.Screen on)
        {
            new Flight(image, card, card, tilt, on, true, null).Show();
        }

        Flight(BitmapSource image, Rect from, Rect to, double tilt, Forms.Screen on, bool falling, Action completion)
        {
            this.from = from;
            this.to = to;
            this.tilt = tilt;
            this.falling = falling;
            this.completion = completion;
            duration = falling ? 0.55 : 0.65;
            screen = Screens.ToDips(on.Bounds);

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Left = screen.X;
            Top = screen.Y;
            Width = screen.Width;
            Height = screen.Height;

            glass.Background = Frame.Glass();
            glass.BorderBrush = Frame.Edge();
            glass.BorderThickness = new Thickness(1);
            container.Children.Add(glass);
            photo.Source = image;
            photo.Stretch = Stretch.UniformToFill;
            RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.HighQuality);
            container.Children.Add(photo);
            container.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Direction = 270, Opacity = 0.24 };
            container.RenderTransform = rotate;
            canvas.Children.Add(container);
            canvas.Children.Add(clip);
            canvas.IsHitTestVisible = false;
            Content = canvas;

            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
            };
            if (falling) UpdateFall(0); else Update(0);
            CompositionTarget.Rendering += OnFrame;
        }

        void OnFrame(object sender, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (start == TimeSpan.Zero) start = now;
            double t = (now - start).TotalSeconds;

            if (fadeOut >= 0)
            {
                // The real card fades in underneath; this one fades out over it.
                double k2 = Math.Min(1, (t - fadeOut) / 0.16);
                Opacity = 1 - k2;
                if (k2 >= 1) Finish();
                return;
            }

            double k = Math.Min(1, t / duration);
            if (falling) UpdateFall(k); else Update(k);
            if (k < 1) return;
            if (falling) { Finish(); return; }
            if (completion != null) completion();
            fadeOut = t;
        }

        void Finish()
        {
            CompositionTarget.Rendering -= OnFrame;
            Close();
        }

        void Place(double w, double h, double topX, double topY, double angle)
        {
            container.Width = w;
            container.Height = h;
            Canvas.SetLeft(container, topX - w / 2);
            Canvas.SetTop(container, topY);
            rotate.CenterX = w / 2;
            rotate.CenterY = 0;
            rotate.Angle = angle;
            // The clip grips the top edge: 26 points tall, 12 of them over the card.
            clip.RenderTransform = new RotateTransform(angle, 4.5, 14);
            Canvas.SetLeft(clip, topX - 4.5);
            Canvas.SetTop(clip, topY - 14);
        }

        void UpdateFall(double raw)
        {
            double e = raw * raw * raw;
            double angle = tilt + (tilt * 7 + 20) * e;
            SetChrome(to.Width, to.Height, 1);
            Place(to.Width, to.Height, to.X + to.Width / 2 - screen.X, to.Y - screen.Y + 520 * e, angle);
            Opacity = 1 - e;
        }

        void Update(double raw)
        {
            double k = Ease.InOutCubic(raw);
            double chrome = Ease.Smooth(k, 0.35, 1);
            Func<double, double, double> lerp = (a, b) => a + (b - a) * k;
            double w = lerp(from.Width, to.Width), h = lerp(from.Height, to.Height);
            double topX = lerp(from.X + from.Width / 2, to.X + to.Width / 2) - screen.X;
            double topY = lerp(from.Y, to.Y) - screen.Y - Math.Sin(Math.PI * k) * Arc;
            SetChrome(w, h, k);
            glass.Opacity = chrome;
            clip.Opacity = chrome;
            Place(w, h, topX, topY, tilt * k);
        }

        void SetChrome(double w, double h, double k)
        {
            double inset = Frame.Inset * k;
            double radius = Frame.Radius * k;
            glass.CornerRadius = new CornerRadius(radius);
            photo.Margin = new Thickness(inset);
            double pw = Math.Max(0, w - inset * 2), ph = Math.Max(0, h - inset * 2);
            double r = Math.Max(0, radius - inset);
            photo.Clip = new RectangleGeometry(new Rect(0, 0, pw, ph), r, r);
        }
    }

    /// Where on screen a capture was taken. Windows does not record it on the
    /// file, so it is worked out from the image's size: the whole virtual
    /// desktop or one screen (PrtScn), the active window (Alt+PrtScn), or a
    /// region whose bottom right corner is where the pointer let go (the
    /// Snipping Tool). Returned in screen units, or null to just drop it in.
    static class CaptureArea
    {
        public static Rect? Guess(int width, int height, IntPtr foreground, bool fromPointer = true)
        {
            if (width < 3 || height < 3) return null;
            Func<int, int, bool> near = (a, b) => Math.Abs(a - b) <= 2;
            var virt = Forms.SystemInformation.VirtualScreen;
            foreach (var s in Forms.Screen.AllScreens)
                if (near(s.Bounds.Width, width) && near(s.Bounds.Height, height)
                    && s.Bounds.Contains(Screens.UnderPointer().Bounds.Location))
                    return Screens.ToDips(s.Bounds);
            if (near(virt.Width, width) && near(virt.Height, height))
            {
                // A capture of every screen flies up from the one with the line.
                return Screens.ToDips(Screens.UnderPointer().Bounds);
            }
            Native.RECT r;
            if (foreground != IntPtr.Zero && Native.GetWindowRect(foreground, out r)
                && Math.Abs(r.Right - r.Left - width) <= 20 && Math.Abs(r.Bottom - r.Top - height) <= 20)
            {
                int dx = (r.Right - r.Left - width) / 2;
                return Screens.ToDips(new System.Drawing.Rectangle(r.Left + dx, r.Top, width, height));
            }
            if (!fromPointer) return null;
            var p = Screens.Cursor();
            var area = new System.Drawing.Rectangle(p.X - width, p.Y - height, width, height);
            if (virt.Contains(area)) return Screens.ToDips(area);
            return null;
        }
    }

    /// Knows when to stay hidden: a full screen app (a video, a game, a
    /// presentation) on that screen should never get a line across the top.
    static class FullScreen
    {
        public static bool IsActive(Forms.Screen screen)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Native.GetShellWindow() || fg == Native.GetDesktopWindow()) return false;
            string cls = Native.ClassName(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            var b = screen.Bounds;
            return r.Left <= b.Left && r.Top <= b.Top && r.Right >= b.Right && r.Bottom >= b.Bottom;
        }
    }
}
