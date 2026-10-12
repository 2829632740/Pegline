using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pegline
{
    static class PreviewTests
    {
        [STAThread]
        static int Main()
        {
            try
            {
                Screens.Init();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                HoverPreviewTests.Run();
                RunWindowTests();
                Console.WriteLine("All preview tests passed.");
                app.Shutdown();
                return 0;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                return 1;
            }
        }

        static void Check(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }

        static void Pump(int milliseconds)
        {
            var frame = new DispatcherFrame();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            Dispatcher.PushFrame(frame);
        }

        static void Until(Func<bool> condition)
        {
            var until = DateTime.UtcNow.AddSeconds(4);
            while (!condition() && DateTime.UtcNow < until) Pump(20);
            Check(condition(), "Preview did not finish loading within four seconds.");
        }

        static Image FindImage(DependencyObject node)
        {
            var image = node as Image;
            if (image != null) return image;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                var found = FindImage(VisualTreeHelper.GetChild(node, i));
                if (found != null) return found;
            }
            return null;
        }

        static ImagePreviewWindow VisiblePreview()
        {
            return Application.Current.Windows.OfType<ImagePreviewWindow>().FirstOrDefault(w => w.IsVisible);
        }

        static void RunWindowTests()
        {
            string path = Path.Combine(Path.GetTempPath(), "pegline-preview-" + Guid.NewGuid() + ".png");
            using (var bitmap = new System.Drawing.Bitmap(1800, 1200))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.DarkSeaGreen);
                bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            var owner = new Window { Title = "Pegline preview verification", Width = 240, Height = 100,
                WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false };
            var preview = new HoverPreview(owner);
            try
            {
                owner.Show();
                Pump(30);
                var foreground = Native.GetForegroundWindow();
                var screen = Forms.Screen.PrimaryScreen;
                var area = screen.WorkingArea;
                var anchor = new Rect(area.Left + area.Width / 2 - 80, area.Top + 50, 160, 100);
                var item = new Pegged(path, Thumbnails.Load(path, 480));
                var start = DateTime.UtcNow;
                preview.Update(item, anchor, screen, start);
                Pump(80);
                Check(VisiblePreview() == null, "A quick pass over a photo must not open a preview.");
                preview.Update(item, anchor, screen, start.AddSeconds(1));
                Until(() => VisiblePreview() != null);
                var window = VisiblePreview();
                Check(Native.GetForegroundWindow() == foreground, "Opening preview stole keyboard focus.");
                var bounds = window.PixelBounds;
                Check(area.Contains(bounds), "Preview escaped its monitor working area.");
                Check(bounds.Top >= anchor.Bottom, "Preview covers its hovered photo.");
                var image = FindImage(window);
                var source = image == null ? null : image.Source as BitmapSource;
                Check(source != null && source.PixelWidth > item.Thumb.PixelWidth,
                    "Preview must decode the original, not enlarge the 480-pixel thumbnail.");
                int style = Native.GetWindowLong(new System.Windows.Interop.WindowInteropHelper(window).Handle, Native.GWL_EXSTYLE);
                Check((style & Native.WS_EX_NOACTIVATE) != 0 && (style & Native.WS_EX_TRANSPARENT) != 0,
                    "Preview must be non-activating and allow clicks to pass through.");
                using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                Console.WriteLine("PASS: real preview loads original, fits screen, preserves focus, passes clicks and releases file.");

                preview.Hide();
                Check(VisiblePreview() == null, "Leaving photo must hide preview immediately.");
                Check(FindImage(window).Source == null, "Hidden preview must release its decoded image.");
                preview.Update(item, anchor, screen, start.AddSeconds(2));
                preview.Update(item, anchor, screen, start.AddSeconds(3));
                preview.Hide();
                Pump(150);
                Check(VisiblePreview() == null, "An async load completed after leaving and reopened preview.");
                Console.WriteLine("PASS: leave cancels pending decode and releases displayed image.");

                var target = new Border();
                Gestures.Attach(target, new CardView(item, null, 0), null);
                Gestures.Interacting += preview.Hide;
                try
                {
                    foreach (var button in new[] { MouseButton.Left, MouseButton.Right })
                    {
                        preview.Update(item, anchor, screen, start.AddSeconds(4));
                        preview.Update(item, anchor, screen, start.AddSeconds(5));
                        Until(() => VisiblePreview() != null);
                        target.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, button)
                            { RoutedEvent = Mouse.PreviewMouseDownEvent });
                        Check(VisiblePreview() == null, "Mouse down must hide preview before click or context menu handling.");
                    }
                }
                finally { Gestures.Interacting -= preview.Hide; }
                Console.WriteLine("PASS: left and right mouse down dismiss preview synchronously.");

                File.WriteAllText(path, "not an image");
                preview.Update(item, anchor, screen, start.AddSeconds(6));
                preview.Update(item, anchor, screen, start.AddSeconds(7));
                Pump(200);
                Check(VisiblePreview() == null, "Unreadable image must not open a stale preview.");
                preview.Close();
                Pump(50);
                Check(VisiblePreview() == null, "Closing preview must cancel all work.");
                Console.WriteLine("PASS: invalid image and shutdown leave no preview.");
            }
            finally
            {
                preview.Close();
                owner.Close();
                File.Delete(path);
            }
        }
    }
}
