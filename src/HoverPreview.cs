using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Pegline
{
    /// The pointer must rest on the same photo before its original is decoded.
    /// A generation also prevents an old decode from reopening a hidden view.
    sealed class PreviewHoverState
    {
        Guid id;
        object revision;
        DateTime stamp, since;
        bool requested;
        public int Version { get; private set; }

        public bool Observe(Guid next, object thumb, DateTime writeTime, DateTime now)
        {
            if (next == Guid.Empty) { Reset(); return false; }
            if (id != next)
            {
                id = next;
                since = now;
                revision = thumb;
                stamp = writeTime;
                requested = false;
                Version++;
            }
            else if (!ReferenceEquals(revision, thumb) || stamp != writeTime)
            {
                revision = thumb;
                stamp = writeTime;
                requested = false;
                Version++;
            }
            if (requested || (now - since).TotalMilliseconds < 350) return false;
            requested = true;
            return true;
        }

        public bool IsCurrent(int version)
        {
            return id != Guid.Empty && requested && Version == version;
        }

        public void Reset()
        {
            if (id != Guid.Empty) Version++;
            id = Guid.Empty;
            revision = null;
            requested = false;
        }
    }

    static class PreviewLayout
    {
        public const double Inset = 6;

        static bool Positive(double value)
        {
            return value > 0 && !double.IsNaN(value) && !double.IsInfinity(value);
        }

        public static bool Valid(Rect rect)
        {
            return !rect.IsEmpty && Positive(rect.Width) && Positive(rect.Height)
                && !double.IsNaN(rect.X) && !double.IsInfinity(rect.X)
                && !double.IsNaN(rect.Y) && !double.IsInfinity(rect.Y);
        }

        /// All arguments and the result are in device pixels; scale converts
        /// the visual margins and caps from WPF units to this monitor's pixels.
        public static Drawing.Rectangle Bounds(Size sourcePixels, Rect anchor, Rect workingArea, double scale)
        {
            if (!Positive(sourcePixels.Width) || !Positive(sourcePixels.Height)
                || !Valid(anchor) || !Valid(workingArea) || !Positive(scale)) return Drawing.Rectangle.Empty;

            double margin = 12 * scale, inset = Inset * scale, gap = 12 * scale;
            double left = Math.Ceiling(workingArea.Left + margin), right = Math.Floor(workingArea.Right - margin);
            double top = Math.Ceiling(workingArea.Top + margin), bottom = Math.Floor(workingArea.Bottom - margin);
            double below = Math.Max(top, Math.Ceiling(anchor.Bottom + gap));
            double above = Math.Min(bottom, Math.Floor(anchor.Top - gap));
            bool placeBelow = bottom - below > 2 * inset + 1;
            double availableHeight = placeBelow ? bottom - below : above - top;
            double maxWidth = Math.Min(900 * scale, right - left);
            double maxHeight = Math.Min(650 * scale, availableHeight);
            if (maxWidth <= 2 * inset + 1 || maxHeight <= 2 * inset + 1) return Drawing.Rectangle.Empty;

            double fit = Math.Min(1, Math.Min((maxWidth - 2 * inset) / sourcePixels.Width,
                                            (maxHeight - 2 * inset) / sourcePixels.Height));
            int width = (int)Math.Min(Math.Floor(maxWidth), Math.Ceiling(sourcePixels.Width * fit + 2 * inset));
            int height = (int)Math.Min(Math.Floor(maxHeight), Math.Ceiling(sourcePixels.Height * fit + 2 * inset));
            int x = (int)Math.Round(Math.Max(left, Math.Min(right - width, anchor.X + anchor.Width / 2 - width / 2.0)));
            int y = (int)(placeBelow ? below : above - height);
            return new Drawing.Rectangle(x, y, width, height);
        }
    }

    /// The original is decoded off the UI thread and held only while visible.
    sealed class HoverPreview
    {
        readonly Window owner;
        readonly PreviewHoverState state = new PreviewHoverState();
        ImagePreviewWindow window;
        Pegged current;
        Rect anchor, workingArea;
        double scale;
        string screenName;
        BitmapSource displayed;
        Size sourcePixels;
        bool closed;

        public HoverPreview(Window owner)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            this.owner = owner;
            owner.Closed += OwnerClosed;
            owner.IsVisibleChanged += OwnerVisibilityChanged;
        }

        public void Update(Pegged item, Rect? card, Forms.Screen screen, DateTime now)
        {
            if (closed) return;
            if (!owner.IsVisible || item == null || item.Thumb == null || item.Falling || item.Flying
                || !card.HasValue || !PreviewLayout.Valid(card.Value) || screen == null)
            {
                Hide();
                return;
            }
            var area = Screens.ToRect(screen.WorkingArea);
            double nextScale = Screens.ScaleOf(screen);
            if (screenName != screen.DeviceName || workingArea != area || Math.Abs(scale - nextScale) > 0.001) Hide();
            screenName = screen.DeviceName;
            workingArea = area;
            scale = nextScale;
            anchor = card.Value;
            current = item;

            int previous = state.Version;
            bool load = state.Observe(item.Id, item.Thumb, item.LastWrite, now);
            if (state.Version != previous) ClearWindow();
            if (displayed != null) ShowImage(displayed, sourcePixels);
            if (load) Load(item, state.Version);
        }

        void Load(Pegged item, int version)
        {
            string path = item.Path;
            var revision = item.Thumb;
            DateTime stamp = item.LastWrite;
            var requestedAnchor = anchor;
            var requestedArea = workingArea;
            double requestedScale = scale;
            var dispatcher = owner.Dispatcher;
            ThreadPool.QueueUserWorkItem(delegate
            {
                BitmapSource bitmap = null;
                Size pixels = Size.Empty;
                try
                {
                    pixels = Thumbnails.PixelSizeOf(path);
                    var bounds = PreviewLayout.Bounds(pixels, requestedAnchor, requestedArea, requestedScale);
                    if (!bounds.IsEmpty)
                    {
                        int decode = (int)Math.Ceiling(Math.Max(bounds.Width, bounds.Height) - 2 * PreviewLayout.Inset * requestedScale);
                        bitmap = Thumbnails.Load(path, Math.Max(1, decode));
                    }
                }
                catch (Exception) { }
                if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
                try
                {
                    dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (closed || !owner.IsVisible || !state.IsCurrent(version) || current != item
                            || item.Falling || item.Flying || !ReferenceEquals(item.Thumb, revision)
                            || item.LastWrite != stamp || !File.Exists(path)) return;
                        if (bitmap == null) { ClearWindow(); return; }
                        displayed = bitmap;
                        sourcePixels = pixels;
                        ShowImage(bitmap, pixels);
                    }));
                }
                catch (InvalidOperationException) { }
            });
        }

        void ShowImage(BitmapSource bitmap, Size pixels)
        {
            var bounds = PreviewLayout.Bounds(pixels, anchor, workingArea, scale);
            if (bounds.IsEmpty) { ClearWindow(); return; }
            if (window == null) window = new ImagePreviewWindow(owner);
            window.ShowImage(bitmap, bounds);
        }

        void ClearWindow()
        {
            displayed = null;
            sourcePixels = Size.Empty;
            if (window != null) window.Release();
        }

        public void Hide()
        {
            state.Reset();
            current = null;
            ClearWindow();
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            Hide();
            owner.Closed -= OwnerClosed;
            owner.IsVisibleChanged -= OwnerVisibilityChanged;
            if (window != null && !window.IsClosed) window.Close();
            window = null;
        }

        void OwnerClosed(object sender, EventArgs e) { Close(); }
        void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!owner.IsVisible) Hide();
        }
    }

    sealed class ImagePreviewWindow : ScreenWindow
    {
        readonly Image image = new Image { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        readonly Border frame;
        IntPtr handle;
        public bool IsClosed { get; private set; }

        public ImagePreviewWindow(Window owner)
        {
            Owner = owner;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowActivated = false;
            ShowInTaskbar = false;
            Focusable = false;
            IsHitTestVisible = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            Title = "Pegline preview";
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            frame = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(248, 248, 248)),
                BorderBrush = Frame.Edge(), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(Frame.Radius), Padding = new Thickness(PreviewLayout.Inset - 1),
                Child = image, IsHitTestVisible = false
            };
            Content = frame;
            SourceInitialized += delegate
            {
                handle = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(handle, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
                HwndSource.FromHwnd(handle).AddHook(WndProc);
            };
            Closed += delegate { IsClosed = true; image.Source = null; };
        }

        public void ShowImage(BitmapSource bitmap, Drawing.Rectangle bounds)
        {
            if (IsClosed) return;
            image.Source = bitmap;
            if (PixelBounds != bounds) Place(bounds);
            frame.Width = bounds.Width / Scale;
            frame.Height = bounds.Height / Scale;
            if (!IsVisible)
            {
                Show();
                Native.SetWindowPos(handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
        }

        public void Release()
        {
            image.Source = null;
            if (!IsClosed && IsVisible) Hide();
        }

        static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                handled = true;
                return new IntPtr(Native.MA_NOACTIVATE);
            }
            return IntPtr.Zero;
        }
    }
}
