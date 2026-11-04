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
            ("Neo Ensemble", "NeoEnsembleEditor", "neo-ensemble-editor", 1000, 330),
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

                // A double-click puts it back to its default, also undoable. (Two clicks make one
                // only within the double-click time, in real time: a loaded CI machine may miss it.)
                int clickCount = 0;
                knob.AddHandler(InputElement.PointerPressedEvent, (s, a) => clickCount = a.ClickCount, RoutingStrategies.Tunnel);
                Click(window, new Point(2, 2));   // elsewhere first: the drag's press doesn't count as a first click
                Click(window, c);
                Click(window, c);
                if (clickCount == 2)
                {
                    Assert.That(threshold.GetValue(0), Is.EqualTo(700), "a double-click resets the knob to its default");
                    Core.SongCore.Undo();
                    Assert.That(threshold.GetValue(0), Is.EqualTo(after));
                }
                else Assert.Warn("The two clicks came too far apart for a double-click; the reset wasn't tried.");

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
    }
}
