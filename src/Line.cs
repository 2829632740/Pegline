using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Pegline
{
    /// One screenshot hanging on the line.
    class Pegged
    {
        static readonly Random rnd = new Random();

        public readonly Guid Id = Guid.NewGuid();
        public readonly string Path;
        public BitmapSource Thumb;
        public DateTime LastWrite;
        /// Every photo hangs a little crooked, like on a real line.
        public readonly double Tilt = rnd.NextDouble() * 5 - 2.5;
        public bool Falling;
        /// Still flying in from where it was captured; the card waits hidden.
        public bool Flying;

        public Pegged(string path, BitmapSource thumb)
        {
            Path = path;
            Thumb = thumb;
            LastWrite = SafeWriteTime(path);
        }

        public static DateTime SafeWriteTime(string path)
        {
            try { return File.GetLastWriteTimeUtc(path); }
            catch (Exception) { return DateTime.MinValue; }
        }
    }

    /// The line itself: what hangs on it and what you can do with each item.
    /// The files never move. The line is only a view onto them.
    class Line
    {
        public readonly List<Pegged> Items = new List<Pegged>();
        public Guid CopiedId, DraggingId, PressedId, HoverId;
        /// Whether the line has slid down into view.
        public bool Revealed;
        public int MaxItems = 8;

        /// Anything changed: items added, removed, landed or redrawn.
        public event Action Changed;
        /// Every so often a little wind moves the line.
        public event Action Gust;
        /// Called just before a photo starts falling, so the fall can be drawn
        /// over the whole screen.
        public Action<Pegged> OnFall;

        readonly DispatcherTimer gustTimer = new DispatcherTimer();
        readonly DispatcherTimer editTimer = new DispatcherTimer();
        readonly Random rnd = new Random();

        public static readonly string InboxFolder = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pegline", "Screenshots");

        public Line()
        {
            Restore();
            gustTimer.Tick += delegate
            {
                if (Items.Count > 0 && DraggingId == Guid.Empty && Gust != null) Gust();
                gustTimer.Interval = TimeSpan.FromSeconds(7 + rnd.NextDouble() * 9);
            };
            gustTimer.Interval = TimeSpan.FromSeconds(7 + rnd.NextDouble() * 9);
            gustTimer.Start();

            // An image edited in Paint is saved over the original: the photo on
            // the line shows the new version.
            editTimer.Interval = TimeSpan.FromSeconds(1);
            editTimer.Tick += delegate { ReloadEdited(); };
            editTimer.Start();
        }

        public bool SoundOn
        {
            get { return !Settings.Get("soundOff"); }
            set { Settings.Set("soundOff", !value); }
        }

        public int LiveCount { get { return Items.Count(i => !i.Falling); } }

        public Pegged Find(Guid id) { return Items.FirstOrDefault(i => i.Id == id); }

        void Notify() { if (Changed != null) Changed(); }

        static void After(double seconds, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += delegate { t.Stop(); action(); };
            t.Start();
        }

        // Hanging and dropping

        public Guid Hang(string path, bool quietly = false, bool flying = false)
        {
            if (Items.Any(i => SamePath(i.Path, path) && !i.Falling)) return Guid.Empty;
            var thumb = Thumbnails.Load(path, 480);
            if (thumb == null) return Guid.Empty;
            var item = new Pegged(path, thumb) { Flying = flying };
            Items.Add(item);
            // A full line lets the oldest photo fall off the far end.
            while (LiveCount > MaxItems)
            {
                var oldest = Items.First(i => !i.Falling);
                Drop(oldest.Id, true);
            }
            Save();
            if (!quietly && SoundOn) Sounds.Tink();
            Notify();
            return item.Id;
        }

        /// The capture has reached the line: the real card takes over.
        public void Land(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            item.Flying = false;
            Notify();
        }

        public void Drop(Guid id, bool quietly = false)
        {
            var item = Find(id);
            if (item == null || item.Falling) return;
            if (OnFall != null) OnFall(item);
            item.Falling = true;
            Save();
            if (!quietly && SoundOn) Sounds.Pop();
            Notify();
            After(0.6, delegate
            {
                Items.Remove(item);
                Notify();
            });
        }

        public void Clear()
        {
            var live = Items.Where(i => !i.Falling).ToList();
            for (int n = 0; n < live.Count; n++)
            {
                var item = live[n];
                bool quiet = n > 0;
                if (n == 0) Drop(item.Id, quiet);
                else After(0.06 * n, delegate { Drop(item.Id, quiet); });
            }
        }

        /// Photos whose file was deleted or moved away fall off by themselves.
        public void Prune()
        {
            foreach (var item in Items.Where(i => !i.Falling && !File.Exists(i.Path)).ToList())
                Drop(item.Id, true);
        }

        // Actions on one photo

        public void Copy(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            try
            {
                var data = new DataObject();
                var full = Thumbnails.Load(item.Path, 0);
                if (full != null) data.SetImage(full);
                var png = PngData(item.Path, full);
                if (png != null) data.SetData("PNG", new MemoryStream(png));
                data.SetFileDropList(new StringCollection { item.Path });
                ClipboardCatcher.IgnoreNext();
                Clipboard.SetDataObject(data, true);
            }
            catch (Exception)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            CopiedId = id;
            Notify();
            After(1.2, delegate
            {
                if (CopiedId == id) { CopiedId = Guid.Empty; Notify(); }
            });
        }

        public void Open(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true }); }
            catch (Exception) { System.Media.SystemSounds.Beep.Play(); }
        }

        /// Moves the file to the Recycle Bin and takes the photo off the line.
        public void Trash(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            try
            {
                if (File.Exists(item.Path))
                    FileSystem.DeleteFile(item.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                if (SoundOn) Sounds.Rustle();
                Drop(id, true);
            }
            catch (Exception)
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        /// Whether the file lives in Pegline's own folder. Those are discarded
        /// to the Recycle Bin, or the folder would fill up with forgotten
        /// screenshots. Files anywhere else stay where they are.
        public bool IsInInbox(Guid id)
        {
            var item = Find(id);
            if (item == null) return false;
            string dir = System.IO.Path.GetFullPath(InboxFolder).TrimEnd('\\') + "\\";
            return System.IO.Path.GetFullPath(item.Path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        /// The corner cross and "Take down" both end up here.
        public void Discard(Guid id)
        {
            if (IsInInbox(id)) Trash(id); else Drop(id);
        }

        /// Keep a caught screenshot by moving it to the Desktop.
        public void SaveToDesktop(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            try
            {
                File.Move(item.Path, UniquePath(desktop, System.IO.Path.GetFileName(item.Path)));
                Drop(id, true);
            }
            catch (Exception)
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }

        public static string UniquePath(string folder, string name)
        {
            string stem = System.IO.Path.GetFileNameWithoutExtension(name);
            string ext = System.IO.Path.GetExtension(name);
            string candidate = System.IO.Path.Combine(folder, name);
            for (int n = 2; File.Exists(candidate); n++)
                candidate = System.IO.Path.Combine(folder, stem + " " + n + ext);
            return candidate;
        }

        /// Long press: Windows has no system Markup sheet, so the photo opens
        /// in Paint. Saving there writes over the file and the line refreshes.
        public void Markup(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            try { Process.Start("mspaint.exe", "\"" + item.Path + "\""); }
            catch (Exception)
            {
                try { Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true, Verb = "edit" }); }
                catch (Exception) { Open(id); }
            }
        }

        public void Reveal(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            Process.Start("explorer.exe", "/select,\"" + item.Path + "\"");
        }

        void ReloadEdited()
        {
            bool changed = false;
            foreach (var item in Items.Where(i => !i.Falling))
            {
                var stamp = Pegged.SafeWriteTime(item.Path);
                if (stamp == item.LastWrite || stamp == DateTime.MinValue) continue;
                var thumb = Thumbnails.Load(item.Path, 480);
                if (thumb == null) continue;   // still being written, try next second
                item.LastWrite = stamp;
                item.Thumb = thumb;
                changed = true;
            }
            if (changed) Notify();
        }

        // Persistence

        void Save()
        {
            Settings.Pegged = Items.Where(i => !i.Falling).Select(i => i.Path).ToArray();
        }

        void Restore()
        {
            // The line's real capacity is only known once it is on a screen;
            // bring everything back and let the next capture trim it.
            int max = MaxItems;
            MaxItems = int.MaxValue;
            foreach (var path in Settings.Pegged)
                if (path.Length > 0 && File.Exists(path)) Hang(path, true);
            MaxItems = max;
        }

        static bool SamePath(string a, string b)
        {
            return string.Equals(System.IO.Path.GetFullPath(a), System.IO.Path.GetFullPath(b),
                                 StringComparison.OrdinalIgnoreCase);
        }

        static byte[] PngData(string path, BitmapSource full)
        {
            try
            {
                if (System.IO.Path.GetExtension(path).Equals(".png", StringComparison.OrdinalIgnoreCase))
                    return File.ReadAllBytes(path);
                if (full == null) return null;
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(full));
                var ms = new MemoryStream();
                enc.Save(ms);
                return ms.ToArray();
            }
            catch (Exception) { return null; }
        }
    }

    static class Thumbnails
    {
        /// Decodes an image without keeping the file open, so it can still be
        /// moved, dragged into a folder or deleted while it hangs. A maxPixels
        /// of 0 loads it at full size.
        public static BitmapSource Load(string path, int maxPixels)
        {
            try
            {
                byte[] bytes;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length == 0) return null;
                    bytes = new byte[fs.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = fs.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                }
                var size = PixelSize(bytes);
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                img.StreamSource = new MemoryStream(bytes);
                if (maxPixels > 0 && (size.Width > maxPixels || size.Height > maxPixels))
                {
                    if (size.Width >= size.Height) img.DecodePixelWidth = maxPixels;
                    else img.DecodePixelHeight = maxPixels;
                }
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch (Exception) { return null; }
        }

        public static Size PixelSizeOf(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    var frame = BitmapFrame.Create(fs, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    return new Size(frame.PixelWidth, frame.PixelHeight);
                }
            }
            catch (Exception) { return new Size(0, 0); }
        }

        static Size PixelSize(byte[] bytes)
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return new Size(frame.PixelWidth, frame.PixelHeight);
        }
    }
}
