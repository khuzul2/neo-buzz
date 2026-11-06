// Fork (docs/specs/ui-gallery.md): the studio's UI gallery for reviews. Every view, menu, context menu, window and
// dialog of the Mac app that can be reached headless, with the demo song (ReBuzz/AppViews/GallerySong.cs) loaded
// where content matters, drawn at 1440×900 logical points and scale 2 (a Retina Mac), as gallery/mac-NNN-*.png with
// gallery/index-mac.md (what each shows, how a user gets there, notes on the capture). Explicit: normal runs skip it.
//
//   dotnet test ReBuzz.Studio.Tests -c Release --filter TestCategory=Gallery     (GALLERY_DIR: where to; default
//   bin/…/screenshots/gallery)
//
// The window is drawn as macOS has it: no in-window menu (the Mac's menus are in the menu bar, which headless can't
// draw: the menu shots draw the same menu model as an in-window menu instead). Dialogs, which are windows of their
// own, are drawn over the main window, centred, as macOS shows a modal window. Each shot is taken whatever the others
// did: one that fails is listed (index-mac.md, the test's output) and the rest go on; the test then fails.

using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;
using ReBuzz.Core.Session;
using ReBuzz.Gallery;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    [Category("Gallery")]
    [Explicit("The UI gallery for reviews (docs/specs/ui-gallery.md): dotnet test ReBuzz.Studio.Tests --filter TestCategory=Gallery")]
    public class GalleryTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ the gallery

        internal const double Width = 1440, Height = 900, Scale = 2;
        const int MaxBytes = 1_500_000;

        sealed record Row(string File, string What, string How, string Size, string Song, string Notes);

        static readonly List<Row> rows = new();
        static readonly List<string> failures = new();
        static bool installed;
        static readonly List<Window> opened = new();
        static System.Action<Window> onOpened;

        static string Folder
        {
            get
            {
                string dir = System.Environment.GetEnvironmentVariable("GALLERY_DIR") ?? Path.Combine(Studio.ScreenshotFolder, "gallery");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>Every window drawn at scale 2 from when it opens; the newest window kept (dialogs).</summary>
        static void Install()
        {
            if (installed) return;
            installed = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, e) =>
            {
                opened.RemoveAll(x => !x.IsVisible);
                opened.Add(w);
                w.SetRenderScaling(Scale);
                onOpened?.Invoke(w);
            }, RoutingStrategies.Direct, handledEventsToo: true);
        }

        static void Settle()
        {
            for (int i = 0; i < 2; i++)
            {
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>Lets the views' timers run (meters, play cursors, the status line) for about <paramref name="ms"/>.</summary>
        static void Wait(int ms)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (clock.ElapsedMilliseconds < ms)
            {
                Thread.Sleep(15);
                Settle();
            }
        }

        static Bitmap Frame(TopLevel top)
        {
            foreach (var v in top.GetVisualDescendants()) v.InvalidateVisual();
            Settle();
            var frame = top.CaptureRenderedFrame();
            Assert.That(frame, Is.Not.Null, "the window drew");
            return frame!;
        }

        static string SizeText(TopLevel top, Bitmap frame) =>
            $"{top.ClientSize.Width:0}×{top.ClientSize.Height:0} pt, scale {top.RenderScaling:0.#} ({frame.PixelSize.Width}×{frame.PixelSize.Height} px)";

        static void Save(Bitmap frame, string file)
        {
            string path = Path.Combine(Folder, file);
            frame.Save(path);
            if (new FileInfo(path).Length > MaxBytes)
            {
                // Over the size a review page takes well: the same picture at three quarters (never seen at 2880×1800).
                var smaller = frame.CreateScaledBitmap(new PixelSize(frame.PixelSize.Width * 3 / 4, frame.PixelSize.Height * 3 / 4), BitmapInterpolationMode.HighQuality);
                smaller.Save(path);
            }
        }

        static void Record(string file, string what, string how, string size, string song, string notes)
        {
            rows.RemoveAll(r => r.File == file);
            rows.Add(new Row(file, what, how, size, song, notes));
            WriteIndex();
        }

        static void WriteIndex()
        {
            static string Cell(string s) => (s ?? "").Replace("|", "\\|").Replace("\n", " ");
            var sb = new StringBuilder();
            sb.AppendLine("| App | File | What it shows | How a user reaches it | Window size and scale | Song | Notes |");
            sb.AppendLine("|---|---|---|---|---|---|---|");
            foreach (var r in rows.OrderBy(r => r.File, System.StringComparer.Ordinal))
                sb.AppendLine($"| Mac ({Product.Name}, headless) | {Cell(r.File)} | {Cell(r.What)} | {Cell(r.How)} | {Cell(r.Size)} | {Cell(r.Song)} | {Cell(r.Notes)} |");
            if (failures.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Not captured:");
                foreach (var f in failures) sb.AppendLine("- " + f);
            }
            File.WriteAllText(Path.Combine(Folder, "index-mac.md"), sb.ToString());
        }

        const string Headless = "Headless (Avalonia.Headless, Skia): the window's content only, no macOS title bar; text in the headless fallback font, greyscale-antialiased.";

        /// <summary>A shot of a window: mac-<paramref name="id"/>.png.</summary>
        static void Shot(TopLevel top, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            string file = $"mac-{id}.png";
            var frame = Frame(top);
            Save(frame, file);
            Record(file, what, how, SizeText(top, frame), song, notes ?? "");
        }

        /// <summary>A dialog (a window of its own) drawn over the main window, centred, with a shadow, as macOS shows it.</summary>
        static void DialogShot(Window main, Window dialog, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            string file = $"mac-{id}.png";
            var back = Frame(main);
            var front = Frame(dialog);
            var size = back.PixelSize;
            using var target = new RenderTargetBitmap(size, new Vector(96, 96));
            using (var dc = target.CreateDrawingContext())
            {
                // Source rectangles in pixels: the frames are 192 dpi, and DrawImage's default source (Size, in points) would take a quarter.
                dc.DrawImage(back, new Rect(0, 0, size.Width, size.Height), new Rect(0, 0, size.Width, size.Height));
                double x = System.Math.Round((size.Width - front.PixelSize.Width) / 2.0), y = System.Math.Round((size.Height - front.PixelSize.Height) / 3.0);
                var r = new Rect(x, y, front.PixelSize.Width, front.PixelSize.Height);
                for (int i = 1; i <= 12; i++)
                    dc.DrawRectangle(new SolidColorBrush(Colors.Black, 0.035), null, r.Inflate(i * 2).Translate(new Vector(0, i)), 12 + i, 12 + i);
                dc.DrawImage(front, new Rect(0, 0, front.PixelSize.Width, front.PixelSize.Height), r);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(0x60, 0x80, 0x80, 0x80)), 2), r);
            }
            Save(target, file);
            Record(file, what, how, $"{SizeText(main, back)}; dialog {SizeText(dialog, front)}", song,
                (notes == null ? "" : notes + " ") + "The dialog is its own window: drawn here over the main window, centred, with a shadow (the harness's composite; macOS adds its title bar).");
        }

        /// <summary>A sentence on the machine keyboard under a machine's window, where it has one (a machine that plays notes).</summary>
        static string KeyboardUnder(MachineCore m)
        {
            var route = KeyboardRoute.For(m);
            if (!route.Shows) return "";
            return route.Input == KeyboardInput.Midi
                ? " Under it, the machine keyboard: it plays the machine by MIDI, as a MIDI keyboard does."
                : " Under it, the machine keyboard: it plays the machine's Note track parameter, a note per track.";
        }

        /// <summary>Takes a shot, or lists why not and goes on.</summary>
        static void Try(string id, System.Action shot)
        {
            try
            {
                shot();
            }
            catch (System.Exception e)
            {
                string why = $"mac-{id}: {e.GetType().Name}: {e.Message.Split('\n')[0]}";
                failures.Add(why);
                TestContext.Out.WriteLine("[gallery] not captured: " + why + "\n" + e.StackTrace);
                WriteIndex();
            }
            finally
            {
                foreach (var w in Popups().ToList()) w.Close();
                CloseMenus();
                Settle();
            }
        }

        static List<Window> mains = new();

        /// <summary>Dialogs and tool windows a shot left open (not main windows or Preferences).</summary>
        static IEnumerable<Window> Popups() => opened.Where(w => w.IsVisible && w is not MainWindow && w is not PreferencesWindow).ToList();

        /// <summary>
        /// A dialog that <paramref name="trigger"/> opens, drawn over <paramref name="main"/> as soon as it opens and then
        /// closed (Cancel): from inside the dialog's own modal frame where it has one (the engine's questions wait in one).
        /// </summary>
        static void DialogOnOpen(Window main, System.Action trigger, string id, string what, string how, string song = GallerySong.Title, string notes = null)
        {
            System.Exception error = null;
            bool done = false;
            onOpened = w =>
            {
                onOpened = null;
                Dispatcher.UIThread.Post(() =>
                {
                    try
                    {
                        CloseMenus();           // the menu that opened it has gone by the time a user sees the dialog
                        Settle();
                        DialogShot(main, w, id, what, how, song, notes);
                    }
                    catch (System.Exception e)
                    {
                        error = e;
                    }
                    finally
                    {
                        w.Close();
                        done = true;
                    }
                }, DispatcherPriority.Background);
            };
            try
            {
                trigger();
                for (int i = 0; i < 40 && !done; i++) Settle();
            }
            finally
            {
                onOpened = null;
            }
            if (error != null) throw error;
            if (!done) throw new AssertionException("the dialog didn't open");
        }

        static readonly List<ContextMenu> menus = new();

        static void CloseMenus()
        {
            foreach (var m in menus) m.Close();
            menus.Clear();
            foreach (var w in mains) foreach (var mi in w.GetVisualDescendants().OfType<MenuItem>().Where(i => i.IsSubMenuOpen)) mi.Close();
        }

        static int failuresBefore;

        /// <summary>At a test's start: its failures are the ones listed after this.</summary>
        static void BeforeAll() => failuresBefore = failures.Count;

        static void AfterAll()
        {
            WriteIndex();
            TestContext.Out.WriteLine($"[gallery] {rows.Count} shots in {Folder}");
            var mine = failures.Skip(failuresBefore).ToList();
            if (mine.Count > 0) Assert.Fail($"{mine.Count} shot(s) not captured:\n" + string.Join("\n", mine));
        }

        // ------------------------------------------------------------------ the main window and the song

        static WorkManager work;
        readonly float[] buffer = new float[512];

        /// <summary>Renders whole ticks offline (no audio device here), as the audio thread would.</summary>
        void RunTicks(int ticks)
        {
            work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            for (int i = 0; i < ticks; i++)
            {
                var mi = ReBuzzCore.masterInfo;
                int frames = System.Math.Max(1, mi.SamplesPerTick - mi.PosInTick);
                while (frames > 0)
                {
                    int n = System.Math.Min(frames, buffer.Length / 2);
                    lock (ReBuzzCore.AudioLock) work.MainAudioFillBuffer(buffer, 0, n * 2);
                    frames -= n;
                }
            }
            Dispatcher.UIThread.RunJobs();
        }

        static string SongFolder
        {
            get
            {
                string dir = Path.Combine(Path.GetTempPath(), "rebuzz-gallery-" + System.Environment.ProcessId);
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        /// <summary>A task's result, the dispatcher running meanwhile (a blocking wait could deadlock on the UI thread); up to a minute.</summary>
        static T Await<T>(System.Threading.Tasks.Task<T> task)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted && clock.Elapsed.TotalSeconds < 60)
            {
                Thread.Sleep(10);
                Settle();
            }
            Assert.That(task.IsCompleted, Is.True, "it finished within a minute");
            return task.Result;
        }

        static void NewSong()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Core.ExecuteCommand(BuzzCommand.Stop);
            Core.Recording = false;
            Core.Modified = false;
            Core.ExecuteCommand(BuzzCommand.NewFile);
            Settle();
            Dialogs.Responder = null;
        }

        /// <summary>
        /// The demo song, built in a new song and saved as "Night Drive.bmxml" (the title names it). Not opened again: the
        /// notes moved onto the note lanes show in the Pattern View as they are written (they showed only once the song was
        /// opened again before; ui-gallery.md).
        /// </summary>
        static GallerySong.Song LoadDemo()
        {
            NewSong();
            var built = GallerySong.Build(Core);
            foreach (var p in built.Problems) TestContext.Out.WriteLine("[gallery] demo song: " + p);
            string path = Path.Combine(SongFolder, GallerySong.Title + ".bmxml");
            SongFiles.Picker = _ => path;
            try
            {
                Assert.That(Await(SongFiles.SaveAs()), Is.True, "the demo song saved");
            }
            finally
            {
                SongFiles.Picker = null;
            }
            Core.Modified = false;
            File.Copy(path, Path.Combine(Folder, GallerySong.Title + ".bmxml"), true);      // beside the pictures (the Mac job opens it in the app)
            Settle();
            return GallerySong.Find(Core);
        }

        /// <summary>The main window at 1440×900, scale 2, as on a Mac: no in-window menu (the menu bar has it).</summary>
        static MainWindow Open(string view = "Machines")
        {
            Install();
            var window = new MainWindow(Studio.Host) { Width = Width, Height = Height };
            window.Show();
            window.SetRenderScaling(Scale);
            if (window.WindowMenu != null) window.WindowMenu.IsVisible = false;
            mains.Add(window);
            Settle();
            window.ShowView(view);
            Settle();
            window.RefreshNow();
            return window;
        }

        static void Close(MainWindow window)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            foreach (var w in Popups().ToList()) w.Close();
            PreferencesWindow.Current?.Close();
            window.Close();
            mains.Remove(window);
            Settle();
            Dialogs.Responder = null;
            Dialogs.TextResponder = null;
        }

        static T Tab<T>(MainWindow window, string title) where T : class =>
            window.Tabs.Items.OfType<TabItem>().First(t => (string)t.Header == title).Content as T;

        static void Click(TopLevel top, Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
        {
            top.MouseMove(p, mods);
            top.MouseDown(p, button, mods);
            top.MouseUp(p, button, mods);
            Settle();
        }

        static Point Centre(Control c, TopLevel top, double fx = 0.5, double fy = 0.5)
        {
            top.UpdateLayout();
            return c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), top)!.Value;
        }

        static void Key(TopLevel top, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
        {
            top.KeyPressQwerty(key, mods);
            top.KeyReleaseQwerty(key, mods);
            Settle();
        }

        /// <summary>Hovers a menu item (its submenu opens), as the pointer would.</summary>
        static void Hover(MenuItem item)
        {
            var top = TopLevel.GetTopLevel(item)!;
            top.UpdateLayout();
            var p = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
            top.MouseMove(p);
            Settle();
            if (item.ItemCount > 0 && !item.IsSubMenuOpen) item.IsSubMenuOpen = true;
            Settle();
        }

        static MenuItem ItemOf(ItemsControl menu, string header) =>
            menu.Items.OfType<MenuItem>().FirstOrDefault(i => (i.Tag as string ?? i.Header as string) == header || i.Header as string == header)
            ?? throw new AssertionException($"no item {header} (there are {string.Join(", ", menu.Items.OfType<MenuItem>().Select(i => i.Header))})");

        static Window WaitForWindow(System.Func<Window, bool> which)
        {
            for (int i = 0; i < 20; i++)
            {
                Settle();
                var w = opened.LastOrDefault(x => x.IsVisible && which(x));
                if (w != null) return w;
            }
            throw new AssertionException("the window didn't open");
        }

        /// <summary>The pattern grid focused and its cursor on the first note lane (Tab), which scrolls the lanes into view.</summary>
        static void ToNotes(MainWindow window)
        {
            dynamic view = PatternView.Page.EditorControl;
            ((Control)view.TrackerGrid.Body).Focus();
            Settle();
            Key(window, PhysicalKey.Tab);
            Wait(100);
        }

        // ------------------------------------------------------------------ 0xx: start, views, transport

        [AvaloniaTest, Order(1)]
        public void Views()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() => { SessionCore.Timestamp = System.Diagnostics.Stopwatch.GetTimestamp; Core.ExecuteCommand(BuzzCommand.Stop); Core.Recording = false; });
            NewSong();
            var window = Open();
            try
            {
                Try("010-start-empty", () => Shot(window, "010-start-empty", "The main window on a new, empty song: transport bar, view tabs (Machines selected), the Master alone, status line.",
                    $"Start {Product.Name} (or File ▸ New, ⌘N).", song: "none (new song)", notes: Headless));
                Try("011-start-sequence-empty", () => { window.ShowView("Sequence"); Shot(window, "011-start-sequence-empty", "The Sequence tab on a new song: no tracks yet.", "F4, or the Sequence tab.", song: "none (new song)"); });
                Try("012-start-session-empty", () => { window.ShowView("Session"); Shot(window, "012-start-session-empty", "The Session tab on a new song: an empty launch grid with its toolbar and mixer.", "Ctrl+Tab, or the Session tab.", song: "none (new song)"); });
                Try("013-start-pattern-empty", () => { window.ShowView("Pattern"); Shot(window, "013-start-pattern-empty", "The Pattern tab on a new song: no machine with patterns yet.", "F2, or the Pattern tab.", song: "none (new song)"); });
                Try("014-start-notes-empty", () => { window.ShowView("Song Notes"); Shot(window, "014-start-notes-empty", "The Song Notes tab on a new song: the empty notes editor and its toolbar.", "F10, or the Song Notes tab.", song: "none (new song)"); });
            }
            finally
            {
                Close(window);
            }

            var demo = LoadDemo();
            window = Open();
            SessionCore.Timestamp = () => 1_000_000;
            try
            {
                var machines = Tab<MachineView>(window, "Machines");
                Try("020-machines", () => Shot(window, "020-machines", "Machines: the demo song's machines and wires (Neo generators on the left, effects, Master on the right), at zoom 0.",
                    "F3, or the Machines tab.", notes: Headless));
                Try("021-machines-selected", () =>
                {
                    Click(window, machines.PointOf(demo.Lead, window) + new Vector(14, 9));
                    Shot(window, "021-machines-selected", "Machines with Lead selected (a click on its box).", "Click a machine.");
                });
                Try("022-machines-hover", () =>
                {
                    ToolTip.SetServiceEnabled(window, true);
                    window.MouseMove(machines.PointOf(demo.Delay, window) + new Vector(3, 2));
                    Wait(100);
                    window.MouseMove(machines.PointOf(demo.Delay, window));
                    Wait(1200);
                    Shot(window, "022-machines-hover", "Machines with the pointer resting on Delay: its hover state and tooltip.", "Point at a machine.");
                    ToolTip.SetServiceEnabled(window, false);
                });
                Try("023-machines-amp", () =>
                {
                    var wire = demo.Delay.Outputs.First(o => o.Destination == demo.Verb);
                    Click(window, machines.ArrowOf(wire, window), MouseButton.Right);
                    Assert.That(machines.Amp.IsVisible, Is.True, "the amp control opened");
                    Shot(window, "023-machines-amp", "Machines: the amp and pan control of the Delay → Verb wire (the send).", "Right-click a wire's arrow.");
                    window.MouseMove(new Point(20, 860));
                    Settle();
                });
                Try("024-machines-zoomed", () =>
                {
                    machines.ZoomSlider.Value = 3;
                    Settle();
                    Shot(window, "024-machines-zoomed", "Machines zoomed in (zoom slider at 3).", "⌘ + scroll wheel, a pinch, or the zoom slider (top right).");
                    machines.ZoomSlider.Value = 0;
                    Settle();
                });

                Try("030-sequence", () =>
                {
                    window.ShowView("Sequence");
                    Shot(window, "030-sequence", "Sequence: the 16-bar arrangement of the demo song, one track a generator, coloured pattern boxes, the pattern list on the right.", "F4, or the Sequence tab.");
                });
                Try("031-sequence-cursor", () =>
                {
                    var seq = Tab<SequenceView>(window, "Sequence");
                    var geo = seq.Canvas.Geometry;
                    Click(window, seq.Canvas.TranslatePoint(new Point(geo.XOf(8 * 16) + 6, geo.YOf(2) + geo.TrackHeight * 0.6), window)!.Value);
                    Shot(window, "031-sequence-cursor", "Sequence with the cursor on Lead's Hook at bar 9 (selected pattern, the pattern list follows).", "Click a pattern box.");
                });

                Try("040-session", () =>
                {
                    window.ShowView("Session");
                    Shot(window, "040-session", "Session (stopped): the launch grid of the demo song's scenes (Intro, Verse, Drop, Break, Fill), clip colours, the Verb return, clip panel and mixer.", "Ctrl+Tab, or the Session tab.", notes: Headless);
                });
                var session = Tab<SessionView>(window, "Session");
                Try("041-session-queued", () =>
                {
                    Click(window, Centre(session.Scenes[2], window));
                    Shot(window, "041-session-queued", "Session: the Drop scene launched, its clips queued for the next bar (blinking state, drawn mid-blink).", "Click a scene's ▶ (or press its number key).");
                });
                Try("042-session-playing", () =>
                {
                    RunTicks(20);
                    session.RefreshState();
                    session.RefreshMeters();
                    Shot(window, "042-session-playing", "Session playing: the Drop scene's clips playing (green with their progress), the clock running, meters moving.", "After a launch, at the bar line; the transport runs.");
                });
                Try("043-session-clip-panel", () =>
                {
                    Click(window, Centre(session.SlotAt(2, 2), window, 0.6));
                    Shot(window, "043-session-clip-panel", "Session: Lead's Hook clip selected, its properties in the clip panel (launch mode, quantization, legato, loop region, follow action, scene tempo).", "Click a clip (not its ▶).");
                });
                Try("044-session-mixer-hidden", () =>
                {
                    Click(window, Centre(session.MixerButton, window));
                    Shot(window, "044-session-mixer-hidden", "Session with the mixer hidden (the grid gets the room).", "The toolbar's Mixer button (Ctrl+Alt+M).");
                    Click(window, Centre(session.MixerButton, window));
                });
                Try("045-session-stopped-all", () =>
                {
                    Click(window, Centre(session.StopAllButton, window));
                    RunTicks(20);
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    Settle();
                    session.RefreshState();
                    Shot(window, "045-session-stopped-all", "Session after Stop all: clips stopped, the transport stopped.", "The toolbar's ■ Stop all, then Space.");
                });
                Try("046-session-recording", () =>
                {
                    Core.Playing = true;
                    RunTicks(3);
                    int padTrack = session.Headers.ToList().FindIndex(h => h.Machine == demo.Pad);
                    Click(window, Centre(session.Headers[padTrack].Arm, window));
                    session.RefreshState();
                    var empty = session.SlotAt(padTrack, 3);
                    Click(window, Centre(empty.RecordButton, window));
                    RunTicks(24);
                    Core.SendMIDIInput(SessionRig.NoteOn(60, 99));
                    RunTicks(4);
                    session.RefreshState();
                    Shot(window, "046-session-recording", "Session recording: Pad's track armed (●), a take recording into its empty Break slot while the transport runs.", "Arm a track (its header's ●), then click an empty slot's ● (or the toolbar's ● Session record).");
                    Core.SendMIDIInput(SessionRig.NoteOff(60));
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    RunTicks(2);
                    Click(window, Centre(session.Headers[padTrack].Arm, window));
                    Core.SessionLayout.Undo();
                    Settle();
                });
                Try("047-session-piano-roll", () =>
                {
                    var piano = PianoRollWindow.Show(window, Core, Core.SessionLayout, Core.SessionLayout.SequenceOf(session.Headers.ToList().FindIndex(h => h.Machine == demo.Lead)), demo.Hook);
                    Settle();
                    Shot(piano, "047-session-piano-roll", "The piano roll of Lead's Hook clip (a window of its own): notes, velocities, the grid.", "⌘ + double-click a clip in the Session View.");
                    piano.Close();
                });

                Core.BPM = 124;                 // the Drop scene's tempo, put back
                Core.SongCore.PlayPosition = 0;
                Try("050-pattern", () =>
                {
                    PatternView.Edit(demo.LineA);
                    window.ShowView("Pattern");
                    Wait(200);
                    ToNotes(window);
                    Shot(window, "050-pattern", "Pattern: Bass's Line A in the Modern Pattern Editor (machine and pattern choosers, toolbar, the global columns, the notes on the MIDI note lanes on the right).",
                        "F2, or the Pattern tab; or double-click a pattern in the Sequence View.");
                });
                Try("051-pattern-drums", () =>
                {
                    PatternView.Edit(demo.Beat);
                    Wait(200);
                    ToNotes(window);
                    Shot(window, "051-pattern-drums", "Pattern: Drums' Beat (kick, snare, clap and hats on Neo Drums' MIDI note lanes).", "Choose Drums and Beat in the Pattern tab's choosers.");
                });
                Try("052-pattern-selection", () =>
                {
                    PatternView.Edit(demo.Chords);
                    Wait(200);
                    ToNotes(window);
                    for (int i = 0; i < 6; i++) Key(window, PhysicalKey.ArrowDown, RawInputModifiers.Shift);
                    for (int i = 0; i < 2; i++) Key(window, PhysicalKey.ArrowRight, RawInputModifiers.Shift);
                    Shot(window, "052-pattern-selection", "Pattern: Pad's Chords with a block selected (Shift + arrows).", "Shift + arrow keys (or a drag) in the pattern grid.");
                });
                Try("053-pattern-long-scrolled", () =>
                {
                    PatternView.Edit(demo.Arp);
                    Wait(200);
                    ToNotes(window);
                    for (int i = 0; i < 4; i++) Key(window, PhysicalKey.PageDown);
                    Wait(150);
                    Shot(window, "053-pattern-long-scrolled", "Pattern: Lead's 8-bar Arp (128 rows), scrolled down with the cursor past the middle.", "Page Down in a long pattern.");
                });

                Control Grid() => (Control)((dynamic)PatternView.Page.EditorControl).TrackerGrid.Body;
                ContextMenu GridMenu()
                {
                    // In the grid's view (the body is as wide as all its columns, most of them scrolled away).
                    var body = Grid();
                    var view = (Control)((dynamic)PatternView.Page.EditorControl).TrackerGrid;
                    Click(window, Centre(view, window, 0.45, 0.4), MouseButton.Right);
                    var menu = body.ContextMenu;
                    Assert.That(menu, Is.Not.Null, "the grid has a menu");
                    if (!menu!.IsOpen) menu.Open(body);
                    Settle();
                    Assert.That(menu.IsOpen, Is.True, "the grid's menu opened");
                    menus.Add(menu!);
                    return menu!;
                }
                Try("054-pattern-menu", () =>
                {
                    GridMenu();
                    Shot(window, "054-pattern-menu", "Pattern: the grid's context menu (Edit ▸, Select All, Unselect, Pattern Properties…).", "Right-click in the pattern grid.");
                });
                Try("055-pattern-menu-edit", () =>
                {
                    Hover(ItemOf(GridMenu(), "Edit"));
                    Shot(window, "055-pattern-menu-edit", "Pattern: the grid menu's Edit ▸ (increase, interpolate, randomize, rotate, write state, undo, clipboard).", "Right-click in the pattern grid ▸ Edit.");
                });
                Try("056-pattern-properties", () =>
                {
                    var item = GridMenu().Items.OfType<MenuItem>().First(i => (i.Header as string)?.StartsWith("Pattern Properties") == true);
                    DialogOnOpen(window, () => Click(window, Centre(item, window)), "056-pattern-properties", "Pattern Properties: name, length, rows per beat, the columns shown (the MIDI lanes among them).",
                        "Right-click in the pattern grid ▸ Pattern Properties… (Ctrl+Backspace).");
                });
                Try("057-pattern-help", () =>
                {
                    var help = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().First(b => b.Content as string == "Help");
                    Click(window, Centre(help, window));
                    Wait(150);
                    Shot(window, "057-pattern-help", "Pattern: the editor's help panel (its keys).", "The pattern toolbar's Help button (F1).");
                    Click(window, Centre(help, window));
                });

                Try("060-song-notes", () =>
                {
                    window.ShowView("Song Notes");
                    Shot(window, "060-song-notes", "Song Notes: the demo song's notes (Markdown: headings, a list, bold), with the formatting toolbar.", "F10, or the Song Notes tab.");
                });

                // The transport in its three states, in the Sequence View (its play cursor shows where the song is).
                Try("070-transport-stopped", () =>
                {
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    Core.SongCore.PlayPosition = 0;
                    window.ShowView("Sequence");
                    window.RefreshNow();
                    Shot(window, "070-transport-stopped", "The transport bar stopped (▶ ■ ● Loop, BPM, position 1.1.1, the master meter), Sequence View.", "Space or F8 stops.");
                });
                Try("071-transport-playing", () =>
                {
                    Core.SongCore.PlayPosition = 8 * 16;
                    Core.Playing = true;
                    RunTicks(22);
                    Wait(300);
                    window.RefreshNow();
                    Shot(window, "071-transport-playing", "The transport playing from bar 9: ▶ lit, the position counting, the meter showing the level, the play cursor in the Sequence View.", "Space or F5 plays.");
                });
                Try("072-transport-recording", () =>
                {
                    Core.Recording = true;
                    RunTicks(4);
                    Wait(200);
                    window.RefreshNow();
                    Shot(window, "072-transport-recording", "The transport recording while it plays: ● lit.", "F7 (or the ● button) while playing.");
                    Core.Recording = false;
                    Core.ExecuteCommand(BuzzCommand.Stop);
                    RunTicks(2);
                    Settle();
                });
            }
            finally
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 1xx: the menus

        /// <summary>The Mac's menus as an in-window menu: the app menu (ReBuzz) and the menu bar's File … Help, from the same model.</summary>
        static Menu MacMenuBar(MainWindow window)
        {
            var model = new[] { new MenuEntry { Header = Avalonia.Application.Current?.Name ?? Product.Name, Items = () => window.AppMenuModel() } }.Concat(window.MenuModel(mac: true));
            var bar = AppMenu.ToMenu(model);
            var dock = (DockPanel)window.Transport.Parent!;
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Insert(0, bar);
            Settle();
            return bar;
        }

        [AvaloniaTest, Order(2)]
        public void Menus()
        {
            BeforeAll();
            LoadDemo();
            var window = Open("Machines");
            try
            {
                var bar = MacMenuBar(window);
                const string Bar = "macOS shows these menus in its menu bar, which headless can't draw: here the same menu model (AppMenu, mac: true) is drawn as an in-window Avalonia menu at the top of the window. Shortcuts show as Avalonia writes them off a Mac.";
                var items = bar.Items.OfType<MenuItem>().ToList();
                string[] ids = { "100-menu-app", "101-menu-file", "102-menu-edit", "103-menu-transport", "104-menu-view", "105-menu-window", "106-menu-help" };
                string[] whats =
                {
                    $"The app menu ({Product.Name}) as the model has it, whole: About {Product.Name}, Preferences… (⌘,), Services (the system fills it), Hide {Product.Name} (⌘H), Hide Others (⌥⌘H), Show All, Quit {Product.Name} (⌘Q). The real Mac's is mac-real-100.",
                    "File: New, Open…, Open Recent ▸, Save, Save As… (no Exit: Quit is in the app menu).",
                    "Edit: Undo, Redo, Cut, Copy, Paste (Preferences is in the app menu).",
                    "Transport: Play or Stop (Space), Play, Stop, Record, Restart audio.",
                    "View: the five views, the shown one checked, with their keys.",
                    "Window: Minimize, Zoom, Agent connection….",
                    "Help: Keyboard shortcuts… (About is in the app menu).",
                };
                for (int i = 0; i < items.Count && i < ids.Length; i++)
                {
                    int k = i;
                    Try(ids[k], () =>
                    {
                        items[k].IsSubMenuOpen = true;
                        Settle();
                        Shot(window, ids[k], whats[k], $"The menu bar's {items[k].Tag} menu.", notes: Bar);
                        items[k].IsSubMenuOpen = false;
                        Settle();
                    });
                }
                Try("107-menu-file-recent", () =>
                {
                    var file = items[1];
                    file.IsSubMenuOpen = true;
                    Settle();
                    Hover(ItemOf(file, "Open Recent"));
                    Shot(window, "107-menu-file-recent", "File ▸ Open Recent: the recent songs (Night Drive.bmxml), Clear Menu.", "File ▸ Open Recent.", notes: Bar);
                    file.IsSubMenuOpen = false;
                });
                ((DockPanel)bar.Parent!).Children.Remove(bar);
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 2xx: context menus

        [AvaloniaTest, Order(3)]
        public void ContextMenus()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open("Machines");
            try
            {
                var view = Tab<MachineView>(window, "Machines");
                void MachineMenu(string id, Point at, string what, string how, params string[] path)
                {
                    Try(id, () =>
                    {
                        Click(window, at, MouseButton.Right);
                        var menu = view.Canvas.OpenMenu;
                        Assert.That(menu?.IsOpen, Is.True, "a right click opens a menu");
                        menus.Add(menu!);
                        ItemsControl level = menu!;
                        foreach (var p in path)
                        {
                            var item = ItemOf(level, p);
                            Hover(item);
                            level = item;
                        }
                        Shot(window, id, what, how);
                    });
                }
                MachineMenu("200-machines-menu-canvas", view.PointOf(0.1, 0.85, window), "Machines: the menu on empty space (New ▸, Unmute all, Snap, Select All, Deselect…).", "Right-click empty space in the Machine View.");
                MachineMenu("201-machines-menu-new-generators", view.PointOf(0.1, 0.85, window), "Machines: New ▸ Generators, the installed generators (the machine browser): Neo and Pedal machines.", "Right-click empty space ▸ New ▸ Generators.", "New", "Generators");
                MachineMenu("202-machines-menu-new-effects", view.PointOf(0.1, 0.85, window), "Machines: New ▸ Effects, the installed effects.", "Right-click empty space ▸ New ▸ Effects.", "New", "Effects");
                MachineMenu("203-machines-menu-generator", view.PointOf(demo.Bass, window), "Machines: a generator's menu (Bass, Neo Poly): Mute, Solo, Parameters…, Editor…, MIDI input, Replace, Rename…, Delete.", "Right-click a generator.");
                MachineMenu("204-machines-menu-effect", view.PointOf(demo.Delay, window), "Machines: an effect's menu (Delay, Neo Delay): Mute, Bypass, Parameters…, Editor…, Rename…, Delete.", "Right-click an effect.");
                MachineMenu("205-machines-menu-master", view.PointOf(demo.Master, window), "Machines: the Master's menu (Parameters…, clear the clip indicator).", "Right-click the Master.");

                // The Sequence View's menus.
                window.ShowView("Sequence");
                Settle();
                var seq = Tab<SequenceView>(window, "Sequence");
                Try("210-sequence-menu", () =>
                {
                    var geo = seq.Canvas.Geometry;
                    var p = seq.Canvas.TranslatePoint(new Point(geo.XOf(4 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value;
                    Click(window, p);
                    Click(window, p, MouseButton.Right);
                    Assert.That(seq.OpenMenu?.IsOpen, Is.True);
                    menus.Add(seq.OpenMenu!);
                    Shot(window, "210-sequence-menu", "Sequence: the context menu on a pattern (Add Track ▸, Delete/Move Track, Set Start/End, Time Signature…, Insert/Delete, Color ▸, Rename Machine…, Quick Create Patterns ▸, Cut/Copy/Paste).", "Right-click in the Sequence View (or Ctrl+Enter).");
                });
                Try("211-sequence-menu-colour", () =>
                {
                    var geo = seq.Canvas.Geometry;
                    var p = seq.Canvas.TranslatePoint(new Point(geo.XOf(4 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value;
                    Click(window, p, MouseButton.Right);
                    menus.Add(seq.OpenMenu!);
                    Hover(ItemOf(seq.OpenMenu!, "Color"));
                    Shot(window, "211-sequence-menu-colour", "Sequence: Color ▸, the pattern colours.", "Right-click a pattern ▸ Color.");
                });
                Try("212-sequence-add-track", () =>
                {
                    Click(window, Centre(seq.AddTrackButton, window));
                    Assert.That(seq.OpenMenu?.IsOpen, Is.True);
                    menus.Add(seq.OpenMenu!);
                    Shot(window, "212-sequence-add-track", "Sequence: the + button's menu, a track for one of the song's machines.", "The + button above the track list.");
                });

                // The Session View's menus.
                window.ShowView("Session");
                Settle();
                var session = Tab<SessionView>(window, "Session");
                void SessionMenu(string id, Control target, double fx, string what, string how, params string[] path)
                {
                    Try(id, () =>
                    {
                        Click(window, Centre(target, window, fx), MouseButton.Right);
                        var menu = session.LastMenu;
                        Assert.That(menu?.IsOpen, Is.True, "a menu opened");
                        menus.Add(menu!);
                        ItemsControl level = menu!;
                        foreach (var p in path)
                        {
                            var item = ItemOf(level, p);
                            Hover(item);
                            level = item;
                        }
                        Shot(window, id, what, how);
                    });
                }
                SessionMenu("220-session-menu-clip", session.SlotAt(1, 1), 0.6, "Session: a clip's menu (Launch, Stop, Edit notes…, Rename…, Duplicate, Delete, Colour ▸, Assign pattern ▸, MIDI learn…, Pattern View moves).", "Right-click a clip.");
                SessionMenu("221-session-menu-clip-colour", session.SlotAt(1, 1), 0.6, "Session: a clip's Colour ▸ palette.", "Right-click a clip ▸ Colour.", "Colour");
                SessionMenu("222-session-menu-empty-slot", session.SlotAt(1, 3), 0.6, "Session: an empty slot's menu (Create clip, Assign pattern ▸, MIDI learn…).", "Right-click an empty slot.");
                SessionMenu("223-session-menu-scene", session.Scenes[2], 0.5, "Session: a scene's menu (Launch, Rename…, Tempo…, Time signature…, Colour ▸, Insert/Duplicate/Delete scene, Insert into arrangement…, Follow action).", "Right-click a scene's button.");
                SessionMenu("224-session-menu-track", session.Headers[0], 0.5, "Session: a track header's menu (colour, rename, arm, stop, remove the track…).", "Right-click a track's header.");
                Try("225-session-menu-settings", () =>
                {
                    Click(window, Centre(session.SettingsButton, window));
                    menus.Add(session.LastMenu!);
                    Shot(window, "225-session-menu-settings", "Session: the ⚙ menu (new clip and record length, count-in, sync, controllers, return tracks, cue output, MIDI learn, keyboard shortcuts).", "The toolbar's ⚙ button.");
                });
                Try("226-session-menu-settings-returns", () =>
                {
                    Click(window, Centre(session.SettingsButton, window));
                    menus.Add(session.LastMenu!);
                    Hover(ItemOf(session.LastMenu!, "Return tracks"));
                    Shot(window, "226-session-menu-settings-returns", "Session: ⚙ ▸ Return tracks (Verb is a return).", "⚙ ▸ Return tracks.");
                });
                Try("227-session-menu-add-track", () =>
                {
                    var add = session.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "FlatButton" && c.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "+ Track"));
                    Click(window, Centre(add, window));
                    menus.Add(session.LastMenu!);
                    Shot(window, "227-session-menu-add-track", "Session: + Track, a track for a machine without one.", "The toolbar's + Track button.");
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 3xx: windows and dialogs

        [AvaloniaTest, Order(4)]
        public void WindowsAndDialogs()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() =>
            {
                AudioPage.OutputSource = null;
                AudioPage.InputSource = null;
                AudioPage.ReopenHook = null;
                Dialogs.Responder = null;
                Dialogs.TextResponder = null;
                ArrangementInsertDialog.Responder = null;
            });
            var demo = LoadDemo();
            var window = Open("Machines");
            try
            {
                // Preferences, every page.
                AudioPage.OutputSource = () => new[] { "PortAudio: MacBook Pro Speakers (Core Audio)", "PortAudio: External Headphones (Core Audio)", "PortAudio: Studio Display Speakers (Core Audio)" };
                AudioPage.InputSource = () => new[] { "PortAudio: MacBook Pro Microphone (Core Audio)" };
                AudioPage.ReopenHook = _ => { };
                const string Devices = "Device lists injected (headless has no audio devices; on a Mac PortAudio lists CoreAudio's).";
                var prefs = PreferencesWindow.Open(Studio.Host, PreferencesWindow.AudioPageName);
                Settle();
                string[] pages = prefs.Pages.Select(p => p.Title).ToArray();
                int n = 0;
                foreach (var page in pages)
                {
                    string id = $"{300 + n * 2:000}-prefs-{page.ToLowerInvariant()}";
                    int k = n++;
                    Try(id, () =>
                    {
                        prefs.ShowPage(page);
                        Settle();
                        Shot(prefs, id, $"Preferences ▸ {page} (a window of its own; changes apply as they are made).",
                            page == "Agent" ? $"{Product.Name} ▸ Preferences… (⌘,) ▸ Agent; or Window ▸ Agent connection… (⇧⌘A)." : $"{Product.Name} ▸ Preferences… (⌘,) ▸ {page}.",
                            notes: page == "Audio" ? Devices : null);
                        var scroll = prefs.Selected as ScrollViewer;
                        if (scroll != null && scroll.Extent.Height > scroll.Viewport.Height + 4)
                        {
                            scroll.Offset = new Vector(0, scroll.Extent.Height);
                            Settle();
                            string more = $"{300 + k * 2 + 1:000}-prefs-{page.ToLowerInvariant()}-scrolled";
                            Shot(prefs, more, $"Preferences ▸ {page}, scrolled to its end.", $"{Product.Name} ▸ Preferences… ▸ {page}, scrolled.", notes: page == "Audio" ? Devices : null);
                            scroll.Offset = default;
                        }
                    });
                }
                Try("320-prefs-agent-on", () =>
                {
                    prefs.ShowPage("Agent");
                    var agent = prefs.Page<AgentPage>();
                    agent.AllowBox.IsChecked = true;
                    Wait(300);
                    Shot(prefs, "320-prefs-agent-on", "Preferences ▸ Agent with agents allowed: the listening state and the `claude mcp add` line with its token, Copy and New token.",
                        "Window ▸ Agent connection… (⇧⌘A), then Allow agents.", notes: "The token is this test run's throwaway one.");
                    agent.AllowBox.IsChecked = false;
                    Settle();
                });
                Try("321-prefs-safety-question", () =>
                {
                    prefs.ShowPage("Audio");
                    var audio = prefs.Page<AudioPage>();
                    DialogOnOpen(prefs, () => audio.LimiterBox.IsChecked = false, "321-prefs-safety-question", "The safety question before the limiter goes off (Preferences ▸ Audio ▸ Safety): Turn Off or Keep On, Keep On on Return and Escape.",
                        "Preferences ▸ Audio: untick Safety limiter.", notes: Devices);
                    Settle();
                });
                prefs.Close();
                Settle();

                Try("330-about", () => DialogOnOpen(window, () => AboutWindow.Open(), "330-about", $"About {Product.Name}: the build and the credits.", $"{Product.Name} ▸ About {Product.Name}."));
                Try("331-keyboard-shortcuts", () => DialogOnOpen(window, () => _ = Dialogs.Info(MainWindow.ShortcutsText(mac: true), "Keyboard shortcuts"),
                    "331-keyboard-shortcuts", "Help ▸ Keyboard shortcuts: the studio's keys (Mac glyphs: ⌘ ⇧).", "Help ▸ Keyboard shortcuts…."));
                Try("332-save-changes", () =>
                {
                    Core.SetModifiedFlag();
                    DialogOnOpen(window, () => _ = SongFiles.ConfirmDiscard(), "332-save-changes", "Save changes? before a new song, opening another or closing: Don't Save, Cancel, Save (Return; macOS's order, the default rightmost).",
                        "File ▸ New / Open… / close the window with unsaved changes.");
                    Settle();
                    Core.Modified = false;
                });
                Try("333-rename-machine", () =>
                {
                    var view = Tab<MachineView>(window, "Machines");
                    Click(window, view.PointOf(demo.Lead, window), MouseButton.Right);
                    menus.Add(view.Canvas.OpenMenu!);
                    var rename = ItemOf(view.Canvas.OpenMenu!, "Rename...");
                    DialogOnOpen(window, () => Click(window, Centre(rename, window)), "333-rename-machine", "Rename a machine (the studio's text prompt).", "Right-click a machine ▸ Rename….");
                });
                Try("334-session-insert-arrangement", () =>
                {
                    window.ShowView("Session");
                    Settle();
                    var session = Tab<SessionView>(window, "Session");
                    Click(window, Centre(session.Scenes[1], window), MouseButton.Right);
                    menus.Add(session.LastMenu!);
                    DialogOnOpen(window, () => Assert.That(session.ClickMenuItem("Insert into arrangement…"), Is.True), "334-session-insert-arrangement",
                        "Session: Insert into arrangement (the scene's clips written into the Sequence View at a bar, for some bars).", "Right-click a scene ▸ Insert into arrangement….");
                });
                Try("335-session-scene-tempo", () =>
                {
                    var session = Tab<SessionView>(window, "Session");
                    Click(window, Centre(session.Scenes[2], window), MouseButton.Right);
                    menus.Add(session.LastMenu!);
                    DialogOnOpen(window, () => Assert.That(session.ClickMenuItem("Tempo…"), Is.True), "335-session-scene-tempo", "Session: a scene's tempo prompt.", "Right-click a scene ▸ Tempo….");
                });
                Try("336-sequence-time-signature", () =>
                {
                    window.ShowView("Sequence");
                    Settle();
                    var seq = Tab<SequenceView>(window, "Sequence");
                    var geo = seq.Canvas.Geometry;
                    Click(window, seq.Canvas.TranslatePoint(new Point(geo.XOf(2 * 16) + 6, geo.YOf(1) + geo.TrackHeight * 0.6), window)!.Value, MouseButton.Right);
                    menus.Add(seq.OpenMenu!);
                    var item = ItemOf(seq.OpenMenu!, "Set Time Signature...");
                    DialogOnOpen(window, () => Click(window, Centre(item, window)), "336-sequence-time-signature", "Sequence: Set Time Signature (beats/note, as 4/4, or ticks; checked as it is typed).", "T in the Sequence View, or its menu ▸ Set Time Signature….");
                    window.ShowView("Machines");
                });
                Try("337-autosave-recovery", () =>
                {
                    // As after a crash: the song's autosave left behind, then offered at start-up (Autosave.OfferRecovery).
                    Core.SetModifiedFlag();
                    Assert.That(Core.Autosave.SaveNow(System.DateTime.Now), Is.True);
                    try
                    {
                        DialogOnOpen(window, () => _ = Autosave.OfferRecovery(Core), "337-autosave-recovery", $"At start-up after {Product.Name} didn't close normally: recover the autosaved changes? Discard… (asks again), Decide Later (Escape), Restore (Return).",
                            $"Start {Product.Name} after a crash, with unsaved changes autosaved.");
                    }
                    finally
                    {
                        Settle();
                        foreach (var leftover in SongAutosave.FindLeftovers()) SongAutosave.Delete(leftover.Path);
                        Core.Modified = false;
                    }
                });
                Try("338-error-open", () =>
                {
                    string broken = Path.Combine(SongFolder, "Broken song.bmx");
                    File.WriteAllBytes(broken, Encoding.ASCII.GetBytes("Buzz\0not a song at all"));
                    DialogOnOpen(window, () => _ = SongFiles.Open(broken), "338-error-open", "An error: a file that isn't a song, opened. What happened in words; the exception under Details (closed).", "File ▸ Open… a damaged file.");
                });
            }
            finally
            {
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 4xx: machine windows

        [AvaloniaTest, Order(5)]
        public void MachineWindows()
        {
            BeforeAll();
            var demo = LoadDemo();
            var window = Open("Machines");
            var made = new List<MachineCore>();
            // The machine windows on the Mac the gallery draws (1440 × 900 pt, less its 25 pt menu bar): the headless
            // platform's screen is 1920 × 1280, and it cuts windows off at 1080. They keep inside the work area and scroll.
            var workArea = MachineWindowFit.WorkArea;
            MachineWindowFit.WorkArea = _ => new Size(Width, Height - 25);
            try
            {
                MachineCore Add(string gear, string name)
                {
                    Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, 0.5f, 0.8f);
                    var m = Core.SongCore.MachinesList.Last(x => x.Name == name);
                    made.Add(m);
                    return m;
                }

                // Every Neo machine's editor, as a double click opens it.
                var neo = Core.MachineDLLs.Values.Where(d => d?.Info != null && d.Name.StartsWith("Neo ") && d.Info.Type != MachineType.Master)
                    .Select(d => d.Name).OrderBy(x => x, System.StringComparer.Ordinal).ToList();
                int i = 0;
                foreach (var gear in neo)
                {
                    string id = $"{400 + i++:000}-editor-{gear.ToLowerInvariant().Replace(' ', '-')}";
                    Try(id, () =>
                    {
                        var m = demo.Generators.Concat(new[] { demo.Drive, demo.Comp, demo.Delay, demo.Verb, demo.Eq, demo.Limiter }).FirstOrDefault(x => x?.DLL.Name == gear) ?? Add(gear, gear.Replace("Neo ", ""));
                        Core.UiHost.OpenGui(m, null);
                        var editor = StudioMachineUiHost.GuiWindowOf(m);
                        Assert.That(editor, Is.Not.Null, "its GUI opened");
                        var busy = m.ManagedMachine?.GetType().GetProperty("Busy");
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        while (busy?.GetValue(m.ManagedMachine) is true && clock.ElapsedMilliseconds < 5000) Thread.Sleep(20);
                        Wait(200);
                        Shot(editor!, id, $"{gear}'s editor ({m.Name}), as a double click opens it: a window of its own." + KeyboardUnder(m), "Double-click the machine in the Machine View.");
                        Core.UiHost.CloseWindows(m);
                    });
                }

                // The generic parameter window: a Neo machine, a Pedal synth, a Pedal effect.
                void Parameters(string id, MachineCore m, string what, string how)
                {
                    Try(id, () =>
                    {
                        Core.UiHost.OpenParameterWindow(m, null);
                        var w = StudioMachineUiHost.ParameterWindowOf(m);
                        Assert.That(w, Is.Not.Null, "the parameter window opened");
                        Settle();
                        Shot(w!, id, what + KeyboardUnder(m), how);
                        Core.UiHost.CloseWindows(m);
                    });
                }
                Parameters("450-parameters-neo-poly", demo.Bass, "The parameter window of Bass (Neo Poly): every parameter as a slider with its value, presets, track choice.", "Right-click a machine ▸ Parameters….");
                string synth = new[] { "Pedal SH101", "Pedal Juno106", "Pedal M1", "Pedal FM" }.FirstOrDefault(Core.MachineDLLs.ContainsKey);
                string effect = new[] { "Pedal Comp", "Pedal Plate", "Pedal Chorus" }.FirstOrDefault(Core.MachineDLLs.ContainsKey);
                if (synth != null) Parameters("451-parameters-pedal-synth", Add(synth, synth.Replace("Pedal ", "")), $"The parameter window of a Pedal synth ({synth}): the generic view (Pedal machines have no editor of their own here).", "Double-click a Pedal machine, or its menu ▸ Parameters….");
                else failures.Add("mac-451: no Pedal synth in Gear");
                if (effect != null) Parameters("452-parameters-pedal-effect", Add(effect, effect.Replace("Pedal ", "")), $"The parameter window of a Pedal effect ({effect}).", "Double-click a Pedal machine, or its menu ▸ Parameters….");
                else failures.Add("mac-452: no Pedal effect in Gear");
                Try("453-parameters-menu", () =>
                {
                    Core.UiHost.OpenParameterWindow(demo.Bass, null);
                    var w = StudioMachineUiHost.ParameterWindowOf(demo.Bass)!;
                    Settle();
                    var row = w.GetVisualDescendants().OfType<Control>().First(c => c.GetType().Name == "ParameterRow");
                    Click(w, Centre(row, w, 0.1), MouseButton.Right);
                    Shot(w, "453-parameters-menu", "A parameter's menu in the parameter window (right-click on a row).", "Right-click a parameter in the parameter window.");
                    Core.UiHost.CloseWindows(demo.Bass);
                });

                // The machine keyboard (docs/specs/studio-mvp.md §3.12): playing, and hidden.
                Try("470-keyboard-playing", () =>
                {
                    Core.UiHost.OpenGui(demo.Bass, null);
                    var w = StudioMachineUiHost.GuiWindowOf(demo.Bass)!;
                    Wait(200);
                    var kb = StudioMachineUiHost.KeyboardOf(w);
                    Assert.That(kb, Is.Not.Null, "Bass's editor has the keyboard");
                    var key = kb!.Keys.ElementOf(69);
                    Assert.That(key, Is.Not.Null, "A4 is on the keyboard");
                    var at = key!.TranslatePoint(new Point(key.Bounds.Width / 2, key.Bounds.Height * 0.85), w)!.Value;
                    w.MouseMove(at);
                    w.MouseDown(at, MouseButton.Left);
                    foreach (var note in new[] { 48, 55, 64 }) Core.MachineManager.SendMidiInput(demo.Bass, 0x90 | (note << 8) | (72 << 16), false);
                    try
                    {
                        for (int i = 0; i < 4; i++) kb.Poll();
                        Wait(100);
                        Shot(w, "470-keyboard-playing", "Bass's editor (Neo Poly) with its keyboard playing: A4 held with the pointer low on the key (loud: the readout names the note and its velocity, the fill shows it), and C3, G3 and E4 lit from a MIDI keyboard. Keys light whatever plays them (a pointer, MIDI, the machine's own patterns).",
                            "Click and hold a key under a machine's window; drag across the keys to glide. Lower on a key plays louder.");
                    }
                    finally
                    {
                        w.MouseUp(at, MouseButton.Left);
                        foreach (var note in new[] { 48, 55, 64 }) Core.MachineManager.SendMidiInput(demo.Bass, 0x80 | (note << 8), false);
                        Settle();
                        Core.UiHost.CloseWindows(demo.Bass);
                    }
                });
                Try("471-keyboard-hidden", () =>
                {
                    Core.UiHost.OpenParameterWindow(demo.Bass, null);
                    var w = StudioMachineUiHost.ParameterWindowOf(demo.Bass)!;
                    Settle();
                    var kb = StudioMachineUiHost.KeyboardOf(w)!;
                    kb.SetShown(false);                    // not remembered: the gallery leaves the machine type's choice alone
                    try
                    {
                        Settle();
                        Shot(w, "471-keyboard-hidden", "Bass's parameter window with its keyboard hidden: the strip stays, its ▸ Keyboard button shows the keys again (the window shrinks and grows by them; the choice is kept per machine type).",
                            $"The ▾ Keyboard button over the keys, or {(System.OperatingSystem.IsMacOS() ? "⌘K" : "Ctrl+K")} in the window. Preferences ▸ General ▸ Machine Keyboard sets the default.");
                    }
                    finally
                    {
                        kb.SetShown(true);
                        Core.UiHost.CloseWindows(demo.Bass);
                    }
                });
                if (synth != null)
                {
                    Try("460-manifest-editor-pedal-synth", () =>
                    {
                        var m = made.First(x => x.DLL.Name == synth);
                        Assert.That(Core.UiHost.OpenEditor(m), Is.True, "Editor... opened");
                        var w = StudioMachineUiHost.EditorWindowOf(m);
                        Assert.That(w, Is.Not.Null);
                        Wait(200);
                        Shot(w!, "460-manifest-editor-pedal-synth", $"{synth}'s manifest editor (Editor…): its curated face, knobs in sections, the preset bar.", "Right-click a machine ▸ Editor… (or a double click, where Double-click Opens Editor is on).");
                        Core.UiHost.CloseWindows(m);
                    });
                }
            }
            finally
            {
                MachineWindowFit.WorkArea = workArea;
                foreach (var m in made) Core.UiHost.ReleaseMachine(m);
                Close(window);
            }
            AfterAll();
        }

        // ------------------------------------------------------------------ 5xx: old songs, natives, safety, many machines

        [AvaloniaTest, Order(6)]
        public void SongsAndStates()
        {
            BeforeAll();
            using var restore = Studio.AfterTest(() =>
            {
                MachineGuard.Enabled = true;
                Core.SafeMaster.Reset();
            });
            string repo = System.AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReBuzz.sln"))) repo = Path.GetDirectoryName(repo);
            NewSong();
            var window = Open("Machines");
            try
            {
                string old = repo == null ? null : Path.Combine(repo, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx");
                if (old != null && File.Exists(old))
                {
                    const string Song = "FSM_Infector_Demo.bmx (a Windows Buzz song)";
                    const string Natives = "Native (Windows) machines don't load on the Mac: they open as placeholders that keep their data.";
                    Dialogs.Responder = (m, c, b) => UserAnswer.No;
                    try
                    {
                        Assert.That(Await(SongFiles.Open(old)), Is.True, "the old song opened");
                    }
                    finally
                    {
                        Dialogs.Responder = null;
                    }
                    Settle();
                    Try("500-old-song-machines", () => Shot(window, "500-old-song-machines", "An old Windows song with native machines: each a placeholder box (the song plays without them and saves them back unchanged).", "File ▸ Open… a .bmx made on Windows.", Song, Natives));
                    Try("501-old-song-sequence", () => { window.ShowView("Sequence"); Shot(window, "501-old-song-sequence", "The old song's arrangement.", "F4.", Song, Natives); });
                    Try("502-old-song-placeholder-parameters", () =>
                    {
                        var native = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type != MachineType.Master);
                        Core.UiHost.OpenParameterWindow(native, null);
                        var w = StudioMachineUiHost.ParameterWindowOf(native);
                        Assert.That(w, Is.Not.Null);
                        Settle();
                        Shot(w!, "502-old-song-placeholder-parameters", $"The parameter window of a placeholder ({native.Name}, {native.DLL.Name}).", "Right-click a placeholder ▸ Parameters….", Song, Natives);
                        Core.UiHost.CloseWindows(native);
                    });
                    window.ShowView("Machines");
                    NewSong();
                }
                else failures.Add("mac-500..502: the old demo song isn't in this checkout");

                // Many machines: every Neo and Pedal machine in one song.
                Try("510-machines-many", () =>
                {
                    NewSong();
                    var gear = Core.MachineDLLs.Values.Where(d => d?.Info != null && d.Info.Type != MachineType.Master && (d.Name.StartsWith("Neo ") || d.Name.StartsWith("Pedal ")))
                        .Select(d => d.Name).OrderBy(x => x).ToList();
                    int cols = 6;
                    for (int k = 0; k < gear.Count; k++)
                    {
                        float x = -0.9f + 1.6f * (k % cols) / (cols - 1), y = -0.85f + 1.6f * (k / cols) / System.Math.Max(1, (gear.Count - 1) / cols);
                        // As right-click ▸ New makes them (a spot taken moves to free space).
                        var instrument = Core.Instruments.First(i => i.MachineDLL?.Name == gear[k]);
                        var made = ReBuzz.Studio.Views.Machines.MachineEdits.Create(Core, instrument, x, y);
                        if (made is MachineCore mc) mc.Name = gear[k].Replace("Neo ", "").Replace("Pedal ", "P ");
                    }
                    Settle();
                    window.ShowView("Machines");
                    Shot(window, "510-machines-many", $"Machines with many machines: every Neo and Pedal machine ({gear.Count}) in one song.", "Add machines (right-click ▸ New).", song: "a song of every Neo and Pedal machine");
                });

                // Safety: a machine that runs away, the guard's state.
                if (Core.MachineDLLs.ContainsKey("Guard Test"))
                {
                    Try("520-safety-diverged", () =>
                    {
                        NewSong();
                        MachineGuard.Enabled = true;
                        var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
                        Core.SongCore.CreateMachine("Guard Test", null, "Runaway", null, null, null, -1, -0.3f, -0.3f);
                        var m = Core.SongCore.MachinesList.Last(x => x.Name == "Runaway");
                        Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                        m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == "Mode").SetValue(0, 1);
                        for (int b = 0; b < 40; b++) lock (ReBuzzCore.AudioLock) { work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings); work.MainAudioFillBuffer(buffer, 0, 512); }
                        Settle();
                        window.RefreshNow();
                        var view = Tab<MachineView>(window, "Machines");
                        window.MouseMove(view.PointOf(m, window));
                        Settle();
                        Shot(window, "520-safety-diverged", "Safety: a machine whose signal ran away (the guard tripped): drawn red and muted, the status line says why.", "Happens when a machine goes wild (NaN, infinity, over +60 dBFS).",
                            song: "a test song (one misbehaving machine)", notes: "Guard Test is the tests' own machine, built to diverge.");
                        Click(window, view.PointOf(m, window), MouseButton.Right);
                        menus.Add(view.Canvas.OpenMenu!);
                        Shot(window, "521-safety-diverged-menu", "Safety: the diverged machine's menu, Reset and unmute first.", "Right-click the red machine.", song: "a test song (one misbehaving machine)");
                    });
                }
                else failures.Add("mac-520: the Guard Test machine isn't in the tests' Gear");
            }
            finally
            {
                NewSong();
                Close(window);
            }
            AfterAll();
        }
    }
}
