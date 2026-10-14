using System;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Pegline
{
    /// Sticky notes: copied text hung on the line. Each note is a plain .txt
    /// file in Pegline's own folder, so it survives a restart, opens and edits
    /// in Notepad, and drags into any app like a screenshot does. On the line
    /// it is drawn as a small sheet of paper, rendered to an image so the
    /// cards, the drag ghost and the falling animation treat it like a photo.
    static class Notes
    {
        public static readonly string Folder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegline", "Notes");

        /// Paper, edge and ink for each colour: yellow, blue, pink, grey.
        static readonly Color[,] Palette =
        {
            { Color.FromRgb(255, 244, 184), Color.FromRgb(246, 226, 136), Color.FromRgb(74, 59, 18) },
            { Color.FromRgb(219, 241, 255), Color.FromRgb(186, 225, 249), Color.FromRgb(18, 56, 74) },
            { Color.FromRgb(255, 225, 232), Color.FromRgb(248, 196, 210), Color.FromRgb(74, 24, 40) },
            { Color.FromRgb(242, 242, 242), Color.FromRgb(218, 218, 218), Color.FromRgb(40, 40, 40) },
        };

        public const int ColorCount = 4;

        /// The swatch shown for a colour, a little deeper than the paper.
        public static Color Swatch(int color)
        {
            return Palette[Clamp(color), 1];
        }

        static int Clamp(int color) { return color < 0 || color >= ColorCount ? 0 : color; }

        public static bool IsNote(string path)
        {
            try
            {
                string dir = System.IO.Path.GetFullPath(Folder).TrimEnd('\\') + "\\";
                return System.IO.Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase)
                    && path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// Writes a new note and returns its file.
        public static string Create(string text)
        {
            Directory.CreateDirectory(Folder);
            string name = Loc.L("Note", "Nota", "便签") + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".txt";
            string path = Line.UniquePath(Folder, name);
            // With a BOM, so every editor reads Chinese text right.
            File.WriteAllText(path, text, new UTF8Encoding(true));
            return path;
        }

        public static string Read(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8, true)) return r.ReadToEnd();
            }
            catch (Exception) { return null; }
        }

        public static int ColorOf(string path)
        {
            int c;
            return int.TryParse(Settings.GetString(ColorKey(path)), out c) ? Clamp(c) : 0;
        }

        public static void SetColor(string path, int color)
        {
            Settings.SetString(ColorKey(path), Clamp(color).ToString());
        }

        static string ColorKey(string path) { return "noteColor:" + System.IO.Path.GetFileName(path); }

        /// The note as drawn on its card, or null if the file cannot be read.
        public static BitmapSource Thumb(string path)
        {
            string text = Read(path);
            return text == null ? null : Render(text, ColorOf(path));
        }

        /// A sheet of paper, 136 by 88 points, drawn at three times that so it
        /// stays sharp on any screen. Short text such as an order number sits
        /// large in the middle; longer text wraps and ends in an ellipsis.
        public static BitmapSource Render(string text, int color)
        {
            const double W = 136, H = 88, K = 3, Fold = 13;
            color = Clamp(color);
            var paper = Palette[color, 0];
            var edge = Palette[color, 1];
            var ink = new SolidColorBrush(Palette[color, 2]);

            text = (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ').Trim();
            bool brief = text.Length <= 24 && text.IndexOf('\n') < 0;

            var root = new Grid { Width = W, Height = H };
            root.Children.Add(new Rectangle { Fill = new LinearGradientBrush(paper, Color.FromRgb(
                (byte)((paper.R + edge.R) / 2), (byte)((paper.G + edge.G) / 2), (byte)((paper.B + edge.B) / 2)), 90) });
            // A dog-eared corner, bottom right.
            var corner = new Polygon
            {
                Points = new PointCollection { new Point(W - Fold, H), new Point(W, H - Fold), new Point(W - Fold, H - Fold) },
                Fill = new SolidColorBrush(edge), Stroke = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), StrokeThickness = 0.5
            };
            // The shade where the corner lifts off the sheet.
            root.Children.Add(new Polygon
            {
                Points = new PointCollection { new Point(W - Fold, H), new Point(W, H - Fold), new Point(W, H) },
                Fill = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0))
            });
            root.Children.Add(corner);
            var block = new TextBlock
            {
                Text = text, Foreground = ink, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                FontFamily = new FontFamily("KaiTi, STKaiti, Segoe Print, Microsoft YaHei UI"),
            };
            if (brief)
            {
                block.FontSize = text.Length <= 12 ? 16 : 13;
                block.TextAlignment = TextAlignment.Center;
                block.VerticalAlignment = VerticalAlignment.Center;
                block.Margin = new Thickness(8, 4, 8, 6);
            }
            else
            {
                block.FontSize = 11.5;
                block.LineHeight = 14.5;
                block.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
                block.Margin = new Thickness(9, 7, 11, 9);
            }
            root.Children.Add(block);
            root.Measure(new Size(W, H));
            root.Arrange(new Rect(0, 0, W, H));
            root.UpdateLayout();
            var bmp = new RenderTargetBitmap((int)(W * K), (int)(H * K), 96 * K, 96 * K, PixelFormats.Pbgra32);
            bmp.Render(root);
            bmp.Freeze();
            return bmp;
        }
    }
}
