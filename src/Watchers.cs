using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Pegline
{
    /// Watches the folder Windows saves screenshots to and reports new ones.
    /// Pegline never takes screenshots itself: you keep Win+PrtScn, the
    /// Snipping Tool or anything else, and the line just picks them up.
    class ScreenshotWatcher
    {
        public readonly string Folder;
        readonly Action<string> onNew;
        readonly Action onChange;
        readonly HashSet<string> known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer pending = new DispatcherTimer();
        FileSystemWatcher fsw;
        /// Anything created after the app launched counts as new, even if it
        /// landed before the watcher was ready.
        readonly DateTime launch = DateTime.UtcNow;

        static readonly HashSet<string> imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff" };
        static readonly HashSet<string> excluded = new HashSet<string> { "thumbs.db", "desktop.ini", ".ds_store" };

        public ScreenshotWatcher(string folder, Action<string> onNew, Action onChange)
        {
            Folder = folder;
            this.onNew = onNew;
            this.onChange = onChange;
            // A screenshot is written to a temp name and renamed, or written
            // in several chunks; give it a moment.
            pending.Interval = TimeSpan.FromSeconds(0.25);
            pending.Tick += delegate { pending.Stop(); Scan(); };
        }

        DispatcherTimer waitForFolder;

        public void Start()
        {
            // Windows only creates the Screenshots folder on the first
            // Win+PrtScn. Until then, look for it now and then.
            if (!Directory.Exists(Folder))
            {
                waitForFolder = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                waitForFolder.Tick += delegate
                {
                    if (!Directory.Exists(Folder)) return;
                    waitForFolder.Stop();
                    waitForFolder = null;
                    Watch();
                };
                waitForFolder.Start();
                return;
            }
            foreach (var f in Listing())
                if (CreationTime(f) < launch) known.Add(f);
            Watch();
        }

        void Watch()
        {
            Scan();
            try
            {
                fsw = new FileSystemWatcher(Folder) { IncludeSubdirectories = false, NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size };
                FileSystemEventHandler h = delegate { Schedule(); };
                fsw.Created += h;
                fsw.Changed += h;
                fsw.Deleted += h;
                fsw.Renamed += delegate { Schedule(); };
                fsw.EnableRaisingEvents = true;
            }
            catch (Exception) { }
        }

        public void Stop()
        {
            pending.Stop();
            if (waitForFolder != null) { waitForFolder.Stop(); waitForFolder = null; }
            if (fsw != null) { fsw.Dispose(); fsw = null; }
        }

        void Schedule()
        {
            Application.Current.Dispatcher.BeginInvoke(new Action(delegate
            {
                pending.Stop();
                pending.Start();
            }));
        }

        void Scan()
        {
            var files = Listing();
            bool retry = false;
            foreach (var f in files)
            {
                if (known.Contains(f)) continue;
                // Still being written: try again on the next scan.
                if (!Readable(f)) { retry = true; continue; }
                known.Add(f);
                onNew(f);
            }
            known.IntersectWith(files);
            onChange();
            if (retry) { pending.Stop(); pending.Start(); }
        }

        List<string> Listing()
        {
            try
            {
                return Directory.GetFiles(Folder)
                    .Where(f => imageExtensions.Contains(Path.GetExtension(f))
                                && !excluded.Contains(Path.GetFileName(f).ToLowerInvariant())
                                && (File.GetAttributes(f) & FileAttributes.Hidden) == 0)
                    .OrderBy(CreationTime).ToList();
            }
            catch (Exception) { return new List<string>(); }
        }

        static DateTime CreationTime(string f)
        {
            try { return File.GetCreationTimeUtc(f); }
            catch (Exception) { return DateTime.MinValue; }
        }

        static bool Readable(string f)
        {
            try
            {
                using (var s = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read)) return s.Length > 0;
            }
            catch (Exception) { return false; }
        }
    }

    /// Catching mode: the Snipping Tool (Win+Shift+S) and PrtScn put the
    /// capture on the clipboard. Pegline saves each one into its own folder
    /// and hangs it at once, so it never needs saving by hand and the
    /// Desktop only gets what you keep. Also listens for the global shortcuts.
    class ClipboardCatcher
    {
        static DateTime ignoreUntil = DateTime.MinValue;

        readonly HwndSource window;
        readonly Action<string, Rect?, bool> onCapture;
        readonly Action onHotKey;
        readonly DispatcherTimer settle = new DispatcherTimer();
        IntPtr foregroundAtCapture;
        public bool Enabled;
        public bool HotKeyRegistered;
        const int HotKeyId = 0x5045;
        /// The shortcuts you can choose: one starts a screenshot, one hangs
        /// the copied text as a note.
        public const int SnipKeyId = 0x5046, NoteKeyId = 0x5047;
        readonly Dictionary<int, Action> keyActions = new Dictionary<int, Action>();
        readonly Dictionary<int, Shortcut> keys = new Dictionary<int, Shortcut>();
        readonly HashSet<int> held = new HashSet<int>();
        /// A screenshot you asked for is caught even with catching off.
        DateTime armedUntil = DateTime.MinValue;
        bool editArmed;

        /// Copying a photo off the line puts an image on the clipboard too;
        /// that one must not be caught again.
        public static void IgnoreNext()
        {
            ignoreUntil = DateTime.UtcNow.AddSeconds(1);
        }

        public ClipboardCatcher(Action<string, Rect?, bool> onCapture, Action onHotKey, Action onSnipKey, Action onNoteKey)
        {
            this.onCapture = onCapture;
            this.onHotKey = onHotKey;
            keyActions[SnipKeyId] = onSnipKey;
            keyActions[NoteKeyId] = onNoteKey;
            var p = new HwndSourceParameters("PeglineMessages") { ParentWindow = Native.HWND_MESSAGE, Width = 0, Height = 0 };
            window = new HwndSource(p);
            window.AddHook(WndProc);
            Native.AddClipboardFormatListener(window.Handle);
            // Ctrl+Alt+T shows or hides the line.
            HotKeyRegistered = Native.RegisterHotKey(window.Handle, HotKeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)'T');
            // The tool may write several formats in a row; read once it settles.
            settle.Interval = TimeSpan.FromSeconds(0.15);
            settle.Tick += delegate { settle.Stop(); Read(); };
        }

        public void Dispose()
        {
            Native.RemoveClipboardFormatListener(window.Handle);
            Native.UnregisterHotKey(window.Handle, HotKeyId);
            foreach (int id in keyActions.Keys) Native.UnregisterHotKey(window.Handle, id);
            window.Dispose();
        }

        /// The shortcut chosen for one of the keys, null for none.
        public Shortcut KeyOf(int id)
        {
            Shortcut key;
            return keys.TryGetValue(id, out key) ? key : null;
        }

        /// Whether that shortcut is actually ours, not held by another app.
        public bool Holds(int id) { return held.Contains(id); }

        /// Takes over a new shortcut, or none. False when another app already
        /// holds that combination.
        public bool SetKey(int id, Shortcut key)
        {
            Native.UnregisterHotKey(window.Handle, id);
            keys[id] = key;
            held.Remove(id);
            if (key == null) return true;
            if (!Native.RegisterHotKey(window.Handle, id, key.Mods | Native.MOD_NOREPEAT, key.Vk)) return false;
            held.Add(id);
            return true;
        }

        /// Lets go of a shortcut while a new one is being pressed, so the old
        /// one reaches the dialog instead of doing its job.
        public void SuspendKey(int id)
        {
            Native.UnregisterHotKey(window.Handle, id);
            held.Remove(id);
        }

        /// The dedicated Pegline shortcut is deliberately different from
        /// ordinary clipboard capture: it opens the in-app markup sheet first.
        public void ArmForEditor()
        {
            armedUntil = DateTime.UtcNow.AddSeconds(60);
            editArmed = true;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr w, IntPtr l, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && w.ToInt32() == HotKeyId)
            {
                onHotKey();
                handled = true;
            }
            else if (msg == Native.WM_HOTKEY && keyActions.ContainsKey(w.ToInt32()))
            {
                keyActions[w.ToInt32()]();
                handled = true;
            }
            else if (msg == Native.WM_CLIPBOARDUPDATE && (Enabled || DateTime.UtcNow < armedUntil)
                     && DateTime.UtcNow > ignoreUntil && !OwnedByUs())
            {
                Trace.Write("clipboard update");
                foregroundAtCapture = Native.GetForegroundWindow();
                settle.Stop();
                settle.Start();
            }
            return IntPtr.Zero;
        }

        ulong lastHash;
        DateTime lastHashAt;

        /// A 32 bit bitmap read from the clipboard often carries an alpha
        /// channel that is all zero, which would hang an invisible photo.
        static BitmapSource Opaque(BitmapSource image)
        {
            if (image.Format != System.Windows.Media.PixelFormats.Bgra32 && image.Format != System.Windows.Media.PixelFormats.Pbgra32)
                return image;
            int stride = image.PixelWidth * 4;
            var px = new byte[stride * image.PixelHeight];
            image.CopyPixels(px, stride, 0);
            for (int i = 3; i < px.Length; i += 4)
                if (px[i] != 0) return image;
            var opaque = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            opaque.Freeze();
            return opaque;
        }

        static ulong Hash(BitmapSource image)
        {
            var bgr = new FormatConvertedBitmap(image, System.Windows.Media.PixelFormats.Bgr32, null, 0);
            int stride = image.PixelWidth * 4;
            var px = new byte[stride * image.PixelHeight];
            bgr.CopyPixels(px, stride, 0);
            ulong h = 14695981039346656037UL ^ (ulong)(image.PixelWidth * 31 + image.PixelHeight);
            for (int i = 0; i < px.Length; i += 3)
            {
                h ^= px[i];
                h *= 1099511628211UL;
            }
            return h;
        }

        static bool OwnedByUs()
        {
            IntPtr owner = Native.GetClipboardOwner();
            if (owner == IntPtr.Zero) return false;
            uint pid;
            Native.GetWindowThreadProcessId(owner, out pid);
            return pid == (uint)Process.GetCurrentProcess().Id;
        }

        void Read()
        {
            BitmapSource image = null;
            for (int attempt = 0; attempt < 4 && image == null; attempt++)
            {
                try
                {
                    var data = Clipboard.GetDataObject();
                    if (data == null) return;
                    // Only a bare image is a screenshot. A picture copied in a
                    // browser or a document comes with HTML, text or a file.
                    if (data.GetDataPresent(DataFormats.Html) || data.GetDataPresent(DataFormats.UnicodeText)
                        || data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Rtf))
                        return;
                    if (data.GetDataPresent("PNG"))
                    {
                        var stream = data.GetData("PNG") as MemoryStream;
                        if (stream != null)
                        {
                            var dec = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            image = dec.Frames[0];
                        }
                    }
                    if (image == null && data.GetDataPresent(DataFormats.Bitmap)) image = Clipboard.GetImage();
                    if (image == null) return;
                }
                catch (System.Runtime.InteropServices.COMException)
                {
                    System.Threading.Thread.Sleep(40);   // someone else holds the clipboard
                }
                catch (Exception) { return; }
            }
            if (image == null) return;
            Trace.Write("clipboard image " + image.PixelWidth + "x" + image.PixelHeight + " " + image.Format);
            image = Opaque(image);
            // The same capture can be announced more than once, for example
            // again when the app that copied it exits. One capture, one photo.
            ulong hash = Hash(image);
            if (hash == lastHash && (DateTime.UtcNow - lastHashAt).TotalSeconds < 10) return;
            lastHash = hash;
            lastHashAt = DateTime.UtcNow;

            try
            {
                Directory.CreateDirectory(Line.InboxFolder);
                string name = Loc.L("Screenshot", "Captura", "截图") + " " + DateTime.Now.ToString("yyyy-MM-dd HHmmss") + ".png";
                string path = Line.UniquePath(Line.InboxFolder, name);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(image));
                using (var fs = File.Create(path)) enc.Save(fs);
                bool edit = editArmed && DateTime.UtcNow < armedUntil;
                armedUntil = DateTime.MinValue;
                editArmed = false;
                onCapture(path, CaptureArea.Guess(image.PixelWidth, image.PixelHeight, foregroundAtCapture), edit);
            }
            catch (Exception) { }
        }
    }
}
