using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Forms = System.Windows.Forms;

namespace Pegline
{
    /// Pixel and screen bookkeeping. The app is per monitor DPI aware (v2):
    /// everything outside a window (the pointer, screens, capture areas,
    /// card frames) is in device pixels, and each window turns them into its
    /// own WPF units with the scale of the screen it is on.
    static class Screens
    {
        public static void Init()
        {
            // The manifest already asks for this; the calls cover a stripped
            // manifest. WPF must also be told to follow DPI changes, before it
            // reads the switch with its first window.
            AppContext.SetSwitch("Switch.System.Windows.DoNotScaleForDpiChanges", false);
            try { if (Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) return; }
            catch (EntryPointNotFoundException) { }
            try { Native.SetProcessDpiAwareness(2); return; }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            Native.SetProcessDPIAware();
        }

        /// Device pixels per WPF unit on the screen holding a point.
        public static double ScaleAt(System.Drawing.Point p)
        {
            try
            {
                var monitor = Native.MonitorFromPoint(new Native.POINT { X = p.X, Y = p.Y }, Native.MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (Native.GetDpiForMonitor(monitor, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            using (var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96.0;
        }

        public static double ScaleOf(Forms.Screen s)
        {
            var b = s.Bounds;
            return ScaleAt(new System.Drawing.Point(b.X + b.Width / 2, b.Y + b.Height / 2));
        }

        public static Native.POINT Cursor()
        {
            Native.POINT p;
            Native.GetCursorPos(out p);
            return p;
        }

        public static Forms.Screen UnderPointer()
        {
            var p = Cursor();
            return Forms.Screen.FromPoint(new System.Drawing.Point(p.X, p.Y));
        }

        public static Rect ToRect(System.Drawing.Rectangle r)
        {
            return new Rect(r.X, r.Y, r.Width, r.Height);
        }

        /// The top strip of a screen that plays the part of the Mac menu bar:
        /// the taskbar when it sits at the top, otherwise the very top edge
        /// you push the pointer against.
        public static System.Drawing.Rectangle TopBand(Forms.Screen s)
        {
            int h = s.WorkingArea.Top - s.Bounds.Top;
            if (h < 2) h = 2;
            return new System.Drawing.Rectangle(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, h);
        }
    }

    /// A borderless window placed by device pixels. It draws at the scale of
    /// the screen it is on, even when that differs from the one WPF last saw:
    /// a hidden window moved across screens does not always hear about it.
    class ScreenWindow : Window
    {
        /// Where the window is, in device pixels.
        public System.Drawing.Rectangle PixelBounds;
        /// Device pixels per unit of this window's content.
        public double Scale = 1;
        IntPtr hwnd;

        public ScreenWindow()
        {
            DpiChanged += delegate
            {
                // WPF resizes to the size Windows suggests; put it back.
                Dispatcher.BeginInvoke(new Action(delegate { if (!PixelBounds.IsEmpty) Place(PixelBounds); }));
            };
        }

        /// The width of the content, in its own units.
        public double DipWidth { get { return PixelBounds.Width / Scale; } }

        public void Place(System.Drawing.Rectangle px)
        {
            PixelBounds = px;
            Scale = Screens.ScaleAt(new System.Drawing.Point(px.X + px.Width / 2, px.Y + px.Height / 2));
            if (hwnd == IntPtr.Zero) hwnd = new WindowInteropHelper(this).EnsureHandle();
            const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE;
            Native.SetWindowPos(hwnd, IntPtr.Zero, px.X, px.Y, px.Width, px.Height, flags);
            // Landing on a screen with another DPI resizes the window on the way.
            Native.RECT r;
            if (Native.GetWindowRect(hwnd, out r) && (r.Left != px.X || r.Top != px.Y || r.Right != px.Right || r.Bottom != px.Bottom))
                Native.SetWindowPos(hwnd, IntPtr.Zero, px.X, px.Y, px.Width, px.Height, flags);
            var root = Content as FrameworkElement;
            if (root == null) return;
            double f = Scale / VisualTreeHelper.GetDpi(this).DpiScaleX;
            var current = root.LayoutTransform as ScaleTransform;
            if (Math.Abs((current == null ? 1 : current.ScaleX) - f) < 0.001) return;
            root.LayoutTransform = Math.Abs(f - 1) < 0.001 ? null : new ScaleTransform(f, f);
        }

        /// A point in device pixels, in this window's content units.
        public Point ToLocal(Native.POINT p)
        {
            return new Point((p.X - PixelBounds.X) / Scale, (p.Y - PixelBounds.Y) / Scale);
        }

        /// A rectangle in this window's content units, in device pixels.
        public Rect ToScreen(Rect r)
        {
            return new Rect(PixelBounds.X + r.X * Scale, PixelBounds.Y + r.Y * Scale, r.Width * Scale, r.Height * Scale);
        }
    }

    /// A transparent strip along the top of the screen that floats over every
    /// app, never takes focus, and lets clicks pass through everywhere except
    /// over the photos.
    class LineWindow : ScreenWindow
    {
        readonly Line line;
        readonly Canvas slider = new Canvas();
        readonly Canvas rope = new Canvas();
        readonly Border hint = new Border();
        readonly Dictionary<Guid, CardView> cards = new Dictionary<Guid, CardView>();
        readonly TranslateTransform slide = new TranslateTransform(0, -(Layout.PanelHeight + 12));
        readonly Spring slideSpring = new Spring(0.42, 0.82, -(Layout.PanelHeight + 12));
        readonly Random rnd = new Random();
        double hideT = -1, hideFrom;
        double hintOpacity;
        TimeSpan lastFrame;
        double ropeWidth = -1;
        public IntPtr Handle;
        public Forms.Screen Screen;
        bool clickThrough = true;

        public LineWindow(Line line)
        {
            this.line = line;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "Pegline";

            slider.RenderTransform = slide;
            slider.Children.Add(rope);
            hint.CornerRadius = new CornerRadius(14);
            hint.Background = Frame.Glass();
            hint.BorderBrush = Frame.Edge();
            hint.BorderThickness = new Thickness(0.75);
            hint.Padding = new Thickness(12, 5, 12, 6);
            hint.IsHitTestVisible = false;
            hint.Child = new TextBlock
            {
                Text = Loc.L("Take a screenshot and it will hang here", "Haz una captura y se quedará colgada aquí",
                             "截一张图，它就会挂在这里"),
                FontSize = 12, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(70, 70, 70))
            };
            slider.Children.Add(hint);
            var root = new Canvas { ClipToBounds = true };
            root.Children.Add(slider);
            Content = root;

            SourceInitialized += delegate
            {
                Handle = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(Handle, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT, true);
                HwndSource.FromHwnd(Handle).AddHook(WndProc);
            };
            CompositionTarget.Rendering += OnFrame;
            line.Gust += delegate { foreach (var c in cards.Values) c.Breeze(rnd); };
        }

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            // Clicking a photo must not pull focus from the app you are in.
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }

        /// The line hangs on the screen you are using, which is the one with
        /// the pointer: that is where you just took the screenshot.
        public void PlaceOnScreen(Forms.Screen screen = null)
        {
            Screen = screen ?? Screens.UnderPointer();
            var area = Screen.WorkingArea;
            int height = (int)Math.Ceiling(Layout.PanelHeight * Screens.ScaleOf(Screen));
            Place(new System.Drawing.Rectangle(area.X, area.Y, area.Width, height));
        }

        public void KeepOnTop()
        {
            if (Handle != IntPtr.Zero)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// The panel spans the whole width of the screen, so it only accepts
        /// the mouse while the pointer is over a photo. Everywhere else,
        /// clicks go to whatever is underneath.
        public void SetClickThrough(bool on)
        {
            if (on == clickThrough || Handle == IntPtr.Zero) return;
            clickThrough = on;
            Native.SetExStyle(Handle, Native.WS_EX_TRANSPARENT, on);
        }

        /// The photo under a point in screen pixels, if any.
        public Guid PhotoAt(Native.POINT p)
        {
            var local = ToLocal(p);
            double x = local.X, y = local.Y;
            foreach (var c in cards.Values)
            {
                if (c.Item.Falling) continue;
                // Generous: the hover zoom and the tilt push the corners, and
                // the cross sits right on one, a few points outside the frame.
                var r = c.HitRect;
                r.Inflate(10, 10);
                if (r.Contains(x, y)) return c.Item.Id;
            }
            return Guid.Empty;
        }

        /// Where a card will hang, in device pixels, using the same layout as
        /// the line itself.
        public Rect? CardFrame(Guid id)
        {
            int index = line.Items.FindIndex(i => i.Id == id);
            if (index < 0) return null;
            double width = DipWidth;
            double x = Layout.X(index, line.Items.Count, width);
            double top = Layout.RopeY(x, width) - Layout.PinAbove + Frame.CardOffsetBelowTop;
            var thumb = line.Items[index].Thumb;
            var size = Frame.CardSize(thumb.PixelWidth, thumb.PixelHeight);
            return ToScreen(new Rect(x - size.Width / 2, top, size.Width, size.Height));
        }

        /// A hovered card with its Copy and Open buttons, in device pixels:
        /// the big preview opens below this, never over the buttons.
        public Rect? HoverFrame(Guid id)
        {
            var frame = CardFrame(id);
            if (!frame.HasValue) return null;
            var r = frame.Value;
            var item = line.Find(id);
            r.Height += Layout.Below(item != null && item.IsNote) * Scale;
            return r;
        }

        /// Keeps the views in step with the line's items.
        public void Sync()
        {
            double width = DipWidth;
            var live = new HashSet<Guid>(line.Items.Select(i => i.Id));
            foreach (var gone in cards.Keys.Where(k => !live.Contains(k)).ToList())
            {
                slider.Children.Remove(cards[gone]);
                cards.Remove(gone);
            }
            for (int i = 0; i < line.Items.Count; i++)
            {
                var item = line.Items[i];
                if (cards.ContainsKey(item.Id)) continue;
                var card = new CardView(item, line, Layout.X(i, line.Items.Count, width));
                cards[item.Id] = card;
                slider.Children.Add(card);
            }
        }

        void DrawRope(double width)
        {
            if (Math.Abs(width - ropeWidth) < 0.5) return;
            ropeWidth = width;
            rope.Children.Clear();
            double top = Layout.RopeTop;
            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = new Point(-20, top) };
            fig.Segments.Add(new QuadraticBezierSegment(new Point(width / 2, top + 2 * Layout.Sag(width)), new Point(width + 20, top), true));
            geo.Figures.Add(fig);
            geo.Freeze();
            // A thin, neutral line: a mid gray core with a faint highlight and
            // a soft shadow, so it reads on light and dark backgrounds alike.
            rope.Children.Add(new Path
            {
                Data = geo, Stroke = new SolidColorBrush(Color.FromArgb(0x38, 0, 0, 0)), StrokeThickness = 1.4,
                RenderTransform = new TranslateTransform(0, 1.2),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 2.4 }
            });
            rope.Children.Add(new Path { Data = geo, Stroke = new SolidColorBrush(Color.FromRgb(140, 140, 140)), StrokeThickness = 1.2 });
            rope.Children.Add(new Path
            {
                Data = geo, Stroke = new SolidColorBrush(Color.FromArgb(0x73, 255, 255, 255)), StrokeThickness = 0.4,
                RenderTransform = new TranslateTransform(0, -0.35)
            });
            // It fades out at both ends so it seems to come from beyond the screen.
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.08));
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0.92));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            rope.Width = width;
            rope.Height = Layout.PanelHeight;
            rope.OpacityMask = mask;
            rope.IsHitTestVisible = false;
        }

        void OnFrame(object sender, EventArgs e)
        {
            if (!IsVisible) return;
            var now = ((RenderingEventArgs)e).RenderingTime;
            double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : (now - lastFrame).TotalSeconds;
            lastFrame = now;
            if (dt <= 0) return;
            dt = Math.Min(dt, 1.0 / 20);

            double width = DipWidth;
            DrawRope(width);

            // Tucked away, the whole line waits above the top edge and slides
            // out from under it, the way an auto-hiding taskbar does.
            double hidden = -(Layout.PanelHeight + 12);
            if (line.Revealed)
            {
                hideT = -1;
                slideSpring.Target = 0;
                slideSpring.Step(dt);
            }
            else
            {
                if (hideT < 0) { hideT = 0; hideFrom = slideSpring.Value; }
                hideT = Math.Min(1, hideT + dt / 0.22);
                slideSpring.Value = hideFrom + (hidden - hideFrom) * hideT * hideT;
                slideSpring.Velocity = 0;
            }
            slide.Y = slideSpring.Value;

            hintOpacity += ((line.Items.Count == 0 ? 1 : 0) - hintOpacity) * Math.Min(1, dt / 0.3 * 2);
            hint.Opacity = hintOpacity;
            hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(hint, width / 2 - hint.DesiredSize.Width / 2);
            Canvas.SetTop(hint, Layout.RopeY(width / 2, width) + 34 - hint.DesiredSize.Height / 2);

            for (int i = 0; i < line.Items.Count; i++)
            {
                CardView card;
                if (cards.TryGetValue(line.Items[i].Id, out card))
                    card.Step(dt, Layout.X(i, line.Items.Count, width), width);
            }
        }
    }
}
