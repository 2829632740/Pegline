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
        readonly HoverPreview preview;
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
        /// Recent captures: Win+PrtScn and the Windows 11 Snipping Tool both
        /// save a file and fill the clipboard. One capture, one photo, one file.
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
            preview = new HoverPreview(panel);
            Gestures.Interacting += preview.Hide;
            panel.PlaceOnScreen();
            UpdateCapacity();

            catcher = new ClipboardCatcher(HangCapture, Toggle, Snip, HangNote) { Enabled = CatchClipboard };
            LoadKey(ClipboardCatcher.SnipKeyId, "snipKey", Shortcut.DefaultSnip);
            LoadKey(ClipboardCatcher.NoteKeyId, "noteKey", Shortcut.DefaultNote);
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
                    preview.Hide();
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
            mouseTimer.Stop();
            Gestures.Interacting -= preview.Hide;
            preview.Close();
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

        /// The screenshot shortcut: Windows' own snipping overlay, the one
        /// behind Win+Shift+S. What you snip hangs even with catching off.
        void Snip()
        {
            catcher.ArmOnce();
            try { Process.Start("ms-screenclip:"); }
            catch (Exception)
            {
                // Older Windows without the protocol: press Win+Shift+S, once
                // the keys of the shortcut itself are likely let go.
                After(0.3, delegate
                {
                    Native.keybd_event(Native.VK_LWIN, 0, 0, UIntPtr.Zero);
                    Native.keybd_event(Native.VK_SHIFT, 0, 0, UIntPtr.Zero);
                    Native.keybd_event((byte)'S', 0, 0, UIntPtr.Zero);
                    Native.keybd_event((byte)'S', 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Native.keybd_event(Native.VK_SHIFT, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                    Native.keybd_event(Native.VK_LWIN, 0, Native.KEYEVENTF_KEYUP, UIntPtr.Zero);
                });
            }
        }

        /// Never set means the default; "none" means you turned it off.
        void LoadKey(int id, string setting, Shortcut fallback)
        {
            string stored = Settings.GetString(setting);
            catcher.SetKey(id, stored == "none" ? null : Shortcut.Parse(stored) ?? fallback);
        }

        void ChangeKey(int id, string setting, string title)
        {
            int other = id == ClipboardCatcher.SnipKeyId ? ClipboardCatcher.NoteKeyId : ClipboardCatcher.SnipKeyId;
            var before = catcher.KeyOf(id);
            catcher.SuspendKey(id);
            var dialog = new ShortcutDialog(title, before, key =>
            {
                if (key != null && key.Equals(Shortcut.Toggle) && catcher.HotKeyRegistered)
                    return Loc.L("Ctrl+Alt+T already shows and hides the line.", "Ctrl+Alt+T ya muestra y oculta el tendedero.",
                                 "Ctrl+Alt+T 已经用来放下 / 收起绳子了。");
                if (key != null && key.Equals(catcher.KeyOf(other)))
                    return Loc.L("Pegline already uses this combination for something else.",
                                 "Pegline ya usa esta combinación para otra cosa.",
                                 "这个组合 Pegline 已经用在另一个功能上了。");
                if (!catcher.SetKey(id, key))
                    return Loc.L("Another app is using this combination. Try another one.",
                                 "Otra aplicación usa esta combinación. Prueba otra.",
                                 "这个组合已被其他程序占用，换一个试试。");
                Settings.SetString(setting, key == null ? "none" : key.ToString());
                return null;
            });
            dialog.ShowDialog();
            if (!dialog.Applied) catcher.SetKey(id, before);
        }

        /// The note shortcut: the text on the clipboard hangs as a sticky
        /// note. Only when you ask, so copied passwords never end up on the
        /// line by themselves.
        void HangNote()
        {
            string text = null;
            for (int attempt = 0; attempt < 4 && text == null; attempt++)
            {
                try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; }
                catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(40); }
                catch (Exception) { text = ""; }
            }
            if (string.IsNullOrWhiteSpace(text))
            {
                var key = catcher.KeyOf(ClipboardCatcher.NoteKeyId);
                tray.ShowBalloonTip(3000, "Pegline", string.Format(Loc.L(
                    "There is no text on the clipboard. Copy some text, then press {0}.",
                    "No hay texto en el portapapeles. Copia un texto y pulsa {0}.",
                    "剪贴板里没有文字。先复制一段文字，再按 {0}。"), key != null ? key.ToString() : Loc.L("the shortcut", "el atajo", "快捷键")),
                    Forms.ToolTipIcon.Info);
                return;
            }
            // The same text again just shows the note already there.
            var same = line.Items.FirstOrDefault(i => !i.Falling && i.IsNote && Notes.Read(i.Path) == text);
            if (same != null)
            {
                wanted = true;
                Refresh();
                Reveal(false, 2.5);
                return;
            }
            try
            {
                pendingScreen = Screens.UnderPointer();
                line.Hang(Notes.Create(text));
            }
            catch (Exception) { System.Media.SystemSounds.Beep.Play(); }
        }

        /// The capture this one is a second copy of, if any: taken within a
        /// few seconds, one caught from the clipboard and the other saved by
        /// Windows, with the same picture. Two captures from the same source
        /// are always two captures, however alike.
        string TwinOf(string path)
        {
            var now = DateTime.UtcNow;
            recent.RemoveAll(r => (now - r.Key).TotalSeconds > 4);
            bool caught = Line.InInbox(path);
            var twin = recent.Select(r => r.Value)
                             .FirstOrDefault(p => Line.InInbox(p) != caught && File.Exists(p) && Thumbnails.SamePicture(p, path));
            if (twin == null) recent.Add(new KeyValuePair<DateTime, string>(now, path));
            return twin;
        }

        /// Keeps the file Windows saved and deletes the copy Pegline caught,
        /// moving the photo over to it if the copy is the one hanging.
        void FoldTwins(string path, string twin)
        {
            string copy = Line.InInbox(path) ? path : twin;
            string saved = copy == path ? twin : path;
            line.Repoint(copy, saved);
            try { File.Delete(copy); }
            catch (Exception) { }
            recent.RemoveAll(r => r.Value == copy);
            recent.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, saved));
        }

        // The capture flying to the line

        /// A new screenshot lifts off from where it was taken and flies to its
        /// place on the line. Without a known capture area it simply drops in.
        void HangCapture(string path, Rect? from)
        {
            string twin;
            try { twin = TwinOf(path); }
            catch (Exception) { twin = null; }
            Trace.Write("capture " + path + " twin=" + twin);
            if (twin != null)
            {
                FoldTwins(path, twin);
                return;
            }
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
            preview.Hide();
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
            if (!on) preview.Hide();
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

            bool previewButtonDown = buttonDown || (Native.GetAsyncKeyState(Native.VK_RBUTTON) & 0x8000) != 0
                || (Native.GetAsyncKeyState(Native.VK_MBUTTON) & 0x8000) != 0;
            UpdateMousePassThrough(mouse, now, previewButtonDown);

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

        void UpdateMousePassThrough(Native.POINT mouse, DateTime now, bool buttonDown)
        {
            if (Gestures.IsDragging || Gestures.MenuOpen)
            {
                preview.Hide();
                return;
            }
            Guid over = panel.PhotoAt(mouse);
            line.HoverId = over;
            panel.SetClickThrough(over == Guid.Empty && line.PressedId == Guid.Empty);
            if (!isPresent || !isRevealed || buttonDown || line.PressedId != Guid.Empty || FullScreen.IsActive(panel.Screen))
                preview.Hide();
            else
            {
                // A note shows all it has on its card; there is no picture to enlarge.
                var item = line.Find(over);
                preview.Update(item != null && item.IsNote ? null : item, panel.HoverFrame(over), panel.Screen, now);
            }
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

            int snipId = ClipboardCatcher.SnipKeyId, noteId = ClipboardCatcher.NoteKeyId;
            var snip = add(Loc.L("Take a screenshot", "Hacer una captura", "截图"), Snip);
            if (catcher.Holds(snipId)) snip.ShortcutKeyDisplayString = catcher.KeyOf(snipId).ToString();
            var note = add(Loc.L("Hang copied text as a note", "Colgar el texto copiado como nota", "把复制的文字挂成便签"), HangNote);
            if (catcher.Holds(noteId)) note.ShortcutKeyDisplayString = catcher.KeyOf(noteId).ToString();
            Func<int, string> keyText = id => catcher.KeyOf(id) == null ? Loc.L("none", "ninguno", "未设置")
                : catcher.Holds(id) ? catcher.KeyOf(id).ToString()
                : catcher.KeyOf(id) + Loc.L(" (in use by another app)", " (en uso por otra aplicación)", "（被其他程序占用）");
            string snipTitle = Loc.L("Screenshot shortcut", "Atajo de captura", "截图快捷键");
            string noteTitle = Loc.L("Note shortcut", "Atajo de nota", "便签快捷键");
            add(snipTitle + Loc.L(": ", ": ", "：") + keyText(snipId) + "…", () => ChangeKey(snipId, "snipKey", snipTitle));
            add(noteTitle + Loc.L(": ", ": ", "：") + keyText(noteId) + "…", () => ChangeKey(noteId, "noteKey", noteTitle));

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
