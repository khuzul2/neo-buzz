// Fork (docs/specs/studio-mvp.md S3): the machine view, headless: a chain built from an empty song
// with the mouse alone (gear menu, Shift-drag, the amp control, the LED, the context menus, the
// Delete key), each step checked against the engine's graph and undone with Ctrl+Z; an opened
// song's layout; zoom and scroll; an effect dropped into a wire. Screenshots of each stage.

using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Machines;

namespace ReBuzz.Studio.Tests
{
    public class MachineViewTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        /// <summary>Command on macOS (the Mac CI job runs these too), Control elsewhere.</summary>
        internal static RawInputModifiers Cmd => System.OperatingSystem.IsMacOS() ? RawInputModifiers.Meta : RawInputModifiers.Control;

        /// <summary>The main window on a new, empty song, with the machine view showing.</summary>
        internal static (MainWindow Window, MachineView View) OpenEmpty()
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;     // never keep unsaved changes here
            SongFiles.New().Wait();
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            window.Tabs.SelectedIndex = 0;
            Settle();
            var view = (MachineView)((TabItem)window.Tabs.Items[0]!).Content!;
            Assert.That(Graph(), Is.EqualTo("Master"), "a new song has Master alone");
            return (window, view);
        }

        internal static void Close(MainWindow window)
        {
            window.Close();         // the song has changes: the responder drops them, so no dialog stays behind
            Settle();
            Dialogs.Responder = null;
            Dialogs.TextResponder = null;
        }

        static System.TimeSpan menuDelay;

        // Submenus open and close on hover after a delay of wall-clock time; a slow step (the first
        // render) would let those timers fire mid-test. The tests click, so the delay is put off.
        [SetUp]
        public void PutOffMenuHoverDelays()
        {
            menuDelay = Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay;
            Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay = System.TimeSpan.FromHours(1);
        }

        [TearDown]
        public void RestoreMenuHoverDelays() => Avalonia.Controls.Platform.DefaultMenuInteractionHandler.MenuShowDelay = menuDelay;

        internal static void Settle()
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The song's machines and wires, as text: "Master; A→B 100% C; …".</summary>
        internal static string Graph()
        {
            var machines = Core.SongCore.Machines.Select(m => m.Name).OrderBy(n => n);
            var wires = Core.SongCore.Machines.SelectMany(m => m.Inputs).Select(c => $"{c.Source.Name}→{c.Destination.Name}").OrderBy(n => n);
            return string.Join("; ", machines.Concat(wires));
        }

        internal static IMachine Machine(string name) => Core.SongCore.Machines.FirstOrDefault(m => m.Name == name);

        internal static IMachine MachineOf(string gear) => Core.SongCore.Machines.FirstOrDefault(m => m.DLL.Name == gear);

        internal static IMachineConnection Wire(string from, string to) => Machine(from)?.Outputs.FirstOrDefault(c => c.Destination.Name == to);

        internal static void Click(TopLevel top, Point p, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None)
        {
            top.MouseMove(p, mods);
            top.MouseDown(p, button, mods);
            top.MouseUp(p, button, mods);
            Settle();
        }

        /// <summary>Two clicks with nothing between them, well inside the double-click time.</summary>
        internal static void DoubleClick(TopLevel top, Point p, MouseButton button = MouseButton.Left)
        {
            top.MouseMove(p);
            top.MouseDown(p, button);
            top.MouseUp(p, button);
            top.MouseDown(p, button);
            top.MouseUp(p, button);
            Settle();
        }

        internal static void Drag(TopLevel top, Point from, Point to, MouseButton button = MouseButton.Left, RawInputModifiers mods = RawInputModifiers.None, string screenshotOnTheWay = null)
        {
            top.MouseMove(from, mods);
            top.MouseDown(from, button, mods);
            for (int i = 1; i <= 4; i++)
                top.MouseMove(from + (to - from) * (i / 4.0), mods | Held(button));
            if (screenshotOnTheWay != null)
            {
                Settle();
                Studio.Screenshot(top, screenshotOnTheWay);
            }
            top.MouseUp(to, button, mods);
            Settle();
        }

        static RawInputModifiers Held(MouseButton b) => b switch
        {
            MouseButton.Left => RawInputModifiers.LeftMouseButton,
            MouseButton.Right => RawInputModifiers.RightMouseButton,
            MouseButton.Middle => RawInputModifiers.MiddleMouseButton,
            _ => RawInputModifiers.None,
        };

        /// <summary>Ctrl+Z (Command+Z on a Mac).</summary>
        static void Undo(MainWindow window)
        {
            window.KeyPressQwerty(PhysicalKey.Z, Cmd);
            Settle();
        }

        /// <summary>A right click on the canvas, then a path through the menu it opened, clicked with the mouse.</summary>
        internal static void Menu(MainWindow window, MachineView view, Point at, string screenshot, params string[] path)
        {
            Click(window, at, MouseButton.Right);
            var menu = view.Canvas.OpenMenu;
            Assert.That(menu, Is.Not.Null, "a right click opens a menu");
            Assert.That(menu!.IsOpen, Is.True);
            ItemsControl level = menu;
            for (int i = 0; i < path.Length; i++)
            {
                var item = level.Items.OfType<MenuItem>().FirstOrDefault(x => x.Header as string == path[i]);
                Assert.That(item, Is.Not.Null, $"the menu has {path[i]} (it has {string.Join(", ", level.Items.OfType<MenuItem>().Select(x => x.Header))})");
                ClickItem(item!);
                if (i < path.Length - 1)
                {
                    Assert.That(item!.IsSubMenuOpen, Is.True, $"{path[i]} opens its submenu");
                    if (i == path.Length - 2 && screenshot != null)
                        Studio.Screenshot(TopLevel.GetTopLevel(item)!, screenshot);
                }
                level = item!;
            }
            Assert.That(menu.IsOpen, Is.False, "the menu closes once an item is chosen");
        }

        internal static void ClickItem(MenuItem item)
        {
            var top = TopLevel.GetTopLevel(item)!;
            top.UpdateLayout();
            Assert.That(item.IsEffectivelyVisible && item.Bounds.Width > 0, Is.True, $"{item.Header} is showing");
            var centre = item.TranslatePoint(new Point(item.Bounds.Width / 2, item.Bounds.Height / 2), top)!.Value;
            Click(top, centre);
        }

        [AvaloniaTest]
        public void AChainFromAnEmptySongWithTheMouseAloneAndEachStepUndoes()
        {
            var (window, view) = OpenEmpty();
            try
            {
                Studio.Screenshot(window, "machines-1-empty");
                var master = Machine("Master");
                Near(view.Canvas.BoxOf(master).Center, view.Canvas.Map.PointAt(master.Position.Item1, master.Position.Item2), "Master's box is at its position");

                // Add Neo Poly from the gear menu: as in WPF, a generator in a song with no effects is wired to Master.
                var polyAt = view.PointOf(-0.5, -0.4, window);
                Menu(window, view, polyAt, "machines-2-gear-menu", "New", "Generators", "Neo Poly");
                var poly = MachineOf("Neo Poly");
                Assert.That(poly, Is.Not.Null, "Neo Poly was added: " + Graph());
                Assert.That(poly!.Position.Item1, Is.EqualTo(-0.5).Within(0.01));
                Assert.That(poly.Position.Item2, Is.EqualTo(-0.4).Within(0.01));
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Poly→Master"));
                Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { poly }), "the new machine is selected");

                // Add Neo Verb.
                Menu(window, view, view.PointOf(0.3, -0.4, window), null, "New", "Effects", "Neo Verb");
                var verb = MachineOf("Neo Verb");
                Assert.That(verb, Is.Not.Null, "Neo Verb was added");
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master"));
                Studio.Screenshot(window, "machines-3-added");

                // Shift-drag from Poly to Verb, and from Verb to Master, connects them.
                Drag(window, view.PointOf(poly, window), view.PointOf(verb!, window), mods: RawInputModifiers.Shift, screenshotOnTheWay: "machines-4-connecting");
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master; Poly→Verb"));
                Drag(window, view.PointOf(verb!, window), view.PointOf(master, window), mods: RawInputModifiers.Shift);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master; Poly→Verb; Verb→Master"));

                // The amp control's disconnect button takes the direct wire away.
                Click(window, view.ArrowOf(Wire("Poly", "Master")!, window), MouseButton.Right);
                Assert.That(view.Amp.IsVisible, Is.True, "a right click on an arrow opens the amp control");
                Click(window, AmpPart(view, window, "disconnect"));
                Assert.That(view.Amp.IsVisible, Is.False);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Verb; Verb→Master"));
                Studio.Screenshot(window, "machines-5-chain");

                // Move Verb down and right.
                var verbBefore = verb!.Position;
                var verbPoint = view.PointOf(verb, window);
                Drag(window, verbPoint, verbPoint + new Vector(60, 120));
                var moved = view.Canvas.Map.PositionDelta(new Vector(60, 120) / view.Canvas.Zoom);
                Assert.That(verb.Position.Item1, Is.EqualTo(verbBefore.Item1 + moved.X).Within(0.001));
                Assert.That(verb.Position.Item2, Is.EqualTo(verbBefore.Item2 + moved.Y).Within(0.001));
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Verb; Verb→Master"), "a move keeps the wires");

                // Amp and pan on Verb→Master: press the arrow and drag down (the amp knob turns), then the pan knob up.
                var out1 = Wire("Verb", "Master")!;
                Assert.That((out1.Amp, out1.Pan, out1.HasPan), Is.EqualTo((0x4000, 0x4000, true)));
                var arrow = view.ArrowOf(out1, window);
                Drag(window, arrow, arrow + new Vector(0, 20));
                Assert.That(view.Amp.IsVisible, Is.True, "the amp control stays while the pointer is on it");
                Assert.That(view.Amp.AmpTextValue, Is.EqualTo("-12.2dB"));
                var pan = AmpPart(view, window, "pan");
                Drag(window, pan, pan + new Vector(0, -30));
                Assert.That(view.Amp.PanTextValue, Is.EqualTo("44R"));
                Studio.Screenshot(window, "machines-6-amp-control");
                window.MouseMove(arrow + new Vector(300, 200));     // leaving it makes the change one undo step
                Settle();
                Assert.That(view.Amp.IsVisible, Is.False);
                var out2 = Wire("Verb", "Master")!;
                Assert.That(out2.Amp, Is.EqualTo(4025), "-12.2 dB: 20 px down of the knob's 128");
                Assert.That(out2.Pan, Is.EqualTo(0x4000 + 7209), "30 px up, less the 8 px the centre holds");

                // Mute Poly with its LED; solo it from its menu.
                var polyBox = view.Canvas.BoxOf(poly);
                Click(window, view.FromCanvas(MachineGeometry.Led(polyBox).Center, window));
                Assert.That(poly.IsMuted, Is.True, "the LED mutes");
                Menu(window, view, view.PointOf(poly, window), null, "Solo");
                Assert.That(poly.IsSoloed, Is.True, "Solo from the machine's menu");
                Studio.Screenshot(window, "machines-7-muted-soloed");

                // Rename Verb from its menu (IMachineUiHost.AskName → the studio's dialog).
                Dialogs.TextResponder = (title, current) => current == "Verb" ? "Hall" : null;
                Menu(window, view, view.PointOf(verb, window), null, "Rename...");
                Assert.That(Graph(), Is.EqualTo("Hall; Master; Poly; Hall→Master; Poly→Hall"));

                // Delete Verb with the Delete key: as in WPF, its source is wired on to its destination.
                Click(window, view.PointOf(verb, window) + Aside);
                Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { verb }));
                window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
                Settle();
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Poly→Master"));
                Studio.Screenshot(window, "machines-8-deleted");

                // Undo each step (Ctrl+Z), checking the graph each time.
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Hall; Master; Poly; Hall→Master; Poly→Hall"), "undo delete");
                var hallOut = Wire("Hall", "Master")!;
                Assert.That((hallOut.Amp, hallOut.Pan), Is.EqualTo((out2.Amp, out2.Pan)), "the deleted wire comes back as it was");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Verb; Verb→Master"), "undo rename");
                Undo(window);
                Assert.That(Machine("Poly")!.IsSoloed, Is.False, "undo solo");
                Undo(window);
                Assert.That(Machine("Poly")!.IsMuted, Is.False, "undo mute");
                Undo(window);
                var back = Wire("Verb", "Master")!;
                Assert.That((back.Amp, back.Pan), Is.EqualTo((0x4000, 0x4000)), "undo amp and pan");
                Undo(window);
                Assert.That(Machine("Verb")!.Position, Is.EqualTo(verbBefore), "undo move");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master; Poly→Verb; Verb→Master"), "undo disconnect");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master; Poly→Verb"), "undo connect");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master"), "undo connect");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Poly→Master"), "undo add");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master"), "undo add");
                Studio.Screenshot(window, "machines-9-undone");
            }
            finally
            {
                Close(window);
            }
        }

        /// <summary>A part of the open amp control, in the window's pixels.</summary>
        internal static Point AmpPart(MachineView view, TopLevel window, string part)
        {
            var amp = view.Amp;
            var local = part switch
            {
                "amp" => new Point(17, 17),
                "pan" => new Point(17, 45),
                "insert" => new Point(24, amp.Height - 24),
                _ => new Point(62, amp.Height - 24),
            };
            return amp.TranslatePoint(local, window)!.Value;
        }

        /// <summary>Off a machine's centre (where tests last pressed), so a click there isn't taken for a double click.</summary>
        static readonly Vector Aside = new Vector(14, 9);

        static void Near(Point actual, Point expected, string what) =>
            Assert.That(System.Math.Abs(actual.X - expected.X) <= 1 && System.Math.Abs(actual.Y - expected.Y) <= 1, Is.True, $"{what}: {actual} for {expected}");

        static IMachine Add(string gear, float x, float y) =>
            MachineEdits.Create(Core, Core.Instruments.First(i => i.MachineDLL.Name == gear && i.Name.Length == 0), x, y);

        [AvaloniaTest]
        public void ADoubleClickOpensTheGuiAndTheMenuOpensTheParameters()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var poly = Add("Neo Poly", -0.5f, 0);
                var verb = Add("Neo Verb", 0.5f, 0);
                Settle();
                DoubleClick(window, view.PointOf(poly, window));
                // Until S1 gives Neo Poly its editor here, its GUI is its parameter window (StudioMachineUiHost).
                var gui = StudioMachineUiHost.GuiWindowOf(poly) ?? StudioMachineUiHost.ParameterWindowOf(poly);
                Assert.That(gui?.IsVisible, Is.True, "a double click opens the machine's GUI");
                Assert.That(Core.MIDIFocusMachine, Is.EqualTo(poly), "a click gives a generator MIDI focus");

                Menu(window, view, view.PointOf(verb, window), null, "Parameters...");
                Assert.That(StudioMachineUiHost.ParameterWindowOf(verb)?.IsVisible, Is.True, "Parameters... opens the parameter window");

                // Bypass, from the effect's menu.
                Menu(window, view, view.PointOf(verb, window), null, "Bypass");
                Assert.That(verb.IsBypassed, Is.True);
                Settle();
                Studio.Screenshot(window, "machines-10-bypassed");
                Core.UiHost.ReleaseMachine(poly);
                Core.UiHost.ReleaseMachine(verb);
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AnOpenedSongIsLaidOutAtItsStoredPositions()
        {
            var (window, view) = OpenEmpty();
            string dir = Path.Combine(Path.GetTempPath(), "rebuzz-studio-machines-" + System.Environment.ProcessId);
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "layout.bmxml");
            try
            {
                // A song made by the engine alone, saved and opened again.
                var stored = new (string Gear, float X, float Y)[] { ("Neo Poly", -0.9f, -0.8f), ("Neo FM6", -0.9f, 0.7f), ("Neo Drive", 0.25f, -0.5f), ("Neo Verb", 0.8f, 0.6f) };
                foreach (var m in stored)
                    Core.SongCore.CreateMachine(m.Gear, null, null, null, null, null, -1, m.X, m.Y);
                var byGear = stored.ToDictionary(m => m.Gear);
                Core.SongCore.ConnectMachines(MachineOf("Neo Poly"), MachineOf("Neo Drive"), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(MachineOf("Neo Drive"), Machine("Master"), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(MachineOf("Neo FM6"), MachineOf("Neo Verb"), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(MachineOf("Neo Verb"), Machine("Master"), new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                SongFiles.Picker = save => path;
                Assert.That(SongFiles.SaveAs().Result, Is.True);
                SongFiles.New().Wait();
                Assert.That(Graph(), Is.EqualTo("Master"));
                Assert.That(SongFiles.Open(path).Result, Is.True);
                Settle();
                Assert.That(Graph(), Is.EqualTo("Drive; FM6; Master; Poly; Verb; Drive→Master; FM6→Verb; Poly→Drive; Verb→Master"));

                // At zoom 0, scrolled to the middle, a position -1..1 spans the view (MachineCanvas): the
                // box's centre is at half the view plus the position times half the view.
                var size = view.Canvas.Bounds.Size;
                foreach (var m in Core.SongCore.Machines)
                {
                    var (x, y) = byGear.TryGetValue(m.DLL.Name, out var s) ? (s.X, s.Y) : (0f, 0f);
                    Assert.That(m.Position, Is.EqualTo(System.Tuple.Create(x, y)), $"{m.Name} kept its position");
                    var expected = new Point(size.Width / 2 + x * size.Width / 2, size.Height / 2 + y * size.Height / 2);
                    Near(view.Canvas.ToView(view.Canvas.BoxOf(m).Center), expected, m.Name);
                    // And it is there to click.
                    Click(window, view.Canvas.TranslatePoint(expected, window)!.Value);
                    Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { m }), $"a click at {m.Name}'s stored position selects it");
                }
                Studio.Screenshot(window, "machines-11-opened-song");
            }
            finally
            {
                SongFiles.Picker = null;
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AnOldSongOpensWithItsNativeMachinesAsPlaceholders()
        {
            string repo = System.AppContext.BaseDirectory;
            while (repo != null && !File.Exists(Path.Combine(repo, "ReBuzz.sln"))) repo = Path.GetDirectoryName(repo);
            string song = repo == null ? null : Path.Combine(repo, "ExternalMachines", "source", "buzztrax-buzzmachines", "FSM", "Infector", "FSM_Infector_Demo.bmx");
            if (song == null || !File.Exists(song)) Assert.Ignore("the demo song isn't in this checkout");
            var (window, view) = OpenEmpty();
            try
            {
                Assert.That(SongFiles.Open(song).Result, Is.True);
                Settle();
                var machines = Core.SongCore.Machines.ToList();
                Assert.That(machines.Count, Is.GreaterThan(1));
                foreach (var m in machines)
                    Near(view.Canvas.BoxOf(m).Center, view.Canvas.Map.PointAt(System.Math.Clamp(m.Position.Item1, -2, 2), System.Math.Clamp(m.Position.Item2, -2, 2)), m.Name);
                Studio.Screenshot(window, "machines-12-old-song");
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ZoomAndScroll()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var poly = Add("Neo Poly", 0.6f, 0.5f);
                Settle();
                var canvas = view.Canvas;
                var start = canvas.Offset;
                Assert.That(canvas.ZoomLevel, Is.EqualTo(0));

                // Ctrl + wheel zooms around the pointer: the machine under it stays under it.
                var at = view.PointOf(poly, window);
                window.MouseWheel(at, new Vector(0, 1), Cmd);
                Settle();
                Assert.That(canvas.ZoomLevel, Is.EqualTo(1));
                Assert.That(view.ZoomSlider.Value, Is.EqualTo(1), "the slider follows");
                Near(view.PointOf(poly, window), at, "the machine under the pointer");
                for (int i = 0; i < 7; i++)
                {
                    // Scroll offsets are whole pixels (as WPF's sv_ScrollChanged makes them): each step may move it by one.
                    var before = view.PointOf(poly, window);
                    window.MouseWheel(before, new Vector(0, 1), Cmd);
                    Settle();
                    Near(view.PointOf(poly, window), before, $"the machine under the pointer at level {canvas.ZoomLevel}");
                }
                Assert.That(canvas.ZoomLevel, Is.EqualTo(8));
                Assert.That(canvas.Zoom, Is.EqualTo(2.0));
                at = view.PointOf(poly, window);
                Assert.That(canvas.BoxOf(poly).Width * canvas.Zoom, Is.EqualTo(200), "boxes scale too");
                Studio.Screenshot(window, "machines-13-zoomed-in");

                // The wheel scrolls; a middle drag and a Shift drag on empty space pan.
                var o = canvas.Offset;
                window.MouseWheel(at, new Vector(0, -1));
                Settle();
                Assert.That(canvas.Offset, Is.EqualTo(o + new Vector(0, 50)));
                var empty = view.Canvas.TranslatePoint(new Point(40, 40), window)!.Value;
                o = canvas.Offset;
                Drag(window, empty, empty + new Vector(30, 20), MouseButton.Middle);
                Assert.That(canvas.Offset, Is.EqualTo(o - new Vector(30, 20)));
                o = canvas.Offset;
                Drag(window, empty + new Vector(30, 20), empty, mods: RawInputModifiers.Shift);
                Assert.That(canvas.Offset, Is.EqualTo(o + new Vector(30, 20)));

                // The slider zooms out; a middle double click goes back to zoom 0, centred.
                view.ZoomSlider.Value = -8;
                Settle();
                Assert.That(canvas.ZoomLevel, Is.EqualTo(-8));
                Studio.Screenshot(window, "machines-14-zoomed-out");
                DoubleClick(window, empty, MouseButton.Middle);
                Assert.That(canvas.ZoomLevel, Is.EqualTo(0));
                Assert.That(canvas.Offset, Is.EqualTo(start));
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AnEffectDroppedOnAWireGoesIntoItAndSelectionWorksAsInBuzz()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var poly = Add("Neo Poly", -0.6f, 0);
                var verb = Add("Neo Verb", 0, -0.6f);
                Settle();
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master"));

                // Drag the unwired effect onto the middle of Poly→Master (MachineControl: within 15 px).
                var middle = view.PointOf(-0.3, 0, window);
                Drag(window, view.PointOf(verb, window), middle);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Verb; Verb→Master"));
                Assert.That(verb.Position.Item1, Is.EqualTo(-0.3).Within(0.01));
                Studio.Screenshot(window, "machines-15-dropped-into-wire");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master"), "one undo step");
                Assert.That(verb.Position, Is.EqualTo(System.Tuple.Create(0f, -0.6f)));

                // A rubber band selects what it touches; dragging one selected machine moves them all, in one undo step.
                var master = Machine("Master");
                var corner = view.PointOf(-0.9, -0.9, window);
                Drag(window, corner, view.PointOf(0.1, 0.1, window), screenshotOnTheWay: "machines-16-rubber-band");
                Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { poly, verb, master }));
                var polyAt = poly.Position;
                var verbAt = verb.Position;
                var masterAt = master.Position;
                Drag(window, view.PointOf(poly, window), view.PointOf(poly, window) + new Vector(0, 100));
                Assert.That(poly.Position.Item2, Is.GreaterThan(polyAt.Item2));
                Assert.That(verb.Position.Item2 - verbAt.Item2, Is.EqualTo(poly.Position.Item2 - polyAt.Item2).Within(1e-5));
                Assert.That(master.Position.Item2 - masterAt.Item2, Is.EqualTo(poly.Position.Item2 - polyAt.Item2).Within(1e-5));
                Undo(window);
                Assert.That((poly.Position, verb.Position, master.Position), Is.EqualTo((polyAt, verbAt, masterAt)));

                // Ctrl+A, Ctrl+D (Command on a Mac); a click on empty space clears.
                window.KeyPressQwerty(PhysicalKey.A, Cmd);
                Assert.That(view.Canvas.Selection.Count, Is.EqualTo(3));
                window.KeyPressQwerty(PhysicalKey.D, Cmd);
                Assert.That(view.Canvas.Selection, Is.Empty);
                // (Off the centre: a press where the last one was, this soon, would be a double click.)
                Click(window, view.PointOf(poly, window) + Aside);
                Click(window, view.PointOf(verb, window) + Aside, mods: Cmd);
                Assert.That(view.Canvas.Selection, Is.EquivalentTo(new[] { poly, verb }), "Ctrl-click (Command-click) adds");
                Click(window, corner);
                Assert.That(view.Canvas.Selection, Is.Empty);

                // Delete from the menu deletes the selection; one undo brings both back.
                window.KeyPressQwerty(PhysicalKey.A, Cmd);
                Menu(window, view, view.PointOf(poly, window), null, "Delete");
                Assert.That(Graph(), Is.EqualTo("Master"), "Master stays");
                Undo(window);
                Assert.That(Graph(), Is.EqualTo("Master; Poly; Verb; Poly→Master"));
            }
            finally
            {
                Close(window);
            }
        }
    }
}
