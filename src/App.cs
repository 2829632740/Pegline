using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace Pegline
{
    /// Tray icon, shortcut, and the rules for revealing and tucking away the line.
    class Controller
    {
        readonly Line line = new Line();
        readonly LineWindow panel;
        readonly Forms.NotifyIcon tray = new Forms.NotifyIcon();
        ScreenshotWatcher watcher;
        readonly ClipboardCatcher catcher;
        readonly DispatcherTimer mouseTimer = new DispatcherTimer(DispatcherPriority.Render);
        IntPtr mouseHook;
        Native.HookProc hookProc;   // kept alive while the hook is installed

        /// Whether the window is shown. It can be shown and still tucked away
        /// above the top edge, like an auto-hiding taskbar.
        bool isPresent;
        /// Whether the line has slid down into view.
        bool isRevealed;
        /// Opened on purpose with the shortcut or the tray: it stays down until
        /// the pointer has visited it and left, or the shortcut is pressed again.
        bool pinned;
        /// A new screenshot shows itself for a moment, then tucks away.
        DateTime peekUntil = DateTime.MinValue;
        DateTime? hotZoneSince, awaySince;
        /// After a click in the top band the line stays tucked away until the
        /// pointer leaves the band, so it does not come back over a menu or tab.
        bool bandSuppressed;
        /// Whether the line should be up, if nothing prevents it.
        bool wanted;
        /// Set when you open the line on purpose, so it stays up while empty.
        bool keepOpen;
        int lastLiveCount;
        Forms.Screen pendingScreen;
        /// Recent captures by size: Win+PrtScn and the Windows 11 Snipping
        /// Tool both save a file and fill the clipboard. One capture, one photo.
        readonly List<KeyValuePair<DateTime, string>> recent = new List<KeyValuePair<DateTime, string>>();

        /// How long the pointer rests against the top edge before the line
        /// comes down. Short enough to feel instant, long enough that a quick
        /// trip to a tab or a title bar does not trigger it.
        static readonly TimeSpan RevealDelay = TimeSpan.FromSeconds(0.3);
        /// How long the pointer is away before the line tucks back up.
        static readonly TimeSpan RetractDelay = TimeSpan.FromSeconds(0.5);

        static bool CatchClipboard
        {
            get { return Settings.Get("catchClipboard"); }
            set { Settings.Set("catchClipboard", value); }
        }

        public Controller()
        {
            panel = new LineWindow(line);
            panel.PlaceOnScreen();
            UpdateCapacity();

            catcher = new ClipboardCatcher(HangCapture, Toggle) { Enabled = CatchClipboard };
            StartWatcher();
            SetUpTray();
            WatchBandClicks();

            line.OnFall = Fall;
            line.Changed += ItemsChanged;
            panel.Sync();
            lastLiveCount = line.LiveCount;

            mouseTimer.Interval = TimeSpan.FromSeconds(1.0 / 30);
            mouseTimer.Tick += delegate { Tick(); };

            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate
            {
                panel.Dispatcher.BeginInvoke(new Action(delegate
                {
                    tray.Icon = AppIcon.Tray(Screens.ScaleOf(Forms.Screen.PrimaryScreen));
                    panel.PlaceOnScreen(panel.Screen != null && Forms.Screen.AllScreens.Contains(panel.Screen) ? panel.Screen : null);
                    UpdateCapacity();
                }));
            };

            if (!Settings.Get("catchOffered")) After(1.2, OfferCatching);

            if (lastLiveCount > 0)
            {
                wanted = true;
                Refresh();
            }
            if (!Settings.Get("welcomed"))
            {
                Settings.Set("welcomed", true);
                keepOpen = true;
                wanted = true;
                Refresh();
                Reveal(true, 0);
                After(5, delegate
                {
                    if (line.LiveCount != 0) return;
                    keepOpen = false;
                    wanted = false;
                    Refresh();
                });
            }
        }

        static void After(double seconds, Action action)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            t.Tick += delegate { t.Stop(); action(); };
            t.Start();
        }

        public void Shutdown()
        {
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            catcher.Dispose();
            if (watcher != null) watcher.Stop();
            tray.Visible = false;
            tray.Dispose();
        }

        // Catching screenshots

        void StartWatcher()
        {
            if (watcher != null) watcher.Stop();
            watcher = new ScreenshotWatcher(Native.ScreenshotsFolder(), path =>
            {
                // Win+PrtScn captures the whole screen, so it can fly up from there.
                var size = Thumbnails.PixelSizeOf(path);
                HangCapture(path, CaptureArea.Guess((int)size.Width, (int)size.Height, Native.GetForegroundWindow(), false));
            }, line.Prune);
            watcher.Start();
        }

        void SetCatching(bool on)
        {
            CatchClipboard = on;
            catcher.Enabled = on;
        }

        /// Asked once. Reading the clipboard is the user's call, never ours.
        void OfferCatching()
        {
            Settings.Set("catchOffered", true);
            var answer = MessageBox.Show(
                Loc.L("Screenshots will hang on the line the instant you take them, with Win+Shift+S or PrtScn, " +
                      "without saving them by hand and without piling up on your Desktop. Drag one to a folder to keep it, " +
                      "or discard it with the cross. You can turn this off from the tray icon.",
                      "Las capturas se colgarán al instante, con Win+Mayús+S o ImprPant, sin guardarlas a mano y sin " +
                      "acumularse en el Escritorio. Arrastra una a una carpeta para guardarla, o descártala con la cruz. " +
                      "Puedes desactivarlo desde el icono de la bandeja.",
                      "用 Win+Shift+S 或 PrtScn 截的图会立刻挂到绳子上，不用手动保存，也不会堆在桌面。" +
                      "想留下就拖进文件夹，不要就点叉。随时可以在托盘图标里关掉。"),
                Loc.L("Let Pegline handle your screenshots?", "¿Quieres que Pegline se encargue de tus capturas?",
                      "让 Pegline 接管你的截图吗？"),
                MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Yes) SetCatching(true);
        }

        bool IsDuplicate(string path)
        {
            var size = Thumbnails.PixelSizeOf(path);
            if (size.Width <= 0) return false;
            string key = size.Width + "x" + size.Height;
            var now = DateTime.UtcNow;
            recent.RemoveAll(r => (now - r.Key).TotalSeconds > 4);
            bool dup = recent.Any(r => r.Value == key);
            recent.Add(new KeyValuePair<DateTime, string>(now, key));
            return dup;
        }

        // The capture flying to the line

        /// A new screenshot lifts off from where it was taken and flies to its
        /// place on the line. Without a known capture area it simply drops in.
        void HangCapture(string path, Rect? from)
        {
            bool dup;
            try { dup = IsDuplicate(path); }
            catch (Exception) { dup = false; }
            Trace.Write("capture " + path + " dup=" + dup);
            if (dup) return;
            if (from.HasValue)
            {
                var r = from.Value;
                pendingScreen = Forms.Screen.FromPoint(new System.Drawing.Point(
                    (int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)));
            }
            Guid id = line.Hang(path, false, from.HasValue);
            if (id == Guid.Empty || !from.HasValue) return;
            // Let the line come down and lay out before measuring the landing spot.
            After(0.03, delegate { Fly(id, from.Value); });
        }

        void Fly(Guid id, Rect from)
        {
            var item = line.Find(id);
            var to = panel.CardFrame(id);
            if (!isPresent || !isRevealed || item == null || !to.HasValue)
            {
                line.Land(id);
                return;
            }
            int pixels = (int)Math.Max(from.Width, from.Height);
            var image = Thumbnails.Load(item.Path, Math.Min(3000, Math.Max(400, pixels)));
            if (image == null) { line.Land(id); return; }
            Flight.Fly(image, from, to.Value, item.Tilt, panel.Screen, delegate { line.Land(id); });
        }

        /// A discarded card falls over the whole screen, from where it hangs.
        void Fall(Pegged item)
        {
            if (!isPresent || !isRevealed || item.Flying) return;
            var card = panel.CardFrame(item.Id);
            if (!card.HasValue) return;
            Flight.Fall(item.Thumb, card.Value, item.Tilt, panel.Screen);
        }

        // Showing and hiding

        void ItemsChanged()
        {
            panel.Sync();
            int live = line.LiveCount;
            if (live > lastLiveCount)
            {
                panel.PlaceOnScreen(pendingScreen);
                pendingScreen = null;
                UpdateCapacity();
                wanted = true;
                Refresh();
                Reveal(false, 2.5);
            }
            else if (live == 0 && !keepOpen)
            {
                After(0.7, delegate
                {
                    if (line.LiveCount != 0 || keepOpen) return;
                    wanted = false;
                    Refresh();
                });
            }
            lastLiveCount = live;
        }

        /// Decides whether the window is shown at all: something to show, and
        /// no full screen app on that screen.
        void Refresh()
        {
            var screen = panel.Screen ?? Screens.UnderPointer();
            bool blocked = FullScreen.IsActive(screen);
            if (wanted && !blocked) Present(); else Dismiss();
            // The pointer is watched while there is a line, even tucked away,
            // to notice it pushing against the top edge.
            if (wanted) mouseTimer.Start();
            else
            {
                mouseTimer.Stop();
                panel.SetClickThrough(true);
            }
        }

        void Present()
        {
            if (isPresent) return;
            isPresent = true;
            panel.Show();
            panel.KeepOnTop();
        }

        void Dismiss()
        {
            if (!isPresent) return;
            isPresent = false;
            SetRevealed(false);
            After(0.4, delegate { if (!isPresent) panel.Hide(); });
        }

        void Reveal(bool pin, double peekSeconds)
        {
            if (!isPresent) return;
            if (pin) pinned = true;
            if (peekSeconds > 0) peekUntil = DateTime.UtcNow.AddSeconds(peekSeconds);
            awaySince = null;
            panel.KeepOnTop();
            SetRevealed(true);
        }

        void SetRevealed(bool on)
        {
            if (on == isRevealed) return;
            isRevealed = on;
            line.Revealed = on;
            if (!on)
            {
                pinned = false;
                peekUntil = DateTime.MinValue;
                line.HoverId = Guid.Empty;
                panel.SetClickThrough(true);
            }
        }

        void Toggle()
        {
            if (isRevealed)
            {
                SetRevealed(false);
                if (line.LiveCount == 0)
                {
                    keepOpen = false;
                    wanted = false;
                    Refresh();
                }
            }
            else
            {
                keepOpen = true;
                wanted = true;
                panel.PlaceOnScreen();
                UpdateCapacity();
                Refresh();
                Reveal(true, 0);
            }
        }

        /// A click anywhere in the top band of any screen, a tab or a title
        /// bar, puts the line away.
        void WatchBandClicks()
        {
            hookProc = (code, w, l) =>
            {
                int msg = w.ToInt32();
                if (code >= 0 && (msg == Native.WM_LBUTTONDOWN || msg == Native.WM_RBUTTONDOWN))
                {
                    var info = (Native.MSLLHOOKSTRUCT)System.Runtime.InteropServices.Marshal.PtrToStructure(l, typeof(Native.MSLLHOOKSTRUCT));
                    var p = new System.Drawing.Point(info.pt.X, info.pt.Y);
                    if (Forms.Screen.AllScreens.Any(s => Screens.TopBand(s).Contains(p)))
                    {
                        bandSuppressed = true;
                        hotZoneSince = null;
                        if (isRevealed && line.PhotoAtCursorIsNone(panel, info.pt))
                        {
                            pinned = false;
                            SetRevealed(false);
                        }
                    }
                }
                return Native.CallNextHookEx(mouseHook, code, w, l);
            };
            mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, hookProc, Native.GetModuleHandle(null), 0);
        }

        void Tick()
        {
            var mouse = Screens.Cursor();
            var p = new System.Drawing.Point(mouse.X, mouse.Y);
            var now = DateTime.UtcNow;
            var screenUnderPointer = Forms.Screen.FromPoint(p);
            bool inBand = Screens.TopBand(screenUnderPointer).Contains(p);
            if (!inBand) bandSuppressed = false;
            // Dragging a window to the top edge snaps it to full size; that is
            // not a request for the line.
            bool buttonDown = (Native.GetAsyncKeyState(Native.VK_LBUTTON) & 0x8000) != 0;

            if (!isRevealed)
            {
                // Resting against the top edge brings the line down on that screen.
                if (inBand && !bandSuppressed && !buttonDown && !FullScreen.IsActive(screenUnderPointer))
                {
                    if (hotZoneSince == null) hotZoneSince = now;
                    if (now - hotZoneSince.Value >= RevealDelay)
                    {
                        hotZoneSince = null;
                        if (!screenUnderPointer.Equals(panel.Screen))
                        {
                            panel.PlaceOnScreen(screenUnderPointer);
                            UpdateCapacity();
                        }
                        Refresh();
                        Reveal(false, 0);
                    }
                }
                else hotZoneSince = null;
                return;
            }

            UpdateMousePassThrough(mouse);

            // The line's zone runs from its lowest point up to the top of the
            // screen, so moving up never hides it.
            var b = panel.PixelBounds;
            var zone = new Rect(b.Left, panel.Screen.Bounds.Top, b.Width, b.Bottom - panel.Screen.Bounds.Top);
            bool inside = zone.Contains(mouse.X, mouse.Y);
            if (inside && pinned) pinned = false;

            bool busy = pinned || Gestures.IsDragging || Gestures.MenuOpen || line.PressedId != Guid.Empty || now < peekUntil;
            if (inside || busy) awaySince = null;
            else
            {
                if (awaySince == null) awaySince = now;
                if (now - awaySince.Value >= RetractDelay)
                {
                    awaySince = null;
                    SetRevealed(false);
                }
            }
        }

        void UpdateMousePassThrough(Native.POINT mouse)
        {
            if (Gestures.IsDragging || Gestures.MenuOpen) return;
            Guid over = panel.PhotoAt(mouse);
            line.HoverId = over;
            panel.SetClickThrough(over == Guid.Empty && line.PressedId == Guid.Empty);
        }

        void UpdateCapacity()
        {
            double usable = panel.DipWidth - 200;
            line.MaxItems = Math.Max(3, Math.Min(12, (int)(usable / Layout.Spacing)));
        }

        /// Clears out the folder caught screenshots pile up in, after asking.
        /// Only Pegline's own folder: captures saved to Pictures\Screenshots
        /// are yours and stay.
        void EmptyInbox()
        {
            var files = Line.InboxFiles();
            if (files.Length == 0) return;
            int hanging = line.Items.Count(i => !i.Falling && line.IsInInbox(i.Id));
            var answer = MessageBox.Show(
                string.Format(Loc.L(
                    "Move the {0} screenshots Pegline caught to the Recycle Bin? {1} of them hang on the line and come down too.\n\n" +
                    "Screenshots saved to Pictures\\Screenshots (Win+PrtScn) are not touched.",
                    "¿Mover a la Papelera las {0} capturas que guardó Pegline? {1} están colgadas y también se descolgarán.\n\n" +
                    "Las capturas guardadas en Imágenes\\Capturas de pantalla (Win+ImprPant) no se tocan.",
                    "把 Pegline 接管保存的 {0} 个截图文件移到回收站？其中 {1} 张挂在绳子上，会一起取下。\n\n" +
                    "「图片\\Screenshots」里的截图（Win+PrtScn 存的）不受影响。"), files.Length, hanging),
                Loc.L("Delete caught screenshots", "Borrar capturas guardadas", "清空截图文件"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
            line.EmptyInbox(left =>
            {
                if (left == 0) return;
                MessageBox.Show(
                    string.Format(Loc.L("{0} files could not be moved to the Recycle Bin. They may be open in another app.",
                                        "No se pudieron mover {0} archivos a la Papelera. Puede que estén abiertos en otra aplicación.",
                                        "有 {0} 个文件没能移到回收站，可能正被别的程序打开。"), left),
                    "Pegline", MessageBoxButton.OK, MessageBoxImage.Information);
            });
        }

        // Tray icon

        void SetUpTray()
        {
            tray.Icon = AppIcon.Tray(Screens.ScaleOf(Forms.Screen.PrimaryScreen));
            tray.Text = "Pegline";
            tray.Visible = true;
            var menu = new Forms.ContextMenuStrip();
            menu.Opening += delegate { BuildMenu(menu); };
            BuildMenu(menu);
            tray.ContextMenuStrip = menu;
            tray.MouseClick += (s, e) => { if (e.Button == Forms.MouseButtons.Left) Toggle(); };
        }

        void BuildMenu(Forms.ContextMenuStrip menu)
        {
            menu.Items.Clear();
            Func<string, Action, Forms.ToolStripMenuItem> add = (title, action) =>
            {
                var item = new Forms.ToolStripMenuItem(title);
                item.Click += delegate { action(); };
                menu.Items.Add(item);
                return item;
            };

            var toggle = add(isRevealed ? Loc.L("Hide line", "Ocultar tendedero", "收起绳子")
                                        : Loc.L("Show line", "Mostrar tendedero", "放下绳子"), Toggle);
            if (catcher.HotKeyRegistered) toggle.ShortcutKeyDisplayString = "Ctrl+Alt+T";

            add(Loc.L("Take everything down", "Descolgar todo", "全部取下"), line.Clear).Enabled = line.LiveCount > 0;

            int caught = Line.InboxFiles().Length;
            add(string.Format(Loc.L("Delete caught screenshots ({0})…", "Borrar capturas guardadas ({0})…", "清空截图文件（{0} 个）…"), caught),
                EmptyInbox).Enabled = caught > 0 && !line.Emptying;

            var catching = add(Loc.L("Handle screenshots", "Encargarse de las capturas", "接管截图"),
                               () => SetCatching(!CatchClipboard));
            catching.Checked = CatchClipboard;
            catching.ToolTipText = Loc.L("Screenshots from Win+Shift+S and PrtScn hang instantly and skip the Desktop",
                                         "Las capturas de Win+Mayús+S e ImprPant se cuelgan al instante",
                                         "Win+Shift+S 和 PrtScn 的截图立刻挂上，不落到桌面");

            add(Loc.L("Open screenshots folder", "Abrir carpeta de capturas", "打开截图文件夹"), () =>
            {
                string folder = CatchClipboard ? Line.InboxFolder : watcher.Folder;
                Directory.CreateDirectory(folder);
                Process.Start("explorer.exe", "\"" + folder + "\"");
            });

            menu.Items.Add(new Forms.ToolStripSeparator());
            add(Loc.L("Sounds", "Sonidos", "音效"), () => line.SoundOn = !line.SoundOn).Checked = line.SoundOn;
            add(Loc.L("Open at login", "Abrir al iniciar sesión", "开机启动"), () =>
            {
                try { Settings.OpensAtLogin = !Settings.OpensAtLogin; }
                catch (Exception) { System.Media.SystemSounds.Beep.Play(); }
            }).Checked = Settings.OpensAtLogin;

            menu.Items.Add(new Forms.ToolStripSeparator());
            add(Loc.L("Quit Pegline", "Salir de Pegline", "退出 Pegline"), () => Application.Current.Shutdown());
        }
    }

    static class LineExtensions
    {
        /// A click on a photo itself (it can hang over the top band) is not a
        /// click that should put the line away.
        public static bool PhotoAtCursorIsNone(this Line line, LineWindow panel, Native.POINT p)
        {
            return panel.PhotoAt(p) == Guid.Empty;
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool first;
            using (var mutex = new Mutex(true, @"Local\Pegline.SingleInstance", out first))
            {
                if (!first) return;
                Screens.Init();
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                // A slip in one gesture should not take the line down with it.
                app.DispatcherUnhandledException += (s, e) => { Log(e.Exception); e.Handled = true; };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
                Controller controller = null;
                app.Startup += delegate { controller = new Controller(); };
                app.Exit += delegate { if (controller != null) controller.Shutdown(); };
                app.Run();
            }
        }

        static void Log(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(Path.Combine(Settings.Dir, "error.log"), DateTime.Now.ToString("s") + " " + e + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }
}
