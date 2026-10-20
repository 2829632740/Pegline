using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
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
        readonly Dictionary<Tool, Button> toolButtons = new Dictionary<Tool, Button>();
        readonly Dictionary<Color, Button> colorButtons = new Dictionary<Color, Button>();
        readonly Dictionary<double, Button> widthButtons = new Dictionary<double, Button>();
        Tool tool = Tool.Arrow;
        Color color = Colors.Red;
        double stroke = 8;
        Button undoButton;
        Button redoButton;
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
            var bar = new DockPanel { Margin = new Thickness(12, 8, 12, 8), LastChildFill = true };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            AddToolButton(controls, "↗", Tool.Arrow, Loc.L("Arrow", "Flecha", "箭头"));
            AddToolButton(controls, "□", Tool.Rectangle, Loc.L("Rectangle", "Rectángulo", "矩形"));
            AddToolButton(controls, "T", Tool.Text, Loc.L("Text", "Texto", "文字"));
            AddToolButton(controls, "▦", Tool.Mosaic, Loc.L("Mosaic", "Mosaico", "马赛克"));
            AddDivider(controls);
            AddColorButton(controls, Colors.Red); AddColorButton(controls, Colors.Gold); AddColorButton(controls, Colors.LimeGreen);
            AddColorButton(controls, Colors.DodgerBlue); AddColorButton(controls, Colors.Black); AddColorButton(controls, Colors.White);
            AddDivider(controls);
            AddWidthButton(controls, 4); AddWidthButton(controls, 8); AddWidthButton(controls, 12);
            AddDivider(controls);
            undoButton = Button(Loc.L("Undo", "Deshacer", "撤销"));
            undoButton.ToolTip = Loc.L("Undo (Ctrl+Z)", "Deshacer (Ctrl+Z)", "撤销 (Ctrl+Z)");
            undoButton.Click += delegate { Undo(); };
            controls.Children.Add(undoButton);
            redoButton = Button(Loc.L("Redo", "Rehacer", "重做"));
            redoButton.ToolTip = Loc.L("Redo (Ctrl+Y)", "Rehacer (Ctrl+Y)", "重做 (Ctrl+Y)");
            redoButton.Click += delegate { Redo(); };
            controls.Children.Add(redoButton);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var cancel = Button(Loc.L("Cancel", "Cancelar", "取消"));
            cancel.Click += delegate { DialogResult = false; };
            actions.Children.Add(cancel);
            var save = PrimaryButton(Loc.L("Save", "Guardar", "保存"));
            save.Click += delegate { Save(); };
            actions.Children.Add(save);
            DockPanel.SetDock(actions, Dock.Right);
            bar.Children.Add(actions);
            bar.Children.Add(controls);
            UpdateToolbarState();
            UpdateHistoryButtons();
            var toolbar = new Border
            {
                Background = Brush(37, 37, 38),
                BorderBrush = Brush(62, 62, 66),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = bar
            };
            DockPanel.SetDock(toolbar, Dock.Top);
            return toolbar;
        }

        Button Button(string label)
        {
            return new Button { Content = label, Style = ToolbarButtonStyle(), Margin = new Thickness(2), Padding = new Thickness(10, 6, 10, 6), MinWidth = 44 };
        }

        Button PrimaryButton(string label)
        {
            return new Button { Content = label, Style = PrimaryToolbarButtonStyle(), Margin = new Thickness(2), Padding = new Thickness(14, 6, 14, 6), MinWidth = 60 };
        }

        void AddToolButton(Panel bar, string label, Tool selected, string tip)
        {
            var button = Button(label);
            button.ToolTip = tip;
            button.FontSize = selected == Tool.Text ? 16 : 18;
            button.Click += delegate { tool = selected; drawing.Cursor = selected == Tool.Text ? Cursors.IBeam : Cursors.Cross; UpdateToolbarState(); };
            bar.Children.Add(button);
            toolButtons.Add(selected, button);
        }

        void AddColorButton(Panel bar, Color value)
        {
            var button = new Button { Style = ToolbarButtonStyle(), Margin = new Thickness(3), Width = 28, Height = 28, Background = new SolidColorBrush(value), BorderBrush = Brushes.Transparent, BorderThickness = new Thickness(1) };
            button.ToolTip = value.ToString();
            button.Click += delegate { color = value; UpdateToolbarState(); };
            bar.Children.Add(button);
            colorButtons.Add(value, button);
        }

        void AddWidthButton(Panel bar, double value)
        {
            var button = Button(value == 4 ? "·" : value == 8 ? "●" : "⬤");
            button.ToolTip = Loc.L("Line width", "Grosor", "线宽") + " " + value;
            button.Click += delegate { stroke = value; UpdateToolbarState(); };
            bar.Children.Add(button);
            widthButtons.Add(value, button);
        }

        static void AddDivider(Panel bar)
        {
            bar.Children.Add(new Border { Width = 1, Height = 28, Margin = new Thickness(9, 0, 9, 0), Background = Brush(62, 62, 66), VerticalAlignment = VerticalAlignment.Center });
        }

        static Brush Brush(byte red, byte green, byte blue)
        {
            var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
            brush.Freeze();
            return brush;
        }

        static Style ToolbarButtonStyle()
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(45, 45, 48)));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(80, 80, 84)));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(UIElement.FocusableProperty, true));
            style.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, Brush(62, 62, 66)) } });
            style.Triggers.Add(new Trigger { Property = ButtonBase.IsPressedProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, Brush(30, 86, 137)) } });
            style.Triggers.Add(new Trigger { Property = UIElement.IsEnabledProperty, Value = false, Setters = { new Setter(UIElement.OpacityProperty, 0.42) } });
            return style;
        }

        static Style PrimaryToolbarButtonStyle()
        {
            var style = new Style(typeof(Button), ToolbarButtonStyle());
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brush(14, 99, 156)));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Brush(82, 166, 220)));
            style.Triggers.Add(new Trigger { Property = UIElement.IsMouseOverProperty, Value = true, Setters = { new Setter(Control.BackgroundProperty, Brush(17, 119, 187)) } });
            return style;
        }

        void UpdateToolbarState()
        {
            foreach (var pair in toolButtons)
            {
                bool selected = pair.Key == tool;
                pair.Value.Background = selected ? Brush(14, 99, 156) : Brush(45, 45, 48);
                pair.Value.BorderBrush = selected ? Brush(130, 196, 255) : Brush(80, 80, 84);
            }
            foreach (var pair in colorButtons)
            {
                bool selected = pair.Key == color;
                pair.Value.BorderBrush = selected ? (pair.Key == Colors.White ? Brush(14, 99, 156) : Brushes.White) : Brushes.Transparent;
                pair.Value.BorderThickness = new Thickness(selected ? 3 : 1);
            }
            foreach (var pair in widthButtons)
            {
                bool selected = pair.Key == stroke;
                pair.Value.Background = selected ? Brush(14, 99, 156) : Brush(45, 45, 48);
                pair.Value.BorderBrush = selected ? Brush(130, 196, 255) : Brush(80, 80, 84);
            }
        }

        void UpdateHistoryButtons()
        {
            if (undoButton != null) undoButton.IsEnabled = marks.Count > 0;
            if (redoButton != null) redoButton.IsEnabled = undone.Count > 0;
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
            if (arrow != null) arrow.Set(start, end);
            else if (rectangle != null) SetRectangle(rectangle, start, end);
            if (tool == Tool.Mosaic && mosaicSelection != null)
            {
                drawing.Children.Remove(mosaicSelection);
                AddMosaic(start, end);
            }
            else if (arrow != null) AddMarkIfDragged(arrow.Root, end);
            else if (rectangle != null) AddMarkIfDragged(rectangle, end);
            arrow = null;
            rectangle = null;
            mosaicSelection = null;
        }

        void AddMarkIfDragged(UIElement mark, Point end)
        {
            double dx = end.X - start.X, dy = end.Y - start.Y;
            if (dx * dx + dy * dy < 1)
            {
                drawing.Children.Remove(mark);
                return;
            }
            AddMark(mark);
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
            UpdateHistoryButtons();
        }

        void Undo()
        {
            foreach (var box in textEditors.ToArray()) CommitText(box);
            if (marks.Count == 0) return;
            UIElement mark = marks[marks.Count - 1];
            marks.RemoveAt(marks.Count - 1);
            drawing.Children.Remove(mark);
            undone.Push(mark);
            UpdateHistoryButtons();
        }

        void Redo()
        {
            if (undone.Count == 0) return;
            UIElement mark = undone.Pop();
            drawing.Children.Add(mark);
            marks.Add(mark);
            UpdateHistoryButtons();
        }

        void Keys(object sender, KeyEventArgs e)
        {
            if (Keyboard.FocusedElement is TextBox) return;
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
