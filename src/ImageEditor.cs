using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ShapeLine = System.Windows.Shapes.Line;

namespace Pegline
{
    /// A small, purpose-built markup sheet.  It is intentionally a screenshot
    /// annotator, not a general paint program: every visual lives on a canvas
    /// whose coordinates are the source image's pixels, so rendering preserves
    /// the original dimensions even when the preview is scaled to fit a screen.
    sealed class ImageEditor : Window
    {
        enum Tool { Arrow, Rectangle, Text, Mosaic }

        readonly BitmapSource source;
        readonly Func<string> outputPath;
        readonly Canvas drawing = new Canvas();
        readonly List<TextBox> textEditors = new List<TextBox>();
        readonly List<UIElement> marks = new List<UIElement>();
        readonly Stack<UIElement> undone = new Stack<UIElement>();
        Tool tool = Tool.Arrow;
        Color color = Colors.Red;
        double stroke = 8;
        Point start;
        bool drawingNow;
        ArrowShape arrow;
        Rectangle rectangle;
        Rectangle mosaicSelection;
        const int MosaicBlock = 16;

        ImageEditor(BitmapSource source, Func<string> outputPath)
        {
            this.source = source;
            this.outputPath = outputPath;
            Title = Loc.L("Mark up screenshot", "Anotar captura", "标注截图");
            Width = 1120;
            Height = 760;
            MinWidth = 720;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = new SolidColorBrush(Color.FromRgb(34, 34, 34));

            var root = new DockPanel();
            root.Children.Add(BuildToolbar());

            drawing.Width = source.PixelWidth;
            drawing.Height = source.PixelHeight;
            drawing.Background = Brushes.Transparent;
            drawing.ClipToBounds = true;
            var photo = new Image { Source = source, Width = drawing.Width, Height = drawing.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
            drawing.Children.Add(photo);
            drawing.MouseLeftButtonDown += Down;
            drawing.MouseMove += Move;
            drawing.MouseLeftButtonUp += Up;
            PreviewKeyDown += Keys;

            var view = new Viewbox { Stretch = Stretch.Uniform, Margin = new Thickness(16), Child = drawing };
            var frame = new Border { Background = Brushes.Black, Child = view };
            root.Children.Add(frame);
            Content = root;
        }

        UIElement BuildToolbar()
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10) };
            DockPanel.SetDock(bar, Dock.Top);
            AddToolButton(bar, "↗", Tool.Arrow, Loc.L("Arrow", "Flecha", "箭头"));
            AddToolButton(bar, "□", Tool.Rectangle, Loc.L("Rectangle", "Rectángulo", "矩形"));
            AddToolButton(bar, "T", Tool.Text, Loc.L("Text", "Texto", "文字"));
            AddToolButton(bar, "▦", Tool.Mosaic, Loc.L("Mosaic", "Mosaico", "马赛克"));
            bar.Children.Add(new Separator { Width = 16, Opacity = 0 });
            AddColorButton(bar, Colors.Red); AddColorButton(bar, Colors.Gold); AddColorButton(bar, Colors.LimeGreen);
            AddColorButton(bar, Colors.DodgerBlue); AddColorButton(bar, Colors.Black); AddColorButton(bar, Colors.White);
            bar.Children.Add(new Separator { Width = 16, Opacity = 0 });
            AddWidthButton(bar, 4); AddWidthButton(bar, 8); AddWidthButton(bar, 12);
            bar.Children.Add(new Separator { Width = 16, Opacity = 0 });
            var undo = Button(Loc.L("Undo", "Deshacer", "撤销"));
            undo.Click += delegate { Undo(); };
            bar.Children.Add(undo);
            var redo = Button(Loc.L("Redo", "Rehacer", "重做"));
            redo.Click += delegate { Redo(); };
            bar.Children.Add(redo);
            var cancel = Button(Loc.L("Cancel", "Cancelar", "取消"));
            cancel.Click += delegate { DialogResult = false; };
            bar.Children.Add(cancel);
            var save = Button(Loc.L("Save", "Guardar", "保存"));
            save.Click += delegate { Save(); };
            bar.Children.Add(save);
            return bar;
        }

        Button Button(string label)
        {
            return new Button { Content = label, Margin = new Thickness(3), Padding = new Thickness(10, 5, 10, 5), MinWidth = 44 };
        }

        void AddToolButton(Panel bar, string label, Tool selected, string tip)
        {
            var button = Button(label);
            button.ToolTip = tip;
            button.Click += delegate { tool = selected; drawing.Cursor = selected == Tool.Text ? Cursors.IBeam : Cursors.Cross; };
            bar.Children.Add(button);
        }

        void AddColorButton(Panel bar, Color value)
        {
            var button = new Button { Margin = new Thickness(3), Width = 25, Height = 25, Background = new SolidColorBrush(value), BorderBrush = Brushes.LightGray };
            button.ToolTip = value.ToString();
            button.Click += delegate { color = value; };
            bar.Children.Add(button);
        }

        void AddWidthButton(Panel bar, double value)
        {
            var button = Button(value == 4 ? "·" : value == 8 ? "●" : "⬤");
            button.ToolTip = Loc.L("Line width", "Grosor", "线宽") + " " + value;
            button.Click += delegate { stroke = value; };
            bar.Children.Add(button);
        }

        void Down(object sender, MouseButtonEventArgs e)
        {
            start = Clamp(e.GetPosition(drawing));
            if (tool == Tool.Text) { AddText(start); return; }
            drawingNow = true;
            drawing.CaptureMouse();
            if (tool == Tool.Arrow)
            {
                arrow = new ArrowShape(color, stroke);
                drawing.Children.Add(arrow.Root);
                arrow.Set(start, start);
            }
            else
            {
                rectangle = new Rectangle { Stroke = new SolidColorBrush(tool == Tool.Mosaic ? Colors.White : color), StrokeThickness = stroke, StrokeDashArray = tool == Tool.Mosaic ? new DoubleCollection { 3, 2 } : null, Fill = Brushes.Transparent, IsHitTestVisible = false };
                if (tool == Tool.Mosaic) mosaicSelection = rectangle;
                drawing.Children.Add(rectangle);
            }
            e.Handled = true;
        }

        void Move(object sender, MouseEventArgs e)
        {
            if (!drawingNow) return;
            Point end = Clamp(e.GetPosition(drawing));
            if (arrow != null) arrow.Set(start, end);
            else if (rectangle != null) SetRectangle(rectangle, start, end);
        }

        void Up(object sender, MouseButtonEventArgs e)
        {
            if (!drawingNow) return;
            drawingNow = false;
            drawing.ReleaseMouseCapture();
            Point end = Clamp(e.GetPosition(drawing));
            if (tool == Tool.Mosaic && mosaicSelection != null)
            {
                drawing.Children.Remove(mosaicSelection);
                AddMosaic(start, end);
            }
            else if (arrow != null) AddMark(arrow.Root);
            else if (rectangle != null) AddMark(rectangle);
            arrow = null;
            rectangle = null;
            mosaicSelection = null;
        }

        static void SetRectangle(FrameworkElement shape, Point a, Point b)
        {
            Canvas.SetLeft(shape, Math.Min(a.X, b.X));
            Canvas.SetTop(shape, Math.Min(a.Y, b.Y));
            shape.Width = Math.Abs(a.X - b.X);
            shape.Height = Math.Abs(a.Y - b.Y);
        }

        void AddText(Point at)
        {
            var box = new TextBox { FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(color), Background = new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)), BorderBrush = new SolidColorBrush(color), MinWidth = 180, AcceptsReturn = false };
            Canvas.SetLeft(box, at.X);
            Canvas.SetTop(box, at.Y);
            box.LostKeyboardFocus += delegate { CommitText(box); };
            box.KeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) { CommitText(box); e.Handled = true; } if (e.Key == Key.Escape) { drawing.Children.Remove(box); textEditors.Remove(box); } };
            drawing.Children.Add(box);
            textEditors.Add(box);
            box.Focus();
        }

        void CommitText(TextBox box)
        {
            if (!textEditors.Contains(box)) return;
            textEditors.Remove(box);
            double x = Canvas.GetLeft(box), y = Canvas.GetTop(box);
            drawing.Children.Remove(box);
            if (String.IsNullOrWhiteSpace(box.Text)) return;
            var text = new TextBlock { Text = box.Text, FontSize = 32, FontWeight = FontWeights.SemiBold, Foreground = box.Foreground, IsHitTestVisible = false };
            Canvas.SetLeft(text, x);
            Canvas.SetTop(text, y);
            drawing.Children.Add(text);
            AddMark(text);
        }

        void AddMosaic(Point a, Point b)
        {
            int x = Math.Max(0, (int)Math.Floor(Math.Min(a.X, b.X)));
            int y = Math.Max(0, (int)Math.Floor(Math.Min(a.Y, b.Y)));
            int w = Math.Min(source.PixelWidth - x, (int)Math.Ceiling(Math.Abs(a.X - b.X)));
            int h = Math.Min(source.PixelHeight - y, (int)Math.Ceiling(Math.Abs(a.Y - b.Y)));
            if (w < 2 || h < 2) return;
            var crop = new CroppedBitmap(source, new Int32Rect(x, y, w, h));
            var pixels = new TransformedBitmap(crop, new ScaleTransform(1.0 / MosaicBlock, 1.0 / MosaicBlock));
            var image = new Image { Source = pixels, Width = w, Height = h, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
            Canvas.SetLeft(image, x);
            Canvas.SetTop(image, y);
            drawing.Children.Add(image);
            AddMark(image);
        }

        void AddMark(UIElement mark)
        {
            marks.Add(mark);
            undone.Clear();
        }

        void Undo()
        {
            foreach (var box in textEditors.ToArray()) CommitText(box);
            if (marks.Count == 0) return;
            UIElement mark = marks[marks.Count - 1];
            marks.RemoveAt(marks.Count - 1);
            drawing.Children.Remove(mark);
            undone.Push(mark);
        }

        void Redo()
        {
            if (undone.Count == 0) return;
            UIElement mark = undone.Pop();
            drawing.Children.Add(mark);
            marks.Add(mark);
        }

        void Keys(object sender, KeyEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
            if (e.Key == Key.Z) { Undo(); e.Handled = true; }
            if (e.Key == Key.Y) { Redo(); e.Handled = true; }
        }

        Point Clamp(Point p)
        {
            return new Point(Math.Max(0, Math.Min(drawing.Width, p.X)), Math.Max(0, Math.Min(drawing.Height, p.Y)));
        }

        void Save()
        {
            foreach (var box in textEditors.ToArray()) CommitText(box);
            try
            {
                drawing.Measure(new Size(drawing.Width, drawing.Height));
                drawing.Arrange(new Rect(0, 0, drawing.Width, drawing.Height));
                var output = new RenderTargetBitmap(source.PixelWidth, source.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                output.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(output));
                string path = outputPath();
                string temp = path + ".pegline";
                using (var stream = File.Create(temp)) encoder.Save(stream);
                File.Copy(temp, path, true);
                File.Delete(temp);
                DialogResult = true;
            }
            catch (Exception)
            {
                MessageBox.Show(Loc.L("Pegline could not save this screenshot.", "Pegline no pudo guardar esta captura.", "Pegline 无法保存这张截图。"), "Pegline", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public static bool Edit(string path, Func<string> outputPath)
        {
            var bitmap = Thumbnails.Load(path, 0);
            if (bitmap == null) return false;
            return new ImageEditor(bitmap, outputPath).ShowDialog() == true;
        }

        sealed class ArrowShape
        {
            public readonly Canvas Root = new Canvas { IsHitTestVisible = false };
            readonly ShapeLine shaft = new ShapeLine();
            readonly ShapeLine left = new ShapeLine();
            readonly ShapeLine right = new ShapeLine();

            public ArrowShape(Color color, double width)
            {
                var brush = new SolidColorBrush(color);
                foreach (var line in new[] { shaft, left, right })
                {
                    line.Stroke = brush;
                    line.StrokeThickness = width;
                    line.StrokeStartLineCap = PenLineCap.Round;
                    line.StrokeEndLineCap = PenLineCap.Round;
                    Root.Children.Add(line);
                }
            }

            public void Set(Point a, Point b)
            {
                shaft.X1 = a.X; shaft.Y1 = a.Y; shaft.X2 = b.X; shaft.Y2 = b.Y;
                double angle = Math.Atan2(b.Y - a.Y, b.X - a.X);
                double head = Math.Max(18, shaft.StrokeThickness * 3.2);
                SetHead(left, b, angle + 2.55, head);
                SetHead(right, b, angle - 2.55, head);
            }

            static void SetHead(ShapeLine line, Point at, double angle, double length)
            {
                line.X1 = at.X; line.Y1 = at.Y;
                line.X2 = at.X + Math.Cos(angle) * length;
                line.Y2 = at.Y + Math.Sin(angle) * length;
            }
        }
    }
}
