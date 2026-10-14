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
        /// Changes only when a duplicate capture is folded into its twin.
        public string Path;
        public BitmapSource Thumb;
        public DateTime LastWrite;
        /// Every photo hangs a little crooked, like on a real line.
        public readonly double Tilt = rnd.NextDouble() * 5 - 2.5;
        public bool Falling;
        /// Still flying in from where it was captured; the card waits hidden.
        public bool Flying;
        /// Seconds in a row the file has been missing, as the timer saw it.
        public int MissingTicks;

        /// A sticky note rather than a picture.
        public bool IsNote { get { return Notes.IsNote(Path); } }

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
            editTimer.Tick += delegate { PruneSlowly(); ReloadEdited(); };
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
            var thumb = LoadThumb(path);
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

        /// The screenshots caught from the clipboard, on the line or not.
        public static string[] InboxFiles()
        {
            try { return Directory.Exists(InboxFolder) ? Directory.GetFiles(InboxFolder) : new string[0]; }
            catch (Exception) { return new string[0]; }
        }

        /// Whether EmptyInbox is still sending files to the Recycle Bin.
        public bool Emptying;

        /// Sends every caught screenshot to the Recycle Bin and takes those
        /// photos off the line. Files hanging from elsewhere stay put. Each
        /// file takes the shell a moment, so it runs off the UI thread; done
        /// gets the number of files that could not be removed.
        public void EmptyInbox(Action<int> done)
        {
            if (Emptying) return;
            var files = InboxFiles();
            if (files.Length == 0) { if (done != null) done(0); return; }
            Emptying = true;
            var inbox = Items.Where(i => !i.Falling && IsInInbox(i.Id)).ToList();
            for (int n = 0; n < inbox.Count; n++)
            {
                var item = inbox[n];
                if (n == 0) Drop(item.Id, true);
                else After(0.06 * n, delegate { Drop(item.Id, true); });
            }
            if (SoundOn) Sounds.Rustle();
            var ui = Dispatcher.CurrentDispatcher;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                foreach (var f in files)
                {
                    // A file open elsewhere gets the shell's own retry or skip dialog.
                    try { FileSystem.DeleteFile(f, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin, UICancelOption.DoNothing); }
                    catch (Exception) { }
                }
                int left = files.Count(File.Exists);
                ui.BeginInvoke(new Action(delegate
                {
                    Emptying = false;
                    Prune();
                    if (done != null) done(left);
                }));
            });
        }

        /// Photos whose file was deleted or moved away fall off by themselves.
        public void Prune()
        {
            foreach (var item in Items.Where(i => !i.Falling && !File.Exists(i.Path)).ToList())
                Drop(item.Id, true);
        }

        /// The timer's version of Prune, for files outside the watched folder.
        /// An editor that saves by writing a temporary file and renaming it
        /// over the original leaves a moment with no file; only a file still
        /// missing a second later has really gone.
        void PruneSlowly()
        {
            foreach (var item in Items.Where(i => !i.Falling).ToList())
            {
                if (File.Exists(item.Path)) item.MissingTicks = 0;
                else if (++item.MissingTicks >= 2) Drop(item.Id, true);
            }
        }

        // Actions on one photo

        static BitmapSource LoadThumb(string path)
        {
            return Notes.IsNote(path) ? Notes.Thumb(path) : Thumbnails.Load(path, 480);
        }

        /// A note in another colour: saved with the note, redrawn at once.
        public void SetNoteColor(Guid id, int color)
        {
            var item = Find(id);
            if (item == null || !item.IsNote) return;
            Notes.SetColor(item.Path, color);
            var thumb = Notes.Thumb(item.Path);
            if (thumb != null) item.Thumb = thumb;
            Notify();
        }

        public void Copy(Guid id)
        {
            var item = Find(id);
            if (item == null) return;
            try
            {
                var data = new DataObject();
                if (item.IsNote)
                {
                    // A note copies as its text, ready to paste anywhere.
                    string text = Notes.Read(item.Path);
                    if (text == null) throw new IOException();
                    data.SetText(text, TextDataFormat.UnicodeText);
                }
                else
                {
                    var full = Thumbnails.Load(item.Path, 0);
                    if (full != null) data.SetImage(full);
                    var png = PngData(item.Path, full);
                    if (png != null) data.SetData("PNG", new MemoryStream(png));
                    data.SetFileDropList(new StringCollection { item.Path });
                }
                ClipboardCatcher.IgnoreNext();
                PutOnClipboard(data);
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

        /// Puts the copy on the clipboard, then flushes it so it outlives
        /// Pegline. Clipboard history and other watchers open the clipboard
        /// the moment it changes, so the flush often loses that race; the
        /// copy is already there by then, so the flush is simply tried again
        /// a little later instead of setting the clipboard again, which would
        /// only start the race over.
        static void PutOnClipboard(DataObject data)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, false);
                    break;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    if (attempt >= 9) throw;
                    System.Threading.Thread.Sleep(50);
                }
            }
            FlushLater(data, 0);
        }

        static void FlushLater(DataObject data, int attempt)
        {
            try
            {
                if (Clipboard.IsCurrent(data)) Clipboard.Flush();
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                if (attempt < 10) After(0.1 * (attempt + 1), delegate { FlushLater(data, attempt + 1); });
            }
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
            }
            catch (Exception ex)
            {
                // Removing the card must still work when Windows cannot recycle
                // an encrypted file. Preserve the file, never permanently delete it.
                Drop(id, true);
                MessageBox.Show(Loc.L(
                    "Taken off the line. Windows could not move the saved copy to the Recycle Bin; the copy is still here:",
                    "Retirada de la cuerda. Windows no pudo enviar la copia a la papelera; se conserva aquí:",
                    "已从晾绳取下。Windows 无法把保存的副本移入回收站，副本仍保留在：")
                    + Environment.NewLine + item.Path + Environment.NewLine + ex.Message,
                    "Pegline", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Drop(id, true);
            if (SoundOn) Sounds.Rustle();
        }

        /// Whether the file lives in Pegline's own folder. Those are discarded
        /// to the Recycle Bin, or the folder would fill up with forgotten
        /// screenshots. Files anywhere else stay where they are.
        public bool IsInInbox(Guid id)
        {
            var item = Find(id);
            return item != null && InInbox(item.Path);
        }

        public static bool InInbox(string path)
        {
            try
            {
                string dir = System.IO.Path.GetFullPath(InboxFolder).TrimEnd('\\') + "\\";
                return System.IO.Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception) { return false; }
        }

        /// The photo hanging from one file now stands for another with the
        /// same picture: the copy Pegline caught gives way to the one Windows
        /// saved, so a capture is kept once.
        public void Repoint(string from, string to)
        {
            var item = Items.FirstOrDefault(i => !i.Falling && SamePath(i.Path, from));
            if (item == null) return;
            item.Path = to;
            item.LastWrite = Pegged.SafeWriteTime(to);
            Save();
        }

        /// The corner cross and "Take down" both end up here.
        public void Discard(Guid id)
        {
            if (IsOwn(id)) Trash(id); else Drop(id);
        }

        /// Files Pegline made itself, caught screenshots and notes: discarding
        /// them sends them to the Recycle Bin instead of leaving them behind.
        public bool IsOwn(Guid id)
        {
            var item = Find(id);
            return item != null && (InInbox(item.Path) || item.IsNote);
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
            if (item.IsNote)
            {
                // Saving in Notepad redraws the note on the line.
                try { Process.Start("notepad.exe", "\"" + item.Path + "\""); }
                catch (Exception) { Open(id); }
                return;
            }
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
                var thumb = LoadThumb(item.Path);
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

        /// Whether two image files hold the same picture. The clipboard copy
        /// and the file Windows saves can differ in encoding and alpha, so
        /// only colour counts, with a little slack for rounding; anything
        /// beyond 0.5% of pixels clearly different is another picture.
        public static bool SamePicture(string a, string b)
        {
            try
            {
                byte[] pa, pb;
                int wa, ha, wb, hb;
                if (!Pixels(a, out pa, out wa, out ha) || !Pixels(b, out pb, out wb, out hb)) return false;
                if (wa != wb || ha != hb) return false;
                long allowed = (long)wa * ha / 200, differing = 0;
                for (int i = 0; i < pa.Length; i += 4)
                {
                    if (Math.Abs(pa[i] - pb[i]) > 8 || Math.Abs(pa[i + 1] - pb[i + 1]) > 8 || Math.Abs(pa[i + 2] - pb[i + 2]) > 8)
                        if (++differing > allowed) return false;
                }
                return true;
            }
            catch (Exception) { return false; }
        }

        static bool Pixels(string path, out byte[] px, out int w, out int h)
        {
            px = null; w = h = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var frame = BitmapFrame.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var bgr = new FormatConvertedBitmap(frame, System.Windows.Media.PixelFormats.Bgr32, null, 0);
                w = bgr.PixelWidth; h = bgr.PixelHeight;
                px = new byte[w * h * 4];
                bgr.CopyPixels(px, w * 4, 0);
            }
            return true;
        }

        static Size PixelSize(byte[] bytes)
        {
            var frame = BitmapFrame.Create(new MemoryStream(bytes), BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            return new Size(frame.PixelWidth, frame.PixelHeight);
        }
    }
}
