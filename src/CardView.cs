using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Pegline
{
    static class Layout
    {
        public const double PanelHeight = 210;
        public const double RopeTop = 10;
        public const double Spacing = 174;
        public const double CardWidth = 150;
        public const double PinAbove = 9.5;
        /// The Copy and Open buttons that appear under a card on hover: one
        /// row, so they fit under the tallest card within the panel.
        public const double ActionsGap = 6;
        public const double ActionsHeight = 26;
        /// A note also gets a row of colour swatches above its buttons. Notes
        /// are short sheets, so both rows still fit within the panel.
        public const double SwatchesHeight = 22;
        public const double RowGap = 4;

        /// How far a hovered card's controls reach below it.
        public static double Below(bool note)
        {
            return ActionsGap + (note ? SwatchesHeight + RowGap : 0) + ActionsHeight;
        }

        /// The rope hangs as a parabola from edge to edge of the screen.
        public static double Sag(double width) { return Math.Min(30, width * 0.018); }

        public static double RopeY(double x, double width)
        {
            if (width <= 0) return RopeTop;
            double f = x / width;
            return RopeTop + 4 * Sag(width) * f * (1 - f);
        }

        public static double X(int index, int count, double width)
        {
            double total = Math.Max(count - 1, 0) * Spacing;
            return width / 2 - total / 2 + index * Spacing;
        }
    }

    static class Frame
    {
        public const double Radius = 16;
        public const double Inset = 4;
        /// Distance from the top of the hanging view (the clip) to the card.
        public const double CardOffsetBelowTop = 26 - 12;

        /// The photo fits inside the card area keeping its proportions, so the
        /// border hugs it whether the screenshot is wide or tall.
        public static Size PhotoSize(double w, double h)
        {
            double maxW = Layout.CardWidth - 14, maxH = 104;
            if (w <= 0 || h <= 0) return new Size(maxW, maxH);
            double scale = Math.Min(maxW / w, maxH / h);
            return new Size(w * scale, h * scale);
        }

        public static Size CardSize(double w, double h)
        {
            var p = PhotoSize(w, h);
            return new Size(p.Width + Inset * 2, p.Height + Inset * 2);
        }

        public static Brush Glass()
        {
            return new SolidColorBrush(Color.FromArgb(0xB8, 0xF7, 0xF7, 0xF7));
        }

        public static Brush Edge()
        {
            return new LinearGradientBrush(Color.FromArgb(0xE0, 255, 255, 255), Color.FromArgb(0x40, 255, 255, 255), 90);
        }

        /// A minimal aluminium clip: a brushed metal pill with a slot where it
        /// grips the line, and a soft shadow so it reads on any background.
        public static UIElement Clothespin()
        {
            var metal = new LinearGradientBrush { StartPoint = new Point(0, 0.5), EndPoint = new Point(1, 0.5) };
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(179, 179, 179), 0));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(237, 237, 237), 0.35));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(209, 209, 209), 0.65));
            metal.GradientStops.Add(new GradientStop(Color.FromRgb(158, 158, 158), 1));
            var g = new Grid { Width = 9, Height = 26, IsHitTestVisible = false };
            g.Children.Add(new Rectangle
            {
                RadiusX = 3.5, RadiusY = 3.5, Fill = metal, StrokeThickness = 0.6,
                Stroke = new LinearGradientBrush(Color.FromArgb(0xE6, 255, 255, 255), Color.FromArgb(0x2E, 0, 0, 0), 90),
                Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1.5, Direction = 270, Opacity = 0.3 }
            });
            // The slot the line passes through.
            g.Children.Add(new Rectangle
            {
                Width = 5, Height = 1.4, RadiusX = 0.7, RadiusY = 0.7, VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 8.5, 0, 0), Fill = new SolidColorBrush(Color.FromArgb(0x52, 0, 0, 0))
            });
            return g;
        }
    }

    /// One photo with its clothespin. All the charm lives here: it drops onto
    /// the line, swings, sways with the breeze and falls when you pull it off.
    class CardView : Canvas
    {
        public readonly Pegged Item;
        readonly Line line;
        readonly Grid frameHost = new Grid();
        readonly Border frame = new Border();
        readonly Image photo = new Image();
        readonly Grid cross = new Grid();
        readonly Border copiedLabel = new Border();
        readonly StackPanel actions = new StackPanel { Orientation = Orientation.Horizontal };
        /// Notes only: one dot per paper colour, the current one ticked.
        readonly Border swatches;
        readonly Grid[] swatchDots = new Grid[Notes.ColorCount];
        int shownColor = -1;
        readonly UIElement pin = Frame.Clothespin();
        readonly DropShadowEffect shadow = new DropShadowEffect { Direction = 270, Color = Colors.Black };
        readonly RotateTransform rotate = new RotateTransform();
        readonly ScaleTransform scale = new ScaleTransform(1, 1);

        // Every moving value owns a spring.
        public readonly Spring X;
        readonly Spring drop = new Spring(0.42, 0.72, -46);
        readonly Spring swing = new Spring(1, 1, 0);
        readonly Spring press = new Spring(0.3, 0.6, 1);
        double opacity, crossOpacity, copiedOpacity, actionsOpacity;
        bool arrived, wasFlying, wasHover, wasCopied;
        // A nudge eases out to an angle, then the spring lets it swing back.
        double nudgeFrom, nudgeTo, nudgeT = -1;
        object shownThumb;
        Size card;

        public CardView(Pegged item, Line line, double x)
        {
            Item = item;
            this.line = line;
            X = new Spring(0.55, 0.78, x);
            Width = Layout.CardWidth;
            RenderTransform = rotate;
            rotate.CenterX = Layout.CardWidth / 2;

            frame.CornerRadius = new CornerRadius(Frame.Radius);
            frame.Background = Frame.Glass();
            frame.BorderBrush = Frame.Edge();
            frame.BorderThickness = new Thickness(0.75);
            frame.Effect = shadow;
            frameHost.Children.Add(frame);
            photo.Stretch = Stretch.Fill;
            RenderOptions.SetBitmapScalingMode(photo, BitmapScalingMode.HighQuality);
            photo.Margin = new Thickness(Frame.Inset);
            frameHost.Children.Add(photo);

            // The discard cross in the top left corner, shown on hover.
            cross.Width = cross.Height = 20;
            cross.HorizontalAlignment = HorizontalAlignment.Left;
            cross.VerticalAlignment = VerticalAlignment.Top;
            cross.Margin = new Thickness(3);
            cross.IsHitTestVisible = false;
            cross.Children.Add(new Ellipse { Fill = Frame.Glass(), Stroke = Frame.Edge(), StrokeThickness = 0.75 });
            cross.Children.Add(new Path
            {
                Data = Geometry.Parse("M0,0 L6,6 M6,0 L0,6"), Stroke = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            });
            frameHost.Children.Add(cross);

            frameHost.RenderTransform = scale;
            frameHost.Background = Brushes.Transparent;
            Children.Add(frameHost);
            Children.Add(pin);
            SetLeft(pin, Layout.CardWidth / 2 - 4.5);
            SetTop(pin, 0);

            copiedLabel.CornerRadius = new CornerRadius(12);
            copiedLabel.Background = Frame.Glass();
            copiedLabel.BorderBrush = Frame.Edge();
            copiedLabel.BorderThickness = new Thickness(0.75);
            copiedLabel.Padding = new Thickness(10, 4, 10, 5);
            copiedLabel.IsHitTestVisible = false;
            copiedLabel.Child = new TextBlock
            {
                Text = "✓ " + Loc.L("Copied", "Copiado", "已复制"),
                FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(30, 30, 30))
            };
            Children.Add(copiedLabel);

            // Copy and Open under the card, for when a click or a double click
            // is not obvious. Shown on hover; they move and tilt with the card.
            actions.Children.Add(ActionButton("", Loc.L("Copy", "Copiar", "复制"), () => line.Copy(Item.Id)));
            actions.Children.Add(ActionButton("", Loc.L("Open", "Abrir", "打开"), () => line.Open(Item.Id)));
            ((FrameworkElement)actions.Children[1]).Margin = new Thickness(6, 0, 0, 0);
            actions.Opacity = 0;
            actions.IsHitTestVisible = false;
            Children.Add(actions);

            if (item.IsNote)
            {
                var dots = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                for (int i = 0; i < Notes.ColorCount; i++)
                {
                    int color = i;
                    var dot = new Grid { Width = 15, Height = 15, Margin = new Thickness(i == 0 ? 0 : 6, 0, 0, 0), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                    dot.Children.Add(new Ellipse
                    {
                        Fill = new SolidColorBrush(Notes.Swatch(i)),
                        Stroke = new SolidColorBrush(Color.FromArgb(0x50, 0, 0, 0)), StrokeThickness = 0.75
                    });
                    dot.Children.Add(new TextBlock
                    {
                        Text = "", FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 9,
                        Foreground = new SolidColorBrush(Color.FromRgb(40, 40, 40)),
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    });
                    dot.MouseLeftButtonDown += (s, e) => { e.Handled = true; Gestures.NotifyInteracting(); };
                    dot.MouseLeftButtonUp += (s, e) => { e.Handled = true; line.SetNoteColor(Item.Id, color); };
                    dot.MouseRightButtonUp += (s, e) => { e.Handled = true; };
                    swatchDots[i] = dot;
                    dots.Children.Add(dot);
                }
                swatches = new Border
                {
                    Height = Layout.SwatchesHeight, CornerRadius = new CornerRadius(Layout.SwatchesHeight / 2), Padding = new Thickness(6, 0, 6, 0),
                    Background = Frame.Glass(), BorderBrush = Frame.Edge(), BorderThickness = new Thickness(0.75), Child = dots,
                    Effect = new DropShadowEffect { Direction = 270, ShadowDepth = 2, BlurRadius = 8, Opacity = 0.18 },
                    Opacity = 0, IsHitTestVisible = false
                };
                Children.Add(swatches);
            }

            Gestures.Attach(frameHost, this, line);

            if (item.Flying)
            {
                // A capture that flew in is already in place; the flight did the arriving.
                arrived = true;
                drop.Value = drop.Target = 0;
                wasFlying = true;
            }
            else
            {
                swing.Value = 16;
                swing.SetStiffness(46, 2.6);
                drop.Target = 0;
            }
            UpdateThumb();
        }

        /// The card's frame in window coordinates, for catching clicks only
        /// over photos. While the card is hovered it reaches down over its
        /// buttons, so moving onto them keeps them there.
        public Rect HitRect
        {
            get
            {
                double left = X.Value - card.Width / 2;
                double top = Layout.RopeY(X.Value, ActualWidthOfLine) - Layout.PinAbove + drop.Value + Frame.CardOffsetBelowTop;
                var r = new Rect(left, top, card.Width, card.Height);
                if (line.HoverId == Item.Id && line.DraggingId == Guid.Empty)
                {
                    double width = Math.Max(ActionsWidth, swatches != null ? swatches.DesiredSize.Width : 0);
                    r.Union(new Rect(X.Value - width / 2, top + card.Height, width, Layout.Below(swatches != null)));
                }
                return r;
            }
        }

        /// The width of the button row, in window units.
        public double ActionsWidth;

        public double ActualWidthOfLine;

        void UpdateThumb()
        {
            if (ReferenceEquals(shownThumb, Item.Thumb)) return;
            shownThumb = Item.Thumb;
            photo.Source = Item.Thumb;
            var p = Frame.PhotoSize(Item.Thumb.PixelWidth, Item.Thumb.PixelHeight);
            card = Frame.CardSize(Item.Thumb.PixelWidth, Item.Thumb.PixelHeight);
            photo.Width = p.Width;
            photo.Height = p.Height;
            // Concentric corners: the photo's radius is the frame's minus the inset.
            photo.Clip = new RectangleGeometry(new Rect(0, 0, p.Width, p.Height), Frame.Radius - Frame.Inset, Frame.Radius - Frame.Inset);
            frameHost.Width = card.Width;
            frameHost.Height = card.Height;
            SetLeft(frameHost, (Layout.CardWidth - card.Width) / 2);
            SetTop(frameHost, Frame.CardOffsetBelowTop);
            scale.CenterX = card.Width / 2;
            copiedLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            SetLeft(copiedLabel, (Layout.CardWidth - copiedLabel.DesiredSize.Width) / 2);
            SetTop(copiedLabel, Frame.CardOffsetBelowTop + card.Height + 16 - copiedLabel.DesiredSize.Height / 2);
            double row = Frame.CardOffsetBelowTop + card.Height + Layout.ActionsGap;
            if (swatches != null)
            {
                swatches.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                SetLeft(swatches, (Layout.CardWidth - swatches.DesiredSize.Width) / 2);
                SetTop(swatches, row);
                row += Layout.SwatchesHeight + Layout.RowGap;
            }
            actions.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ActionsWidth = actions.DesiredSize.Width;
            SetLeft(actions, (Layout.CardWidth - ActionsWidth) / 2);
            SetTop(actions, row);
            Height = Frame.CardOffsetBelowTop + card.Height + Math.Max(40, Layout.Below(swatches != null) + 4);
        }

        /// A small glass pill with an icon and a word.
        static Border ActionButton(string glyph, string label, Action act)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var ink = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            row.Children.Add(new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 11, Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 1, 6, 0)
            });
            row.Children.Add(new TextBlock { Text = label, FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = ink, VerticalAlignment = VerticalAlignment.Center });
            var idle = Frame.Glass();
            var lit = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
            var b = new Border
            {
                Height = Layout.ActionsHeight, CornerRadius = new CornerRadius(Layout.ActionsHeight / 2), Padding = new Thickness(11, 0, 13, 0),
                Background = idle, BorderBrush = Frame.Edge(), BorderThickness = new Thickness(0.75), Child = row, Cursor = Cursors.Hand,
                Effect = new DropShadowEffect { Direction = 270, ShadowDepth = 2, BlurRadius = 8, Opacity = 0.18 }
            };
            bool down = false;
            b.MouseEnter += delegate { b.Background = lit; };
            b.MouseLeave += delegate { b.Background = idle; down = false; };
            b.MouseLeftButtonDown += (s, e) => { e.Handled = true; down = true; Gestures.NotifyInteracting(); };
            b.MouseLeftButtonUp += (s, e) => { e.Handled = true; if (down) act(); down = false; };
            b.MouseRightButtonUp += (s, e) => { e.Handled = true; };
            return b;
        }

        public void Breeze(Random rnd)
        {
            double delay = rnd.NextDouble() * 0.35;
            double amount = 1.6 + rnd.NextDouble() * 1.8;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(delay) };
            t.Tick += delegate { t.Stop(); Nudge(amount); };
            t.Start();
        }

        void Nudge(double degrees)
        {
            nudgeFrom = swing.Value;
            nudgeTo = degrees;
            nudgeT = 0;
        }

        /// One frame of motion.
        public void Step(double dt, double targetX, double lineWidth)
        {
            UpdateThumb();
            ActualWidthOfLine = lineWidth;
            bool hover = line.HoverId == Item.Id && line.DraggingId == Guid.Empty;
            bool pressed = line.PressedId == Item.Id;
            bool copied = line.CopiedId == Item.Id;
            bool dragging = line.DraggingId == Item.Id;

            if (wasFlying && !Item.Flying) Nudge(2.2);   // landing: just a small sway from rest
            wasFlying = Item.Flying;
            if (copied && !wasCopied) Nudge(3);
            wasCopied = copied;

            X.Target = targetX;
            X.Step(dt);
            drop.Step(dt);

            if (nudgeT >= 0)
            {
                nudgeT += dt / 0.3;
                if (nudgeT >= 1)
                {
                    nudgeT = -1;
                    swing.Value = nudgeTo;
                    swing.Velocity = 0;
                    swing.SetStiffness(38, 2.4);
                }
                else swing.Value = nudgeFrom + (nudgeTo - nudgeFrom) * Ease.OutQuad(nudgeT);
            }
            else swing.Step(dt);

            // Holding presses the photo in slowly, so a long press feels like
            // it is building up to something.
            if (pressed) press.SetResponse(0.45, 1); else press.SetResponse(0.3, 0.6);
            press.Target = pressed ? 0.95 : (hover ? 1.035 : 1);
            press.Step(dt);

            double targetOpacity = Item.Falling || Item.Flying ? 0 : 1;
            if (Item.Falling) opacity = 0;   // the fall is drawn over the screen by Flight
            else if (!arrived) opacity = 0;
            else opacity += (targetOpacity - opacity) * Math.Min(1, dt / 0.16 * 2);
            if (!Item.Flying && !Item.Falling && !arrived) arrived = true;
            if (arrived && !Item.Flying && drop.Target == 0 && drop.Value < -0.5)
                opacity = Math.Max(0, Math.Min(1, 1 + drop.Value / 46));

            crossOpacity += ((hover ? 1 : 0) - crossOpacity) * Math.Min(1, dt * 12);
            copiedOpacity += ((copied ? 1 : 0) - copiedOpacity) * Math.Min(1, dt * 12);
            // The buttons give way to the "Copied" label, which shows in their place.
            bool showActions = hover && !copied && !pressed && arrived && !Item.Flying;
            actionsOpacity += ((showActions ? 1 : 0) - actionsOpacity) * Math.Min(1, dt * 14);
            if (hover != wasHover)
            {
                shadow.Opacity = hover ? 0.26 : 0.18;
                shadow.BlurRadius = hover ? 28 : 20;
                shadow.ShadowDepth = hover ? 8 : 5;
                wasHover = hover;
            }

            double ropeY = Layout.RopeY(X.Value, lineWidth);
            SetLeft(this, X.Value - Layout.CardWidth / 2);
            SetTop(this, ropeY - Layout.PinAbove + drop.Value);
            rotate.Angle = swing.Value + Item.Tilt;
            scale.ScaleX = scale.ScaleY = press.Value;
            Opacity = opacity;
            frameHost.Opacity = dragging ? 0.45 : 1;
            cross.Opacity = dragging ? 0 : crossOpacity;
            cross.RenderTransform = new ScaleTransform(0.6 + 0.4 * crossOpacity, 0.6 + 0.4 * crossOpacity, 10, 10);
            actions.Opacity = dragging ? 0 : actionsOpacity;
            actions.IsHitTestVisible = showActions;
            actions.RenderTransform = new TranslateTransform(0, -4 * (1 - actionsOpacity));
            if (swatches != null)
            {
                swatches.Opacity = actions.Opacity;
                swatches.IsHitTestVisible = showActions;
                swatches.RenderTransform = actions.RenderTransform;
                int color = Notes.ColorOf(Item.Path);
                if (color != shownColor)
                {
                    shownColor = color;
                    for (int i = 0; i < swatchDots.Length; i++)
                        swatchDots[i].Children[1].Visibility = i == color ? Visibility.Visible : Visibility.Hidden;
                }
            }
            copiedLabel.Opacity = copiedOpacity;
            copiedLabel.RenderTransform = new TranslateTransform(0, -4 * (1 - copiedOpacity));
        }

        public bool InCross(Point p)
        {
            return p.X >= 0 && p.Y >= 0 && p.X < 26 && p.Y < 26;
        }

    }
}
