// Fork (docs/specs/studio-mvp.md S1): the Neo machines' editors in the studio, headless. Each
// opens as its machine's GUI and renders (a screenshot each, the size of its WPF editor); a knob
// drag changes its parameter and undo restores it; a preset loads from the bar; the live parts
// (meters, Neo Analyzer's spectrum, Neo EQ's response) draw what the audio does.

using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;

namespace ReBuzz.Studio.Tests
{
    // Inside the namespace: the engine's ReBuzz.Control namespace would hide Avalonia's Control.
    using Control = Avalonia.Controls.Control;

    public class NeoEditorTests
    {
        /// <summary>Each Neo machine, its editor, its screenshot, and its WPF editor's size (CI's neo-*-editor.png).</summary>
        static readonly (string Machine, string Editor, string Shot, int WpfWidth, int WpfHeight)[] Editors =
        {
            ("Neo Poly", "NeoPolyEditor", "neo-poly-editor", 950, 972),
            ("Neo Wave", "NeoWaveEditor", "neo-wave-editor", 994, 1006),
            ("Neo FM6", "NeoFm6Editor", "neo-fm6-editor", 1276, 946),
            ("Neo Comp", "NeoCompEditor", "neo-comp-editor", 880, 294),
            ("Neo Drive", "NeoDriveEditor", "neo-drive-editor", 746, 322),
            ("Neo EQ", "NeoEqEditor", "neo-eq-editor", 822, 508),
            ("Neo Limiter", "NeoLimiterEditor", "neo-limiter-editor", 718, 292),
            ("Neo Utility", "NeoUtilityEditor", "neo-utility-editor", 627, 296),
            ("Neo Verb", "NeoVerbEditor", "neo-verb-editor", 880, 332),
            ("Neo Analyzer", "NeoAnalyzerEditor", "neo-analyzer-editor", 1118, 398),
            ("Neo Ensemble", "NeoEnsembleEditor", "neo-ensemble-editor", 978, 338),
            ("Neo Delay", "NeoDelayEditor", "neo-delay-editor", 994, 343),
            ("Neo Multiband", "NeoMultibandEditor", "neo-multiband-editor", 1042, 578),
            ("Neo Sampler", "NeoSamplerEditor", "neo-sampler-editor", 1144, 778),
            ("Neo Scope", "NeoScopeEditor", "neo-scope-editor", 1098, 458),
            ("Neo Drums", "NeoDrumsEditor", "neo-drums-editor", 1232, 738),
            ("Neo Filter", "NeoFilterEditor", "neo-filter-editor", 802, 620),
            ("Dye Essence", "DyeEssenceEditor", "dye-essence-editor", 1274, 988),
        };

        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string machine, string name, float x = 0.3f, float y = 0.3f)
        {
            Core.SongCore.CreateMachine(machine, null, name, null, null, null, -1, x, y);
            var m = Core.SongCore.MachinesList.First(m => m.Name == name);
            return m;
        }

        static void Remove(params MachineCore[] machines)
        {
            foreach (var m in machines) Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(machines);
        }

        static IParameter Global(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name);

        /// <summary>Opens the machine's GUI as the engine does, laid out and drawn once.</summary>
        static Window OpenEditor(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var window = StudioMachineUiHost.GuiWindowOf(m);
            Assert.That(window, Is.Not.Null, $"{m.Name}: its GUI opened in a window");
            Dispatcher.UIThread.RunJobs();
            window!.CaptureRenderedFrame();
            return window;
        }

        /// <summary>Waits (up to 5 s) while the machine prepares in the background (Neo Wave's tables), then redraws.</summary>
        static void WaitReady(MachineCore m, Window window)
        {
            var target = m.ManagedMachine;
            var busy = target?.GetType().GetProperty("Busy");
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (busy?.GetValue(target) is true && clock.ElapsedMilliseconds < 5000) Thread.Sleep(20);
            foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
        }

        static IEnumerable<Control> Controls(Window window, string type) =>
            window.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == type);

        static Control Find(Window window, string type, string name) =>
            Controls(window, type).Single(c => AutomationProperties.GetName(c) == name);

        static Point Centre(Control c, Window window) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), window)!.Value;

        static Point At(Control c, Window window, double x, double y) => c.TranslatePoint(new Point(x, y), window)!.Value;

        static Rect BoundsIn(Control c, Window window) => new Rect(At(c, window, 0, 0), c.Bounds.Size);

        static object Get(object o, string property) =>
            o.GetType().GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o);

        static void Click(Window window, Point p, MouseButton button = MouseButton.Left)
        {
            window.MouseDown(p, button);
            window.MouseUp(p, button);
        }

        // ------------------------------------------------------------------ audio, offline

        static WorkManager work;

        /// <summary>Runs the engine for <paramref name="seconds"/> (no audio device here), then the UI's queue.</summary>
        static void Run(double seconds)
        {
            work ??= new WorkManager(Core, new WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            int blocks = (int)Math.Ceiling(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256.0);
            lock (ReBuzzCore.AudioLock)
            {
                for (int i = 0; i < blocks; i++) work.MainAudioFillBuffer(buffer, 0, 512);
            }
            Dispatcher.UIThread.RunJobs();
        }

        // ------------------------------------------------------------------ pixels

        /// <summary>The window drawn now (every control redrawn), as rows of 0xRRGGBB pixels.</summary>
        static (uint[] Pixels, int Width, int Height) Frame(Window window, string screenshot = null)
        {
            foreach (var v in window.GetVisualDescendants()) v.InvalidateVisual();
            Dispatcher.UIThread.RunJobs();
            if (screenshot != null) Studio.Screenshot(window, screenshot);
            var bitmap = window.CaptureRenderedFrame();
            Assert.That(bitmap, Is.Not.Null);
            using var fb = bitmap!.Lock();
            int w = fb.Size.Width, h = fb.Size.Height;
            bool rgba = fb.Format == Avalonia.Platform.PixelFormat.Rgba8888;
            var pixels = new uint[w * h];
            var row = new int[w];
            for (int y = 0; y < h; y++)
            {
                Marshal.Copy(fb.Address + y * fb.RowBytes, row, 0, w);
                for (int x = 0; x < w; x++)
                {
                    uint p = (uint)row[x];
                    pixels[y * w + x] = rgba ? ((p & 0xFF) << 16) | (p & 0xFF00) | ((p >> 16) & 0xFF) : p & 0xFFFFFF;
                }
            }
            return (pixels, w, h);
        }

        /// <summary>Pixels in <paramref name="area"/> within <paramref name="tolerance"/> of an RGB colour.</summary>
        static int Count((uint[] Pixels, int Width, int Height) f, Rect area, uint rgb, int tolerance = 40)
        {
            int n = 0;
            for (int y = (int)area.Top; y < (int)area.Bottom && y < f.Height; y++)
                for (int x = (int)area.Left; x < (int)area.Right && x < f.Width; x++)
                {
                    uint p = f.Pixels[y * f.Width + x];
                    int dr = (int)((p >> 16) & 0xFF) - (int)((rgb >> 16) & 0xFF);
                    int dg = (int)((p >> 8) & 0xFF) - (int)((rgb >> 8) & 0xFF);
                    int db = (int)(p & 0xFF) - (int)(rgb & 0xFF);
                    if (Math.Abs(dr) + Math.Abs(dg) + Math.Abs(db) <= tolerance) n++;
                }
            return n;
        }

        /// <summary>Pixels in <paramref name="area"/> that differ between two frames.</summary>
        static int Changed((uint[] Pixels, int Width, int Height) a, (uint[] Pixels, int Width, int Height) b, Rect area)
        {
            int n = 0;
            for (int y = (int)area.Top; y < (int)area.Bottom && y < a.Height; y++)
                for (int x = (int)area.Left; x < (int)area.Right && x < a.Width; x++)
                    if (a.Pixels[y * a.Width + x] != b.Pixels[y * b.Width + x]) n++;
            return n;
        }

        // ------------------------------------------------------------------ tests

        [AvaloniaTest]
        public void EveryNeoEditorOpensAndRendersWithItsWpfLayout()
        {
            Assert.Multiple(() =>
            {
                foreach (var e in Editors)
                {
                    var m = Add(e.Machine, "Editor " + e.Machine);
                    try
                    {
                        var window = OpenEditor(m);
                        Assert.That(window.Content?.GetType().Name, Is.EqualTo(e.Editor), $"{e.Machine}: its own editor, not the parameter window");
                        WaitReady(m, window);
                        Studio.Screenshot(window, e.Shot);
                        Assert.That(Controls(window, "TextBlock").Select(t => ((TextBlock)t).Text ?? ""), Has.None.StartsWith("Editor error"), $"{e.Machine}: the body built");
                        Assert.That(Controls(window, "ParamKnob").Count(), Is.GreaterThan(1), $"{e.Machine}: knobs");
                        Assert.That(Controls(window, "Section").Count(), Is.GreaterThan(2), $"{e.Machine}: sections");
                        Assert.That(Controls(window, "PresetBar").Count(), Is.EqualTo(1), $"{e.Machine}: the preset bar");
                        // The WPF editor's layout: the same size, give or take the fonts' metrics.
                        var size = window.ClientSize;
                        Assert.That(size.Width, Is.EqualTo(e.WpfWidth).Within(6).Percent, $"{e.Machine}: width (WPF {e.WpfWidth})");
                        Assert.That(size.Height, Is.EqualTo(e.WpfHeight).Within(6).Percent, $"{e.Machine}: height (WPF {e.WpfHeight})");
                        TestContext.Out.WriteLine($"{e.Machine}: {size.Width:0} x {size.Height:0} (WPF {e.WpfWidth} x {e.WpfHeight})");
                    }
                    finally
                    {
                        Remove(m);
                    }
                }
            });
        }

        [AvaloniaTest]
        public void AKnobDragChangesItsParameterAndUndoRestoresIt()
        {
            var m = Add("Neo Comp", "Drag Comp");
            try
            {
                var window = OpenEditor(m);
                var knob = Find(window, "ParamKnob", "Threshold");
                var threshold = Global(m, "Threshold");
                var stack = Core.SongCore.ActionStack;
                int before = threshold.GetValue(0), steps = stack.Actions.Count();

                // Drag up 60 pixels: the value rises, as one undo step.
                var c = Centre(knob, window);
                window.MouseDown(c, MouseButton.Left);
                for (int dy = 10; dy <= 60; dy += 10) window.MouseMove(c - new Point(0, dy));
                window.MouseUp(c - new Point(0, 60), MouseButton.Left);
                int after = threshold.GetValue(0);
                Assert.That(after, Is.GreaterThan(before), "dragging up raises the threshold");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "the drag is one undo step");
                Run(0.05);
                double shown = (double)Get(knob, "Value");
                Studio.Screenshot(window, "neo-comp-editor-dragged");

                Core.SongCore.Undo();
                Assert.That(threshold.GetValue(0), Is.EqualTo(before), "undo restores the parameter");
                Run(0.05);
                Assert.That((double)Get(knob, "Value"), Is.LessThan(shown), "and the knob follows");

                Core.SongCore.Redo();
                Assert.That(threshold.GetValue(0), Is.EqualTo(after), "redo sets it again");
                Run(0.05);   // the engine tells the editor (on its next tick)
                Assert.That((double)Get(knob, "Value"), Is.EqualTo(shown));

                // A double-click puts it back to its default, also undoable.
                int clickCount = 0;
                knob.AddHandler(InputElement.PointerPressedEvent, (s, a) => clickCount = a.ClickCount, RoutingStrategies.Tunnel);
                MachineViewTests.DoubleClick(window, c);
                Assert.That(clickCount, Is.EqualTo(2), "the knob had the double click");
                Assert.That(threshold.GetValue(0), Is.EqualTo(700), "a double-click resets the knob to its default");
                Core.SongCore.Undo();
                Assert.That(threshold.GetValue(0), Is.EqualTo(after));

                // A choice button: one click, one step.
                var mode = Find(window, "ParamChoice", "Mode");
                var modeParam = Global(m, "Mode");
                int oldMode = modeParam.GetValue(0);
                var cell = (Rect)mode.GetType().GetMethod("CellBounds")!.Invoke(mode, new object[] { 2 })!;
                Click(window, At(mode, window, cell.Center.X, cell.Center.Y));
                Assert.That(modeParam.GetValue(0), Is.EqualTo(2), "a click picks the choice");
                Core.SongCore.Undo();
                Assert.That(modeParam.GetValue(0), Is.EqualTo(oldMode), "undo puts the choice back");
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void APresetLoadsFromTheBar()
        {
            var m = Add("Neo Comp", "Preset Comp");
            try
            {
                var window = OpenEditor(m);
                var bar = Find(window, "PresetBar", "Presets");
                var names = m.GetPresetNames().Where(n => n != "<default>").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
                Assert.That(names, Has.Count.GreaterThan(3), "Neo Comp ships presets");
                var globals = m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters;
                int[] Values() => globals.Select(p => p.GetValue(0)).ToArray();
                int[] Expected(string preset) => globals.Select(p => m.GetPreset(preset).GetValueOrDefault(p, 0)).ToArray();
                var defaults = Values();

                // The next arrow: the first preset.
                Click(window, At(bar, window, 203, 12));
                Assert.That(Get(bar, "Current"), Is.EqualTo(names[0]));
                Assert.That(Values(), Is.EqualTo(Expected(names[0])), "its values are set");

                // The list: grouped by the name's prefix; pick one.
                Click(window, At(bar, window, 100, 12));
                var menu = (ContextMenu)Get(bar, "Menu");
                Assert.That(menu, Is.Not.Null, "the name opens the list");
                Assert.That(menu.IsOpen, Is.True);
                string pick = names.First(n => n.StartsWith("Glue"));
                var group = menu.Items.OfType<MenuItem>().Single(i => (string)i.Header! == "Glue");
                var item = group.Items.OfType<MenuItem>().Single(i => (string)i.Header! == pick);
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
                Assert.That(Get(bar, "Current"), Is.EqualTo(pick));
                Assert.That(Values(), Is.EqualTo(Expected(pick)));
                Run(0.05);
                Studio.Screenshot(window, "neo-comp-editor-preset");

                // Each load is one undo step.
                Core.SongCore.Undo();
                Assert.That(Values(), Is.EqualTo(Expected(names[0])), "undo goes back to the previous preset");
                Core.SongCore.Undo();
                Assert.That(Values(), Is.EqualTo(defaults), "and to the sound before it");
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void TheLivePartsDrawWhatTheAudioDoes()
        {
            // Neo Poly → Neo EQ → Neo Analyzer → Master, a chord held.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Live Poly", -0.6f, 0);
            var eq = Add("Neo EQ", "Live EQ", -0.2f, 0);
            var analyzer = Add("Neo Analyzer", "Live Analyzer", 0.2f, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, eq, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(eq, analyzer, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(analyzer, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var polyWindow = OpenEditor(poly);
                var eqWindow = OpenEditor(eq);
                var analyzerWindow = OpenEditor(analyzer);
                Run(0.2);
                var polyQuiet = Frame(polyWindow);
                var eqQuiet = Frame(eqWindow);
                var analyzerQuiet = Frame(analyzerWindow);

                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };   // C-3, E-3, G-3
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                Run(1.0);
                var polyLoud = Frame(polyWindow, "neo-poly-editor-live");
                var eqLoud = Frame(eqWindow, "neo-eq-editor-live");
                var analyzerLoud = Frame(analyzerWindow, "neo-analyzer-editor-live");

                Assert.Multiple(() =>
                {
                    // Neo Poly's output meter: its bar, in the accent (or red above 0 dB).
                    var meter = BoundsIn(Find(polyWindow, "Meter", "Output"), polyWindow);
                    Assert.That(Count(polyQuiet, meter, 0xFF5C8A), Is.EqualTo(0), "silent: no bar");
                    Assert.That(Count(polyLoud, meter, 0xFF5C8A) + Count(polyLoud, meter, 0xFF4D4D), Is.GreaterThan(40), "Neo Poly's meter shows the chord");

                    // Neo EQ: the response curve always; the output spectrum under it once audio runs.
                    var graph = BoundsIn(Controls(eqWindow, "InteractiveGraph").Single(), eqWindow);
                    Assert.That(Count(eqQuiet, graph, 0x4FB3FF), Is.GreaterThan(300), "Neo EQ's response curve");
                    Assert.That(Changed(eqQuiet, eqLoud, graph), Is.GreaterThan(1500), "Neo EQ's spectrum shows the chord");
                    var eqOut = BoundsIn(Find(eqWindow, "Meter", "Output"), eqWindow);
                    Assert.That(Count(eqLoud, eqOut, 0x4FB3FF) + Count(eqLoud, eqOut, 0xFF4D4D), Is.GreaterThan(40), "Neo EQ's output meter");

                    // Neo Analyzer: the spectrum (and loudness, stereo) from the engine's snapshots.
                    var spectrum = BoundsIn(Controls(analyzerWindow, "GraphView").First(g => g.Bounds.Width >= 590), analyzerWindow);
                    Assert.That(Changed(analyzerQuiet, analyzerLoud, spectrum), Is.GreaterThan(3000), "Neo Analyzer's spectrum shows the chord");
                    var loudness = BoundsIn(Controls(analyzerWindow, "GraphView").First(g => Math.Abs(g.Bounds.Width - 250) < 1), analyzerWindow);
                    Assert.That(Changed(analyzerQuiet, analyzerLoud, loudness), Is.GreaterThan(300), "its loudness meters move");

                    // They redraw on their own while shown.
                    Assert.That(Controls(analyzerWindow, "GraphView").Count(g => (bool)Get(g, "Animating")), Is.GreaterThan(3), "the analyzer's graphs animate");
                });

                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(analyzer, eq, poly);
            }
        }

        /// <summary>The sequence number of the newest snapshot a Neo Analyzer's or Neo Scope's engine has published (through reflection).</summary>
        static long NewestSnapshot(MachineCore m)
        {
            var engine = Get(m.ManagedMachine, "Engine");
            var read = engine.GetType().GetMethod("Read")!;
            var snapshot = Activator.CreateInstance(read.GetParameters()[0].ParameterType)!;
            read.Invoke(engine, new[] { snapshot });
            return (long)snapshot.GetType().GetField("Sequence")!.GetValue(snapshot)!;
        }

        [AvaloniaTest]
        public void TheAnalysisEditorsDrawTheNewestSnapshotAfterEveryRender()
        {
            // Neo Poly (a chord held) → Neo Analyzer → Neo Scope → Master, both editors open. A frame of one of an editor's
            // graphs, a short offline render (a few blocks, a few ms of wall clock here), another frame: the second draws the
            // engine's newest snapshot, not the first frame's. The editors copied the snapshot at most every 10 ms of wall
            // clock, so a render that outran that left the next frame drawing the one before (Fork Mac 41966698814: Neo
            // Scope's; Neo Analyzer's had the same throttle). The frames are one graph each, the smallest, drawn into a bitmap:
            // a whole window's frame can take the 10 ms by itself.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Fresh Poly", -0.6f, 0);
            var analyzer = Add("Neo Analyzer", "Fresh Analyzer", -0.2f, 0);
            var scope = Add("Neo Scope", "Fresh Scope", 0.2f, 0);
            var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
            int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };   // C-3, E-3, G-3
            try
            {
                Core.SongCore.ConnectMachines(poly, analyzer, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(analyzer, scope, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(scope, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                var editors = new[] { (Machine: analyzer, Window: OpenEditor(analyzer)), (Machine: scope, Window: OpenEditor(scope)) };
                Run(0.2);
                Assert.Multiple(() =>
                {
                    foreach (var (machine, window) in editors)
                    {
                        var live = Get(window.Content!, "Data");
                        var snapshot = Field(live, "Snapshot");
                        var graph = Controls(window, "GraphView").OrderBy(g => g.Bounds.Width * g.Bounds.Height).First();
                        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(graph.Bounds.Width), (int)Math.Ceiling(graph.Bounds.Height)));
                        long Drawn()
                        {
                            bitmap.Render(graph);
                            return (long)Field(snapshot, "Sequence");
                        }
                        long drawn = Drawn();
                        Assert.That(drawn, Is.EqualTo(NewestSnapshot(machine)), $"{machine.Name}: the first frame draws the newest snapshot");
                        for (int k = 0; k < 8; k++)
                        {
                            var clock = System.Diagnostics.Stopwatch.StartNew();
                            Run(0.05);
                            long next = Drawn();
                            double ms = clock.Elapsed.TotalMilliseconds;
                            long newest = NewestSnapshot(machine);
                            Assert.That(next, Is.Not.EqualTo(drawn), $"{machine.Name}: the frame after a render {ms:F1} ms long draws another snapshot than the one before");
                            Assert.That(next, Is.EqualTo(newest), $"{machine.Name}: the engine's newest");
                            drawn = next;
                        }
                    }
                });
            }
            finally
            {
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
                Remove(scope, analyzer, poly);
            }
        }

        [AvaloniaTest]
        public void NeoEnsemblesSweepFollowsItsModeAndItsLfo()
        {
            // Neo Poly → Neo Ensemble → Master, a chord held. In each mode the sweep's picture moves with the
            // LFO (the chorus voices' delays, the flanger's comb, the phaser's notches), and the output meter
            // shows the chord.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Sweep Poly", -0.6f, 0);
            var ensemble = Add("Neo Ensemble", "Sweep Ensemble", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, ensemble, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(ensemble, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(ensemble);
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };   // C-3, E-3, G-3
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                Global(ensemble, "Rate").SetValue(0, 700);       // 2 Hz: the picture moves between frames
                var sweepView = Controls(window, "GraphView").First(g => Math.Abs(g.Bounds.Width - 300) < 1);
                var sweep = BoundsIn(sweepView, window);
                var meter = BoundsIn(Find(window, "Meter", "Output"), window);
                Assert.Multiple(() =>
                {
                    foreach (var (mode, shot) in new[] { (0, "neo-ensemble-editor-chorus"), (1, "neo-ensemble-editor-flanger"), (2, "neo-ensemble-editor-phaser") })
                    {
                        Global(ensemble, "Mode").SetValue(0, mode);
                        if (mode == 1) Global(ensemble, "Feedback").SetValue(0, 1700);
                        Run(0.3);
                        var a = Frame(window);
                        Run(0.12);
                        var b = Frame(window, shot);
                        Assert.That(Changed(a, b, sweep), Is.GreaterThan(400), $"mode {mode}: the sweep's picture moves with the LFO");
                        Assert.That(Count(b, meter, 0xFF7EDB) + Count(b, meter, 0xFF4D4D), Is.GreaterThan(40), $"mode {mode}: the output meter shows the chord");
                    }
                    Assert.That((bool)Get(sweepView, "Animating"), Is.True, "the sweep redraws on its own while shown");
                });
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(ensemble, poly);
            }
        }

        [AvaloniaTest]
        public void NeoDelaysEchoesFollowItsModeAndTimeAndItsMetersTheSound()
        {
            // Neo Poly → Neo Delay → Master, a chord held. The echoes' picture changes with the mode and the time (Stereo, then
            // Ping-pong, then Cross a dotted quarter with the right line longer), the output meter shows the chord, and with
            // Duck on the ducking meter shows the echoes turned down while the chord plays.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Echo Poly", -0.6f, 0);
            var delay = Add("Neo Delay", "Echo Delay", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, delay, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(delay, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(delay);
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };   // C-3, E-3, G-3
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                var echoView = Controls(window, "GraphView").First(g => Math.Abs(g.Bounds.Width - 330) < 1);
                var echoes = BoundsIn(echoView, window);
                var meter = BoundsIn(Find(window, "Meter", "Output"), window);
                var ducking = BoundsIn(Find(window, "Meter", "Ducking"), window);
                Assert.Multiple(() =>
                {
                    var before = Frame(window);
                    foreach (var (mode, sync, offset, shot) in new[] { (0, 4, 500, "neo-delay-editor-stereo"), (1, 4, 500, "neo-delay-editor-pingpong"), (2, 5, 800, "neo-delay-editor-cross") })
                    {
                        Global(delay, "Mode").SetValue(0, mode);
                        Global(delay, "Sync").SetValue(0, sync);
                        Global(delay, "Offset").SetValue(0, offset);
                        Global(delay, "Feedback").SetValue(0, 1600);
                        Run(0.3);
                        var after = Frame(window, shot);
                        Assert.That(Changed(before, after, echoes), Is.GreaterThan(200), $"mode {mode}: the echoes' picture follows the mode and the time");
                        Assert.That(Count(after, meter, 0xFF6F52) + Count(after, meter, 0xFF4D4D), Is.GreaterThan(40), $"mode {mode}: the output meter shows the chord");
                        before = after;
                    }
                    Global(delay, "Duck").SetValue(0, 240);
                    Global(delay, "Duck threshold").SetValue(0, 300);
                    Run(0.3);
                    var ducked = Frame(window, "neo-delay-editor-ducking");
                    Assert.That(Count(ducked, ducking, 0xFF6F52), Is.GreaterThan(30), "the ducking meter shows the echoes turned down");
                });
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(delay, poly);
            }
        }

        [AvaloniaTest]
        public void NeoMultibandsCrossoversDragAndItsMetersShowEachBandsGainChange()
        {
            // Neo Poly → Neo Multiband → Master, a chord held, every band compressing hard. Dragging crossover 2's grip moves its
            // frequency as one undo step; each band's meter shows its cut in the band's colour; a click on band 3 in the graph
            // shows its controls; upward expansion on band 1 grows its meter up instead.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Bands Poly", -0.6f, 0);
            var m = Add("Neo Multiband", "Bands", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, m, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                for (int b = 1; b <= 4; b++)
                {
                    Global(m, $"B{b} threshold").SetValue(0, 0);    // −60 dB
                    Global(m, $"B{b} ratio").SetValue(0, 800);      // 8:1
                    Global(m, $"B{b} attack").SetValue(0, 300);     // fast
                }
                Global(m, "Phase").SetValue(0, 1);                  // minimum phase: the meters move at once
                var window = OpenEditor(m);
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (2 << 4) + 1, (4 << 4) + 5, (5 << 4) + 8 };   // C-2, E-4, G-5: something in every band
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                Run(0.6);
                var graph = Controls(window, "InteractiveGraph").Single();
                var area = BoundsIn(graph, window);
                var meters = Controls(window, "GraphView").Where(g => Math.Abs(g.Bounds.Width - 30) < 1).OrderBy(g => BoundsIn(g, window).X).ToList();
                Assert.That(meters, Has.Count.EqualTo(4), "a meter per band");
                uint[] colours = { 0xFF6B6B, 0x8FD14F, 0x4FB3FF, 0xC38CFF };
                var f = Frame(window, "neo-multiband-editor-compressing");
                Assert.Multiple(() =>
                {
                    for (int b = 0; b < 4; b++)
                        Assert.That(Count(f, BoundsIn(meters[b], window), colours[b], 30), Is.GreaterThan(30), $"band {b + 1}'s meter shows its cut");
                });

                // Crossover 2's grip: at its frequency (1 kHz), the top of the plot (ResponseGraph's layout: 30 px left, 8 px top).
                var xo2 = Global(m, "Crossover 2");
                double hz0 = 20 * Math.Pow(1000, xo2.GetValue(0) / 10000.0);
                double plotW = area.Width - 38;
                double gx = area.X + 30 + Math.Log(hz0 / 20) / Math.Log(1000) * plotW, gy = area.Y + 8 + 6.5;
                int raw0 = xo2.GetValue(0), steps = Core.SongCore.ActionStack.Actions.Count();
                var grip = new Point(gx, gy + 40);
                MachineViewTests.Drag(window, grip, grip + new Point(60, 0));
                Assert.That(xo2.GetValue(0), Is.GreaterThan(raw0), "dragging right raises crossover 2");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "the drag is one undo step");
                Run(0.2);
                Frame(window, "neo-multiband-editor-dragged");
                Core.SongCore.Undo();
                Assert.That(xo2.GetValue(0), Is.EqualTo(raw0), "undo puts it back");

                // A click in band 3 (between 1 and 6 kHz) shows its controls.
                double x3 = area.X + 30 + Math.Log(2500 / 20.0) / Math.Log(1000) * plotW;
                MachineViewTests.Click(window, new Point(x3, area.Y + area.Height / 2));
                Run(0.05);
                Frame(window, "neo-multiband-editor-band3");
                var threshold = Controls(window, "ParamKnob").Where(k => k.IsEffectivelyVisible && AutomationProperties.GetName(k) == "Threshold").ToList();
                Assert.That(threshold, Has.Count.EqualTo(1), "one band's controls are shown");
                // It is band 3's: dragging it up raises B3 threshold.
                var b3 = Global(m, "B3 threshold");
                int t0 = b3.GetValue(0);
                var c = Centre(threshold[0], window);
                MachineViewTests.Drag(window, c, c - new Point(0, 40));
                Assert.That(b3.GetValue(0), Is.GreaterThan(t0), "the knob shown is band 3's threshold");

                // Upward expansion on band 1: its meter grows up from 0 dB.
                Global(m, "B1 ratio").SetValue(0, 0);                    // 0.5:1
                Global(m, "B1 threshold").SetValue(0, 300);             // −30 dB
                Global(m, "B1 range").SetValue(0, 120);                 // 12 dB
                Run(0.6);
                var up = Frame(window, "neo-multiband-editor-expanding");
                var box = BoundsIn(meters[0], window);
                var upper = new Rect(box.X, box.Y, box.Width, box.Height * 0.3);
                Assert.That(Count(up, upper, 0xFF6B6B, 120), Is.GreaterThan(20), "band 1's meter grows up while it expands");
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(m, poly);
            }
        }

        [AvaloniaTest]
        public void DraggingABandInNeoEqsGraphIsOneUndoStep()
        {
            var m = Add("Neo EQ", "Drag EQ");
            try
            {
                var window = OpenEditor(m);
                var graph = Controls(window, "InteractiveGraph").Single();
                var area = BoundsIn(graph, window);
                // Band 3's handle (selected, 250 Hz, 0 dB): its colour's pixels, below the graph's caption.
                var f = Frame(window);
                double sx = 0, sy = 0;
                int n = 0;
                for (int y = (int)area.Top + 24; y < (int)area.Bottom - 16; y++)
                    for (int x = (int)area.Left + 30; x < (int)area.Right; x++)
                        if (Count(f, new Rect(x, y, 1, 1), 0xF5D547, 24) == 1) { sx += x; sy += y; n++; }
                Assert.That(n, Is.GreaterThan(30), "band 3's handle is drawn");
                var handle = new Point(sx / n, sy / n);
                var freq = Global(m, "B3 freq");
                var gain = Global(m, "B3 gain");
                int freq0 = freq.GetValue(0), gain0 = gain.GetValue(0), steps = Core.SongCore.ActionStack.Actions.Count();

                window.MouseDown(handle, MouseButton.Left);
                for (int d = 8; d <= 40; d += 8) window.MouseMove(handle + new Point(d, -d));
                window.MouseUp(handle + new Point(40, -40), MouseButton.Left);
                Assert.That(gain.GetValue(0), Is.GreaterThan(gain0), "dragging up raises the band's gain");
                Assert.That(freq.GetValue(0), Is.GreaterThan(freq0), "and right its frequency");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "the drag is one undo step");
                Run(0.05);
                Studio.Screenshot(window, "neo-eq-editor-dragged");

                Core.SongCore.Undo();
                Assert.That((freq.GetValue(0), gain.GetValue(0)), Is.EqualTo((freq0, gain0)), "undo puts the band back");
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void NeoEqsGraphicModeShowsItsSlidersAndTheirCurve()
        {
            // machine-safety.md §4 (H2): Mode Graphic shows the 31 sliders on the graph with the response through them,
            // and the selected slider's knob below; the editor keeps its size. Drawing across the sliders sets them, as
            // one undo step; with a chord playing, the output spectrum shows under the curve.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Graphic Poly", -0.6f, 0);
            var m = Add("Neo EQ", "Graphic EQ", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, m, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(m);
                var size = window.ClientSize;
                var graph = Controls(window, "InteractiveGraph").Single();
                var area = BoundsIn(graph, window);
                var parametric = Frame(window);
                var stack = Core.SongCore.ActionStack;
                int steps = stack.Actions.Count();

                // The mode: a click on "Graphic".
                var mode = Find(window, "ParamChoice", "Mode");
                var cell = (Rect)mode.GetType().GetMethod("CellBounds")!.Invoke(mode, new object[] { 1 })!;
                Click(window, At(mode, window, cell.Center.X, cell.Center.Y));
                Assert.That(Global(m, "Mode").GetValue(0), Is.EqualTo(1), "a click picks Graphic");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                Run(0.05);
                var graphic = Frame(window, "neo-eq-editor-graphic");
                Assert.That(Changed(parametric, graphic, area), Is.GreaterThan(2000), "the graph shows the sliders");
                Assert.That(window.ClientSize.Width, Is.EqualTo(size.Width).Within(0.5), "the editor keeps its width");
                Assert.That(window.ClientSize.Height, Is.EqualTo(size.Height).Within(0.5), "and its height");
                var knob = Controls(window, "ParamKnob").Single(k => AutomationProperties.GetName(k) == "1 kHz");
                Assert.That(knob.IsEffectivelyVisible && knob.Opacity > 0 && knob.IsHitTestVisible, Is.True, "the selected slider's knob shows");

                // Draw: press beside the 1 kHz cap at +12 dB and drag right to the 2 kHz track at the same height.
                // The plot inside the graph: 30 px in, 8 down, 642 × 272 for 20 Hz – 20 kHz and ±24 dB.
                double X(double hz) => 30 + Math.Log(hz / 20) / Math.Log(1000) * (graph.Bounds.Width - 38);
                double Y(double db) => 8 + (graph.Bounds.Height - 26) * (24 - db) / 48;
                double y12 = Y(12);
                window.MouseDown(At(graph, window, X(1000), y12), MouseButton.Left);
                for (double hz = 1000; hz <= 2000; hz *= Math.Pow(2, 1 / 12.0)) window.MouseMove(At(graph, window, X(hz), y12));
                window.MouseMove(At(graph, window, X(1995.3), y12));
                window.MouseUp(At(graph, window, X(1995.3), y12), MouseButton.Left);
                foreach (var name in new[] { "G 1 kHz", "G 1.25 kHz", "G 1.6 kHz", "G 2 kHz" })
                    Assert.That(Global(m, name).GetValue(0), Is.EqualTo(300).Within(3), $"{name}: drawn at +12 dB");
                foreach (var name in new[] { "G 800 Hz", "G 2.5 kHz" })
                    Assert.That(Global(m, name).GetValue(0), Is.EqualTo(180), $"{name}: untouched");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 2), "the drawing is one undo step");
                Run(0.05);
                var drawn = Frame(window, "neo-eq-editor-graphic-drawn");
                Assert.That(Changed(graphic, drawn, area), Is.GreaterThan(300), "the curve and the caps moved");

                // A chord: the output spectrum shows under the curve.
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                Run(1.0);
                var loud = Frame(window, "neo-eq-editor-graphic-live");
                Assert.That(Changed(drawn, loud, area), Is.GreaterThan(1500), "the spectrum shows the chord");
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);

                Core.SongCore.Undo();
                Assert.That(Global(m, "G 1.25 kHz").GetValue(0), Is.EqualTo(180), "undo takes the drawing back");
                Core.SongCore.Undo();
                Assert.That(Global(m, "Mode").GetValue(0), Is.EqualTo(0), "and the mode");
                Run(0.05);
                Assert.That(window.ClientSize.Height, Is.EqualTo(size.Height).Within(0.5));
            }
            finally
            {
                Remove(m, poly);
            }
        }

        [AvaloniaTest]
        public void AnEditorsOwnInputTakesThePointerAsOnWpf()
        {
            // Neo FM6's algorithm diagram overrides the WPF-shaped mouse callbacks: click for the next
            // algorithm, right click for the previous one.
            var m = Add("Neo FM6", "Input FM6");
            try
            {
                var window = OpenEditor(m);
                var view = Controls(window, "AlgorithmView").Single();
                var algorithm = Global(m, "Algorithm");
                int before = algorithm.GetValue(0);
                var c = Centre(view, window);
                Click(window, c);
                Assert.That(algorithm.GetValue(0), Is.EqualTo((before + 1) % 32), "a click: the next algorithm");
                Click(window, c, MouseButton.Right);
                Assert.That(algorithm.GetValue(0), Is.EqualTo(before), "a right click: back");
                Core.SongCore.Undo();
                Assert.That(algorithm.GetValue(0), Is.EqualTo((before + 1) % 32), "each is an undo step");
                Core.SongCore.Undo();
                Assert.That(algorithm.GetValue(0), Is.EqualTo(before));

                // Closed and opened again, the editor is built anew.
                window.Close();
                Dispatcher.UIThread.RunJobs();
                Assert.That(StudioMachineUiHost.GuiWindowOf(m), Is.Null);
                var again = OpenEditor(m);
                Assert.That(again, Is.Not.SameAs(window));
                Assert.That(Controls(again, "AlgorithmView").Count(), Is.EqualTo(1));
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void NeoSamplersWaveAndZonesShowTheSongsWaveAndItsMarkersAndTabsWork()
        {
            // A sine in the song's wavetable (slot 1, ReBuzz's default root C-4), played by the default zone on C-4. The wave
            // view draws it in zone 1's colour with its voice; dragging S moves Z1 start as one undo step; zone 2's tab shows
            // its controls, and its wave picker steps from Off to the song's wave.
            var path = Path.Combine(Path.GetTempPath(), "neo-sampler-editor-" + Guid.NewGuid().ToString("N") + ".wav");
            using (var w = new BinaryWriter(File.Create(path)))
            {
                int n = 48000;
                w.Write("RIFF"u8.ToArray()); w.Write(36 + n * 4); w.Write("WAVE"u8.ToArray());
                w.Write("fmt "u8.ToArray()); w.Write(16); w.Write((short)3); w.Write((short)1); w.Write(48000); w.Write(48000 * 4); w.Write((short)4); w.Write((short)32);
                w.Write("data"u8.ToArray()); w.Write(n * 4);
                for (int i = 0; i < n; i++) w.Write((float)(0.5 * Math.Sin(2 * Math.PI * 441 * i / 48000) * (1 - i / (double)n)));
            }
            Core.SongCore.Wavetable.LoadWave(0, path, "Editor sine", false);
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var m = Add("Neo Sampler", "Editor Sampler", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(m);
                WaitReady(m, window);
                if (m.TrackCount < 1) m.TrackCount = 1;
                var note = m.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                note.SetValue(0, (4 << 4) + 1);
                Run(0.3);
                var waveView = Controls(window, "InteractiveGraph").Single(g => Math.Abs(g.Bounds.Width - 514) < 1);
                var area = BoundsIn(waveView, window);
                var f = Frame(window, "neo-sampler-editor-playing");
                Assert.That(Count(f, area, 0xFF6B6B, 60), Is.GreaterThan(300), "the wave drawn in zone 1's colour");

                // S (start) sits at the left of the wave; drag it right.
                var start = Global(m, "Z1 start");
                int s0 = start.GetValue(0), steps = Core.SongCore.ActionStack.Actions.Count();
                var from = new Point(area.X + 8, area.Y + 16 + (area.Height - 30) / 2);
                MachineViewTests.Drag(window, from, from + new Point(100, 0));
                Assert.That(start.GetValue(0), Is.GreaterThan(s0), "dragging S right moves the zone's start");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "as one undo step");
                Run(0.1);
                Frame(window, "neo-sampler-editor-start");
                Core.SongCore.Undo();
                Assert.That(start.GetValue(0), Is.EqualTo(s0), "undo puts it back");

                // Zone 2's tab, then its picker's right arrow: Off → the song's wave (slot 1).
                var tabs = Controls(window, "ZoneTabs").Single();
                MachineViewTests.Click(window, At(tabs, window, 30 * 1 + 15, 18 + 11));
                Run(0.05);
                var picker = Controls(window, "WavePicker").Single(c => c.IsEffectivelyVisible);
                var z2 = Global(m, "Z2 wave");
                Assert.That(z2.GetValue(0), Is.EqualTo(0));
                MachineViewTests.Click(window, At(picker, window, picker.Bounds.Width - 8, 29));
                Assert.That(z2.GetValue(0), Is.EqualTo(1), "zone 2's picker stepped to the song's wave");
                Run(0.05);
                Frame(window, "neo-sampler-editor-zone2");
                note.SetValue(0, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(m);
                Core.SongCore.Wavetable.LoadWave(0, null, null, false);
            }
        }
    

        // ------------------------------------------------------------------ Neo Drums

        [AvaloniaTest]
        public void NeoDrumsPadsPlayTheirVoicesAndTheHitPictureFollowsTheKnobs()
        {
            // Neo Drums → Master. A click on a pad plays its voice, harder towards the pad's top, lights the pad and shows the
            // voice's hit; a voice's knob redraws the hit (one undo step), and a double click puts it back to its default.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var m = Add("Neo Drums", "Editor Drums", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(m, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(m);
                var pads = Controls(window, "InteractiveGraph").Single(g => Math.Abs(g.Bounds.Width - 480) < 1);
                var hit = Controls(window, "GraphView").Single(g => Math.Abs(g.Bounds.Width - 400) < 1);
                Rect padArea = BoundsIn(pads, window), hitArea = BoundsIn(hit, window);
                var machine = m.ManagedMachine;
                var engine = machine.GetType().GetProperty("Engine", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(machine)!;
                var peaks = (float[])engine.GetType().GetField("VoicePeak")!.GetValue(engine)!;

                // The pads: six columns, two rows, 6 px apart and from the edge (DrumsViews.cs); the snare is column 1, row 0.
                double w = (480 - 12 - 5 * 6) / 6.0, h = (200 - 12 - 6) / 2.0;
                Point Pad(int voice, double fromTop) => At(pads, window, 6 + voice % 6 * (w + 6) + w / 2, 6 + voice / 6 * (h + 6) + fromTop);
                var idle = Frame(window);
                Click(window, Pad(1, 3));
                Run(0.05);
                float hard = peaks[1];
                Assert.That(hard, Is.GreaterThan(0.05f), "a click on the snare's pad plays the snare");
                Assert.That(peaks.Where((p, v) => v != 1), Is.All.EqualTo(0f), "and nothing else");
                var playing = Frame(window, "neo-drums-editor-snare");
                Assert.That(Changed(idle, playing, padArea), Is.GreaterThan(400), "its pad lights");
                Assert.That(Changed(idle, playing, hitArea), Is.GreaterThan(400), "the hit picture shows the snare's hit");

                Run(1.5);
                Click(window, Pad(1, h - 3));
                Run(0.05);
                Assert.That(peaks[1], Is.LessThan(hard * 0.4f), "lower on the pad plays softer (velocity 40 against 127)");
                Run(1.5);

                // The snare's Decay knob: dragged up, the decay lengthens as one undo step and the hit picture redraws.
                var section = Controls(window, "Section").Single(c => AutomationProperties.GetName(c) == "Snare");
                var knob = section.GetVisualDescendants().OfType<Control>().Single(c => c.GetType().Name == "ParamKnob" && AutomationProperties.GetName(c) == "Decay");
                var decay = Global(m, "Snare decay");
                int before = decay.GetValue(0), steps = Core.SongCore.ActionStack.Actions.Count();
                var shown = Frame(window);
                var c = Centre(knob, window);
                MachineViewTests.Drag(window, c, c - new Point(0, 80));
                Assert.That(decay.GetValue(0), Is.GreaterThan(before), "dragging up lengthens the snare's decay");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "as one undo step");
                Run(0.05);
                var longer = Frame(window, "neo-drums-editor-decay");
                Assert.That(Changed(shown, longer, hitArea), Is.GreaterThan(400), "the hit picture follows the knob");

                MachineViewTests.DoubleClick(window, c);
                Assert.That(decay.GetValue(0), Is.EqualTo(decay.DefValue), "a double click puts the decay back to its default");
                Core.SongCore.Undo();
                Assert.That(decay.GetValue(0), Is.GreaterThan(before), "undo: the dragged decay again");
                Core.SongCore.Undo();
                Assert.That(decay.GetValue(0), Is.EqualTo(before), "and the decay before");
            }
            finally
            {
                Remove(m);
            }
        }

        // ------------------------------------------------------------------ Neo Filter

        [AvaloniaTest]
        public void NeoFiltersResponseFollowsItsLfoAndEnvelopeAndItsHandleDrags()
        {
            // Neo Poly → Neo Filter → Master, a chord held. With the LFO sweeping the cutoff the response's live curve moves from
            // frame to frame; with the envelope on, its meter shows the chord. The handle on the cutoff drags the cutoff (across)
            // and the resonance (up) as one undo step, and the response redraws there.
            var master = Core.SongCore.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var poly = Add("Neo Poly", "Filter Poly", -0.6f, 0);
            var filter = Add("Neo Filter", "Sweep Filter", 0, 0);
            try
            {
                Core.SongCore.ConnectMachines(poly, filter, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Core.SongCore.ConnectMachines(filter, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                var window = OpenEditor(filter);
                poly.TrackCount = Math.Max(poly.TrackCount, 3);
                var note = poly.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                int[] chord = { (3 << 4) + 1, (3 << 4) + 5, (3 << 4) + 8 };   // C-3, E-3, G-3
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, chord[t]);
                var graph = Controls(window, "InteractiveGraph").Single(g => Math.Abs(g.Bounds.Width - 586) < 1);
                var area = BoundsIn(graph, window);
                var envelope = BoundsIn(Find(window, "Meter", "Envelope"), window);
                Assert.Multiple(() =>
                {
                    Global(filter, "LFO retrig").SetValue(0, 0);
                    Global(filter, "LFO rate").SetValue(0, Params(filter, "LFO rate", "4 Hz"));
                    Global(filter, "LFO depth").SetValue(0, 200);
                    Run(0.1);
                    var a = Frame(window);
                    Run(0.07);
                    var b = Frame(window, "neo-filter-editor-sweeping");
                    Assert.That(Changed(a, b, area), Is.GreaterThan(300), "the live curve follows the LFO");

                    Global(filter, "LFO depth").SetValue(0, 0);
                    Global(filter, "Env amount").SetValue(0, 900);
                    Global(filter, "Env sensitivity").SetValue(0, 360);
                    Run(0.3);
                    var c = Frame(window, "neo-filter-editor-envelope");
                    Assert.That(Count(c, envelope, 0xD4E157), Is.GreaterThan(30), "the envelope meter shows the chord");

                    // The handle: at the cutoff (2 kHz) and 20·log10 Q (−3 dB at Q 0.71) on the graph (ResponseGraph's plot: 30 px in,
                    // 8 down, −42 … +24 dB).
                    Global(filter, "Env amount").SetValue(0, 600);
                    Run(0.1);
                    var cutoff = Global(filter, "Cutoff");
                    var resonance = Global(filter, "Resonance");
                    int cutoff0 = cutoff.GetValue(0), resonance0 = resonance.GetValue(0), steps = Core.SongCore.ActionStack.Actions.Count();
                    double plotW = area.Width - 38, plotH = area.Height - 26;
                    var handle = new Point(area.Left + 30 + Math.Log(2000.9 / 20) / Math.Log(1000) * plotW, area.Top + 8 + (24 + 3) / 66.0 * plotH);
                    var before = Frame(window);
                    window.MouseDown(handle, MouseButton.Left);
                    for (int d = 8; d <= 40; d += 8) window.MouseMove(handle + new Point(d, -d));
                    window.MouseUp(handle + new Point(40, -40), MouseButton.Left);
                    Assert.That(cutoff.GetValue(0), Is.GreaterThan(cutoff0), "dragging right raises the cutoff");
                    Assert.That(resonance.GetValue(0), Is.GreaterThan(resonance0), "and up the resonance");
                    Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "the drag is one undo step");
                    Run(0.05);
                    var after = Frame(window, "neo-filter-editor-dragged");
                    Assert.That(Changed(before, after, area), Is.GreaterThan(300), "the response redraws where the handle went");
                    Core.SongCore.Undo();
                    Assert.That((cutoff.GetValue(0), resonance.GetValue(0)), Is.EqualTo((cutoff0, resonance0)), "undo puts both back");
                });
                for (int t = 0; t < chord.Length; t++) note.SetValue(t, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                Remove(filter, poly);
            }
        }

        /// <summary>A parameter's raw value for a value as its machine describes it ("4 Hz"): the nearest by its own texts.</summary>
        static int Params(IMachine m, string name, string text)
        {
            var p = Global(m, name);
            int best = p.MinValue;
            double want = double.Parse(text.Split(' ')[0], System.Globalization.CultureInfo.InvariantCulture), bestErr = double.MaxValue;
            for (int raw = p.MinValue; raw <= p.MaxValue; raw++)
            {
                var d = p.DescribeValue(raw).Split(' ');
                if (d.Length == 0 || !double.TryParse(d[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double v)) continue;
                if (Math.Abs(v - want) < bestErr) { bestErr = Math.Abs(v - want); best = raw; }
            }
            return best;
        }

        // ------------------------------------------------------------------ Neo Scope

        static object Field(object o, string name) =>
            o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(o)!;

        /// <summary>A Neo Scope's newest snapshot (its engine's, through reflection: the types are the machine's own).</summary>
        static object ScopeSnapshot(MachineCore scope)
        {
            var engine = Get(scope.ManagedMachine, "Engine");
            var snapshot = Activator.CreateInstance(engine.GetType().Assembly.GetType("NeoBuzz.Scope.ScopeSnapshot")!)!;
            engine.GetType().GetMethod("Read")!.Invoke(engine, new[] { snapshot });
            return snapshot;
        }

        /// <summary>
        /// What a frame of an editor costs to draw (ms): its content rendered into a bitmap with every graph invalidated,
        /// the audio run a frame's worth between (outside the timing), the best average of three runs of 20 frames.
        /// </summary>
        static double EditorFrameMs(Window window)
        {
            var content = (Control)window.Content!;
            var size = new PixelSize((int)Math.Ceiling(content.Bounds.Width), (int)Math.Ceiling(content.Bounds.Height));
            using var bitmap = new RenderTargetBitmap(size);
            bitmap.Render(content);
            var graphs = content.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "GraphView").ToList();
            double best = double.MaxValue;
            for (int run = 0; run < 3; run++)
            {
                var timer = new System.Diagnostics.Stopwatch();
                for (int k = 0; k < 20; k++)
                {
                    Run(1.0 / 30);                                      // the editors read the snapshot the render published
                    timer.Start();
                    foreach (var g in graphs) g.InvalidateVisual();
                    bitmap.Render(content);
                    timer.Stop();
                }
                best = Math.Min(best, timer.Elapsed.TotalMilliseconds / 20);
            }
            return best;
        }

        /// <summary>
        /// What each of Neo Scope's graphs costs to draw, one setting at a time (noise, then a sine): the numbers behind
        /// its painter's choices (machines.md, Neo Scope). Run by name.
        /// </summary>
        [AvaloniaTest, Explicit("measurements for Neo Scope's painters; run by name")]
        public void NeoScopeEditorCostProbe()
        {
            var song = Core.SongCore;
            var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var sampler = Add("Neo Sampler", "Probe Sampler", -0.6f, 0);
            var scope = Add("Neo Scope", "Probe Scope", 0, 0);
            try
            {
                song.ConnectMachines(sampler, scope, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.ConnectMachines(scope, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Global(sampler, "Z1 wave").SetValue(0, 226);   // noise
                var busy = sampler.ManagedMachine.GetType().GetProperty("Busy")!;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (busy.GetValue(sampler.ManagedMachine) is true && clock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
                var note = sampler.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                note.SetValue(0, (5 << 4) + 1);
                var window = OpenEditor(scope);
                Run(1.0);
                var content = (Control)window.Content!;
                var graphs = content.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "GraphView").ToList();
                double Time(Control c)
                {
                    var size = new PixelSize((int)Math.Ceiling(c.Bounds.Width), (int)Math.Ceiling(c.Bounds.Height));
                    using var bitmap = new RenderTargetBitmap(size);
                    bitmap.Render(c);
                    var t = new System.Diagnostics.Stopwatch();
                    for (int k = 0; k < 20; k++) { Run(1.0 / 30); t.Start(); c.InvalidateVisual(); bitmap.Render(c); t.Stop(); }
                    return t.Elapsed.TotalMilliseconds / 20;
                }
                foreach (var (name, value, label) in new[] { ("Trail", 0, "trail off"), ("Trail", 3, "trail long"), ("Display", 0, "L only"), ("Display", 4, "L&R"), ("Sync", 0, "free 2ms"), ("Sync", 5, "1 bar"), ("Vector", 1, "polar"), ("Vector", 0, "lissajous"),
                    ("Z1 wave", 216, "sine"), ("Sync", 0, "sine free"), ("Sync", 3, "sine 1/4") })
                {
                    (name == "Z1 wave" ? Global(sampler, name) : Global(scope, name)).SetValue(0, value);
                    Run(0.3);
                    TestContext.Out.WriteLine($"[notice] {label}: " + string.Join(", ", graphs.Select(g => $"{g.Bounds.Width:0}x{g.Bounds.Height:0} {Time(g):F2} ms")));
                }
                TestContext.Out.WriteLine($"[notice] the whole editor: {EditorFrameMs(window):F1} ms");
            }
            finally
            {
                Remove(scope, sampler);
            }
        }

        [AvaloniaTest]
        public void NeoScopeStandsALoopStillLocksOnASineAndItsControlsWork()
        {
            // Neo Sampler's built-in kick on the first beat of a one-bar loop → Neo Scope (synced to a bar) → Master. Each pass of
            // the loop draws the same sweep, from the bar's first sample. Then a held sine and the free time base with the trigger:
            // it locks at the level, rising. The controls are one undo step each; Freeze holds the picture; Polar redraws the
            // vectorscope. The scope passes its input bit for bit, and its editor's frames are timed.
            var song = Core.SongCore;
            var master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            var sampler = Add("Neo Sampler", "Scope Kick", -0.6f, 0);
            var scope = Add("Neo Scope", "Scope", 0, 0);
            bool looping = Core.Looping;
            int loopStart = song.LoopStart, loopEnd = song.LoopEnd, songEnd = song.SongEnd;
            try
            {
                song.ConnectMachines(sampler, scope, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                song.ConnectMachines(scope, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                Global(sampler, "Mode").SetValue(0, 1);                       // Kit: zone 1 on C2
                Global(sampler, "Z1 wave").SetValue(0, 201);                  // the built-in kick
                var busy = sampler.ManagedMachine.GetType().GetProperty("Busy")!;
                var clock = System.Diagnostics.Stopwatch.StartNew();
                while (busy.GetValue(sampler.ManagedMachine) is true && clock.ElapsedMilliseconds < 20000) Thread.Sleep(20);
                sampler.CreatePattern("00", 16);
                var pattern = sampler.Patterns.First(p => p.Name == "00");
                Core.SessionLayout.EditNotes(pattern, null, new[] { new ReBuzz.Core.Session.ClipNote(0, PatternEvent.TimeBase * 2, 36, 120) });
                song.AddSequence(sampler, song.Sequences.Count);
                song.Sequences.Last().SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, pattern));
                Core.Looping = true;
                song.LoopStart = 0;
                song.LoopEnd = 16;
                song.SongEnd = 16;
                Global(scope, "Sync").SetValue(0, 5);                         // 1 bar
                var window = OpenEditor(scope);
                var editor = window.Content!;
                Run(0.1);
                var quiet = Frame(window);

                // ---- Synced: three passes of the loop.
                var sweeps = new List<(long Number, double Start, bool Synced, int Filled, int Columns, float[] Max, float[] Min)>();
                lock (ReBuzzCore.AudioLock) Core.Playing = true;
                double bar = 16.0 * ReBuzzCore.masterInfo.SamplesPerTick / ReBuzzCore.masterInfo.SamplesPerSec;
                for (int k = 0; k < (int)(3.6 * bar / 0.05); k++)
                {
                    Run(0.05);
                    var snap = ScopeSnapshot(scope);
                    if (!(bool)Field(snap, "HasFrame")) continue;
                    var f = Field(snap, "Frame");
                    long number = (long)Field(f, "Number");
                    if (sweeps.Count > 0 && sweeps[^1].Number == number) continue;
                    int columns = (int)Field(f, "Columns");
                    sweeps.Add((number, (double)Field(f, "StartBeat"), (bool)Field(f, "Synced"), (int)Field(f, "Filled"), columns,
                        ((float[])Field(f, "AMax")).Take(columns).ToArray(), ((float[])Field(f, "AMin")).Take(columns).ToArray()));
                }
                var synced = Frame(window, "neo-scope-editor-synced");
                Assert.That(sweeps.Count(x => x.Synced), Is.GreaterThanOrEqualTo(3), "a sweep a pass: " + string.Join(", ", sweeps.Select(x => $"#{x.Number} at {x.Start}")));
                var passes = sweeps.Where(x => x.Synced).ToList();
                Assert.Multiple(() =>
                {
                    foreach (var x in passes)
                    {
                        Assert.That(x.Start, Is.EqualTo(0), "each pass's sweep starts on the bar");
                        Assert.That(x.Filled, Is.EqualTo(x.Columns), "and fills the screen");
                        Assert.That(Math.Max(x.Max[0], -x.Min[0]), Is.GreaterThan(0.05f), "the kick starts in the first column");
                    }
                    for (int i = 2; i < passes.Count; i++)
                    {
                        Assert.That(passes[i].Max, Is.EqualTo(passes[1].Max), $"pass {i + 1} draws pass 2's sweep");
                        Assert.That(passes[i].Min, Is.EqualTo(passes[1].Min));
                    }
                    Assert.That((string)Field(Get(editor, "View"), "Right"), Does.StartWith("bar 1.1"), "the editor names the bar");
                    var scopeView = Controls(window, "GraphView").First(g => g.Bounds.Width >= 670);
                    Assert.That(Changed(quiet, synced, BoundsIn(scopeView, window)), Is.GreaterThan(1500), "the scope draws the kick");
                });
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Run(0.2);

                // ---- Free, triggered: a held sine (the built-in one, looped) at C4, rising through 25 %.
                Global(sampler, "Mode").SetValue(0, 0);
                Global(sampler, "Z1 wave").SetValue(0, 216);
                Global(sampler, "Z1 key low").SetValue(0, 0);
                Global(sampler, "Z1 key high").SetValue(0, 127);
                var note = sampler.ParameterGroups[2].Parameters.First(p => p.Name == "Note");
                note.SetValue(0, (5 << 4) + 1);
                var sync = Global(scope, "Sync");
                var trigger = Global(scope, "Trigger");
                var level = Global(scope, "Level");
                sync.SetValue(0, 0);
                Global(scope, "Time/div").SetValue(0, 303);                    // 1 ms a division
                level.SetValue(0, 1250);                                       // +25 %
                Run(0.5);
                var readings = (IReadOnlyDictionary<string, double>)Get(scope.ManagedMachine, "Readings");
                var snapshot = ScopeSnapshot(scope);
                var frame = Field(snapshot, "Frame");
                float first = ((float[])Field(frame, "AMin"))[0], second = ((float[])Field(frame, "AMin"))[1];
                var triggered = Frame(window, "neo-scope-editor-triggered");
                Assert.Multiple(() =>
                {
                    Assert.That(readings["trigger_locked"], Is.EqualTo(1), "locked: " + string.Join(", ", readings.Select(kv => $"{kv.Key} {kv.Value:F2}")));
                    Assert.That((bool)Field(frame, "Triggered"), Is.True);
                    Assert.That(first, Is.InRange(0.25f, 0.30f), "the sweep starts where the sine crosses +25 %");
                    Assert.That(second, Is.GreaterThan(first), "going up");
                    Assert.That(readings["peak_dbfs"], Is.InRange(-12.0, 0.0), "the sine's peak");
                    Assert.That(readings["correlation"], Is.EqualTo(1).Within(1e-6), "a mono sine");
                    Assert.That((string)Field(Get(editor, "View"), "Status"), Is.EqualTo("Trig'd"));
                });

                // ---- The controls: a click on Display's Side, one undo step; Level's knob dragged up; Freeze; Polar.
                var stack = song.ActionStack;
                var display = Find(window, "ParamChoice", "Display");
                var displayParam = Global(scope, "Display");
                int steps = stack.Actions.Count();
                var cell = (Rect)display.GetType().GetMethod("CellBounds")!.Invoke(display, new object[] { 3 })!;
                Click(window, At(display, window, cell.Center.X, cell.Center.Y));
                Assert.That(displayParam.GetValue(0), Is.EqualTo(3), "a click picks Side");
                Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                song.Undo();
                Assert.That(displayParam.GetValue(0), Is.EqualTo(4), "undo: L & R again");
                int level0 = level.GetValue(0);
                var knob = Find(window, "ParamKnob", "Level");
                var c = Centre(knob, window);
                MachineViewTests.Drag(window, c, c - new Point(0, 30));
                Assert.That(level.GetValue(0), Is.GreaterThan(level0), "dragging Level up raises it");
                song.Undo();
                Assert.That(level.GetValue(0), Is.EqualTo(level0));

                var freeze = Find(window, "ParamChoice", "Freeze");
                var on = (Rect)freeze.GetType().GetMethod("CellBounds")!.Invoke(freeze, new object[] { 1 })!;
                Click(window, At(freeze, window, on.Center.X, on.Center.Y));
                Assert.That(Global(scope, "Freeze").GetValue(0), Is.EqualTo(1), "Freeze on");
                Run(0.1);
                var held = Frame(window);
                note.SetValue(0, (6 << 4) + 8);                                // another note: the frozen picture stays
                Run(0.3);
                var stillHeld = Frame(window, "neo-scope-editor-frozen");
                var scopeArea = BoundsIn(Controls(window, "GraphView").First(g => g.Bounds.Width >= 670), window);
                Assert.That(Changed(held, stillHeld, scopeArea), Is.LessThan(200), "frozen, the scope holds (the status aside)");
                Assert.That((string)Field(Get(editor, "View"), "Status"), Is.EqualTo("Frozen"));
                Global(scope, "Freeze").SetValue(0, 0);

                var vectorArea = BoundsIn(Controls(window, "GraphView").First(g => Math.Abs(g.Bounds.Width - 240) < 1 && g.Bounds.Height > 200), window);
                Global(sampler, "Z1 pan").SetValue(0, 30);                      // −70 %: the goniometer leans left
                Run(0.3);
                var lissajous = Frame(window);
                var vector = Find(window, "ParamChoice", "Vector");
                var polarCell = (Rect)vector.GetType().GetMethod("CellBounds")!.Invoke(vector, new object[] { 1 })!;
                Click(window, At(vector, window, polarCell.Center.X, polarCell.Center.Y));
                Assert.That(Global(scope, "Vector").GetValue(0), Is.EqualTo(1), "Polar");
                Run(0.1);
                var polar = Frame(window, "neo-scope-editor-polar");
                Assert.That(Changed(lissajous, polar, vectorArea), Is.GreaterThan(300), "the vectorscope redraws as polar");

                // ---- The editor's cost: the whole editor drawn into a bitmap, every graph redrawn, with the audio running
                // (the trail on, so the last sweeps are drawn too), against Neo Analyzer's editor on the same sound.
                Global(scope, "Trail").SetValue(0, 3);
                Global(scope, "Sync").SetValue(0, 3);
                var analyzer = Add("Neo Analyzer", "Scope Analyzer", 0.4f, 0);
                try
                {
                    song.ConnectMachines(sampler, analyzer, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
                    var analyzerWindow = OpenEditor(analyzer);
                    Run(0.5);
                    double scopeMs = EditorFrameMs(window), analyzerMs = EditorFrameMs(analyzerWindow);
                    TestContext.Out.WriteLine($"[notice] Neo Scope's editor in the studio (headless, Skia in software, {window.ClientSize.Width:0} x {window.ClientSize.Height:0}): " +
                                              $"{scopeMs:F1} ms a frame, every graph redrawn; Neo Analyzer's {analyzerMs:F1} ms ({analyzerWindow.ClientSize.Width:0} x {analyzerWindow.ClientSize.Height:0})");
                    Assert.That(scopeMs, Is.LessThan(Math.Max(3 * analyzerMs, 20)), "a frame costs about what Neo Analyzer's does");
                }
                finally
                {
                    Remove(analyzer);
                }

                // ---- Bit for bit: the machine's Work, loud random samples in.
                var instance = scope.ManagedMachine;
                var work = instance.GetType().GetMethod("Work", new[] { typeof(Buzz.MachineInterface.Sample[]), typeof(Buzz.MachineInterface.Sample[]), typeof(int), typeof(Buzz.MachineInterface.WorkModes) })!;
                var rng = new Random(7);
                var input = Enumerable.Range(0, 1024).Select(i => new Buzz.MachineInterface.Sample(
                    i == 3 ? float.MaxValue : i == 4 ? -0f : (float)(rng.NextDouble() * 2 - 1) * 32000f, (float)(rng.NextDouble() * 2 - 1) * 40000f)).ToArray();
                var output = new Buzz.MachineInterface.Sample[1024];
                lock (ReBuzzCore.AudioLock) work.Invoke(instance, new object[] { output, input, 1024, Buzz.MachineInterface.WorkModes.WM_READWRITE });
                Assert.That(output.Zip(input).All(p => BitConverter.SingleToInt32Bits(p.First.L) == BitConverter.SingleToInt32Bits(p.Second.L)
                    && BitConverter.SingleToInt32Bits(p.First.R) == BitConverter.SingleToInt32Bits(p.Second.R)), Is.True, "the output is the input, bit for bit");

                note.SetValue(0, BuzzGUI.Common.BuzzNote.Off);
                Run(0.1);
            }
            finally
            {
                lock (ReBuzzCore.AudioLock) Core.Playing = false;
                Core.Looping = looping;
                song.LoopStart = 0;
                song.LoopEnd = loopEnd;
                song.LoopStart = loopStart;
                song.SongEnd = songEnd;
                foreach (var seq in song.Sequences.Where(q => q.Machine == sampler).ToList()) song.RemoveSequence(seq);
                Remove(scope, sampler);
            }
        }
    }
}
