using System;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Pegline
{
    /// Click copies, double click opens, press and hold edits, the corner
    /// cross discards, and dragging hands the real file to any app:
    ///
    /// - An app gets a copy, and the photo stays on the line.
    /// - A folder or the Desktop keeps the file, and the photo leaves the line.
    /// - The Recycle Bin discards it.
    /// - Nowhere that accepts it: the photo stays on the line.
    static class Gestures
    {
        public static bool IsDragging;
        public static bool MenuOpen;

        /// How long you hold before the editor opens. Long enough not to fire
        /// on a slow click, short enough to feel deliberate.
        const double HoldSeconds = 0.45;

        public static void Attach(FrameworkElement target, CardView card, Line line)
        {
            Point? down = null;
            bool startedDrag = false, didLongPress = false;
            var hold = new DispatcherTimer { Interval = TimeSpan.FromSeconds(HoldSeconds) };
            Guid id = card.Item.Id;

            Action endPress = delegate
            {
                hold.Stop();
                if (line.PressedId == id) line.PressedId = Guid.Empty;
            };

            hold.Tick += delegate
            {
                hold.Stop();
                if (down == null || startedDrag) return;
                didLongPress = true;
                endPress();
                line.Markup(id);
            };

            target.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                if (card.InCross(e.GetPosition(target)))
                {
                    down = null;
                    line.Discard(id);
                    return;
                }
                if (e.ClickCount == 2)
                {
                    down = null;
                    endPress();
                    line.Open(id);
                    return;
                }
                down = e.GetPosition(target);
                startedDrag = false;
                didLongPress = false;
                line.PressedId = id;
                hold.Start();
                target.CaptureMouse();
            };

            target.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (down == null || startedDrag || didLongPress || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(target);
                if (Math.Abs(p.X - down.Value.X) + Math.Abs(p.Y - down.Value.Y) <= 4) return;
                startedDrag = true;
                endPress();
                target.ReleaseMouseCapture();
                Drag(target, card, line);
                down = null;
            };

            target.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                endPress();
                target.ReleaseMouseCapture();
                if (down != null && !startedDrag && !didLongPress) line.Copy(id);
                down = null;
                didLongPress = false;
            };

            target.LostMouseCapture += delegate { if (!startedDrag) endPress(); };

            target.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                e.Handled = true;
                ShowMenu(target, line, id);
            };
        }

        static void Drag(FrameworkElement source, CardView card, Line line)
        {
            Guid id = card.Item.Id;
            string path = card.Item.Path;
            if (!File.Exists(path)) { line.Prune(); return; }

            var data = new DataObject();
            data.SetFileDropList(new StringCollection { path });

            // Windows draws no preview for a plain file drag, so a ghost of
            // the photo follows the pointer. It ignores the mouse, so drop
            // targets underneath still see the drag.
            var ghost = new DragGhost(card.Item.Thumb);
            ghost.MoveToCursor();
            ghost.Show();
            GiveFeedbackEventHandler feedback = delegate { ghost.MoveToCursor(); };
            // Feedback only arrives while the pointer is over a drop target;
            // a timer keeps the ghost with the pointer everywhere else.
            var follow = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(15) };
            follow.Tick += delegate { ghost.MoveToCursor(); };
            follow.Start();
            Trace.Write("drag start " + path);
            source.GiveFeedback += feedback;

            IsDragging = true;
            line.DraggingId = id;
            DragDropEffects result = DragDropEffects.None;
            try
            {
                // Apps pick copy. Explorer picks move on the same drive, so a
                // folder or the Desktop keeps the file; the Recycle Bin deletes it.
                result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
            catch (Exception) { }
            finally
            {
                source.GiveFeedback -= feedback;
                follow.Stop();
                ghost.Close();
                IsDragging = false;
                line.DraggingId = Guid.Empty;
            }

            Trace.Write("drag end " + result + " exists=" + File.Exists(path));
            line.Prune();
            // Explorer finishes a move a moment later. Check again then, so a
            // photo saved into a folder leaves the line.
            foreach (double delay in new[] { 0.6, 1.5 })
            {
                var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
                t.Tick += delegate { t.Stop(); line.Prune(); };
                t.Start();
            }
        }

        static void ShowMenu(FrameworkElement target, Line line, Guid id)
        {
            var menu = new ContextMenu();
            Action<string, Action> add = (title, action) =>
            {
                var item = new MenuItem { Header = title };
                item.Click += delegate { action(); };
                menu.Items.Add(item);
            };
            add(Loc.L("Copy", "Copiar", "复制"), () => line.Copy(id));
            add(Loc.L("Open", "Abrir", "打开"), () => line.Open(id));
            add(Loc.L("Edit", "Editar", "编辑"), () => line.Markup(id));
            add(Loc.L("Show in Explorer", "Mostrar en el Explorador", "在资源管理器中显示"), () => line.Reveal(id));
            bool inInbox = line.IsInInbox(id);
            if (inInbox) add(Loc.L("Save to Desktop", "Guardar en el Escritorio", "保存到桌面"), () => line.SaveToDesktop(id));
            menu.Items.Add(new Separator());
            if (inInbox)
            {
                add(Loc.L("Discard", "Descartar", "丢弃"), () => line.Discard(id));
            }
            else
            {
                add(Loc.L("Take down", "Descolgar", "取下"), () => line.Discard(id));
                add(Loc.L("Move to Recycle Bin", "Mover a la Papelera", "移到回收站"), () => line.Trash(id));
            }

            // The line never takes focus; the menu needs it to close when you
            // click elsewhere.
            var src = PresentationSource.FromVisual(target) as HwndSource;
            if (src != null) Native.SetForegroundWindow(src.Handle);
            MenuOpen = true;
            menu.Closed += delegate { MenuOpen = false; };
            menu.PlacementTarget = target;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
            menu.IsOpen = true;
        }
    }

    /// The drag preview: the photo in its glass frame, slightly see-through.
    class DragGhost : ScreenWindow
    {
        readonly Size size;

        public DragGhost(System.Windows.Media.Imaging.BitmapSource thumb)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            var card = Frame.CardSize(thumb.PixelWidth, thumb.PixelHeight);
            size = new Size(card.Width * 0.9, card.Height * 0.9);
            var border = new Border
            {
                CornerRadius = new CornerRadius(Frame.Radius), Background = Frame.Glass(), Padding = new Thickness(Frame.Inset),
                BorderBrush = Frame.Edge(), BorderThickness = new Thickness(0.75), Opacity = 0.85,
                Child = new Image { Source = thumb, Stretch = Stretch.Uniform }
            };
            Content = border;
            SourceInitialized += delegate
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.SetExStyle(hwnd, Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, true);
            };
        }

        public void MoveToCursor()
        {
            var p = Screens.Cursor();
            // Sized for the screen under the pointer, so it changes size as it
            // crosses to a screen with another scale, the way windows do.
            double s = Screens.ScaleAt(new System.Drawing.Point(p.X, p.Y));
            int w = (int)Math.Ceiling(size.Width * s), h = (int)Math.Ceiling(size.Height * s);
            Place(new System.Drawing.Rectangle(p.X - w / 2, p.Y + (int)Math.Round(12 * s), w, h));
        }
    }
}
