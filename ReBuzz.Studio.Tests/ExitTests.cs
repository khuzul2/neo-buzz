// Fork (docs/specs/studio-mvp.md S9): P3's exit test, headless. A song made from an empty one by
// simulated mouse and keys alone (machine view, a Neo editor, a parameter window, the Pattern and
// Sequence tabs, the transport, the Session View), played through the engine, saved as .nbl
// and opened again: the same song, and the same samples. And the MCP server's start-up option, which CI uses to run the
// A7 live scenarios against the real app (tools/headless-live.py --studio). Screenshots: exit-*.png.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Machines;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class ExitTests
    {
        const int Row = ClipEvent.TimeBase;      // a row of the pattern editor at 4 rows a beat

        static ReBuzzCore Core => Studio.Host.Core;

        // Not a [TearDown]: each test runs it when it ends, failed or not (Studio.AfterTest).
        public void PutBack()
        {
            SongFiles.Picker = null;
            Dialogs.Responder = null;
            Dialogs.TextResponder = null;
            SessionCore.Timestamp = Stopwatch.GetTimestamp;
            TestTemp.Delete(TestTemp.Path($"rebuzz-studio-exit-{Environment.ProcessId}"));   // its songs and their histories: a few hundred MB per run
        }

        static string Folder(string name)
        {
            string dir = TestTemp.Path($"rebuzz-studio-exit-{Environment.ProcessId}", name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        static void Key(MainWindow window, PhysicalKey key, RawInputModifiers mods = RawInputModifiers.None)
        {
            window.KeyPressQwerty(key, mods);
            window.KeyReleaseQwerty(key, mods);
            Settle();
        }

        static void Keys(MainWindow window, params PhysicalKey[] keys)
        {
            foreach (var k in keys) Key(window, k);
        }

        /// <summary>A control's centre (or a point across it) in its window's pixels.</summary>
        static Point CentreOf(Control c, TopLevel top, double fx = 0.5, double fy = 0.5) =>
            c.TranslatePoint(new Point(c.Bounds.Width * fx, c.Bounds.Height * fy), top)!.Value;

        static void ClickControl(Control c, MouseButton button = MouseButton.Left, double fx = 0.5, double fy = 0.5)
        {
            var top = TopLevel.GetTopLevel(c)!;
            top.UpdateLayout();
            Click(top, CentreOf(c, top, fx, fy), button);
        }

        /// <summary>Opens a drop-down with a click and clicks the item shown as <paramref name="text"/>.</summary>
        static void Choose(ComboBox box, string text)
        {
            ClickControl(box);
            Assert.That(box.IsDropDownOpen, Is.True, "a click opens the drop-down");
            int index = box.Items.Cast<object>().ToList().FindIndex(i => i?.ToString() == text);
            Assert.That(index, Is.GreaterThanOrEqualTo(0), $"the drop-down has {text} ({string.Join(", ", box.Items.Cast<object>())})");
            var item = (Control)box.ContainerFromIndex(index)!;
            ClickControl(item);
            Assert.That(box.SelectedItem?.ToString(), Is.EqualTo(text));
        }

        // ------------------------------------------------------------------ playing

        /// <summary>
        /// Plays the song as a user does: the position to the start, F5, the engine's ticks rendered
        /// offline for <paramref name="ticks"/> ticks (a WorkManager filling buffers, as the audio
        /// thread would), F8, then half a second of tail. Like the agent's render, it starts on a tick
        /// boundary after a silent run-in, so the same song from fresh machines gives the same samples.
        /// Returns the interleaved stereo output; <paramref name="atTick"/> runs after each tick.
        /// </summary>
        static float[] Play(MainWindow window, int ticks, Action<int> atTick = null, int from = 0)
        {
            var core = Core;
            var song = core.SongCore;
            var info = ReBuzzCore.masterInfo;
            var wm = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
            var buffer = new float[512];
            var output = new List<float>();
            void Fill(int frames, bool keep)
            {
                while (frames > 0)
                {
                    int n = Math.Min(frames, buffer.Length / 2);
                    wm.MainAudioFillBuffer(buffer, 0, n * 2);
                    if (keep) output.AddRange(buffer.Take(n * 2));
                    frames -= n;
                }
            }
            lock (ReBuzzCore.AudioLock)
            {
                song.PlayPosition = from;
                info.PosInTick = 0;
                var sub = ReBuzzCore.subTickInfo;
                sub.PosInSubTick = 0;
                sub.CurrentSubTick = 0;
                sub.SubTickReminderCounter = 0;
                Fill(info.SamplesPerSec / 10, keep: true);           // a tenth of a second first: silence
                Assert.That(output.All(x => x == 0), Is.True, "silence before Play");
                output.Clear();
                Key(window, PhysicalKey.F5);
                for (int guard = 0; !core.Playing && guard < 100000; guard++)
                    Fill(info.PosInTick == 0 ? 1 : Math.Max(1, info.SamplesPerTick - info.PosInTick), keep: false);
                Assert.That(core.Playing, Is.True, "F5 plays");
                for (int t = 0; t < ticks; t++)
                {
                    Fill(Math.Max(1, info.SamplesPerTick - info.PosInTick), keep: true);
                    atTick?.Invoke(t + 1);
                }
                Key(window, PhysicalKey.F8);
                Assert.That(core.Playing, Is.False, "F8 stops");
                Fill(info.SamplesPerSec / 2, keep: true);
            }
            Settle();
            return output.ToArray();
        }

        static (double Peak, double RmsDb) Measure(float[] samples)
        {
            double peak = 0, sum = 0;
            foreach (var x in samples)
            {
                peak = Math.Max(peak, Math.Abs(x));
                sum += x * (double)x;
            }
            return (peak, 10 * Math.Log10(Math.Max(sum / Math.Max(1, samples.Length), 1e-30)));
        }

        // ------------------------------------------------------------------ what a song file must give back

        /// <summary>
        /// The song as lines of text: machines (gear, place, tracks, data, every state parameter's value),
        /// wires (amp, pan), patterns (the editor's events and the engine's columns), the pattern
        /// editors' data, the sequences and markers, and the Session View's slots.
        /// </summary>
        static List<string> Snapshot()
        {
            var core = Core;
            var song = core.SongCore;
            var lines = new List<string>
            {
                $"song: {core.BPM} BPM, {core.TPB} TPB, loop {song.LoopStart}-{song.LoopEnd}, end {song.SongEnd}",
            };
            string Hex(byte[] b) => b == null ? "none" : Convert.ToHexString(b);
            foreach (var m in song.MachinesList.Where(m => !m.Hidden).OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                lines.Add($"machine {m.Name}: {m.DLL.Name} at {m.Position.Item1:R}, {m.Position.Item2:R}; {m.TrackCount} tracks; muted {m.IsMuted}");
                string at = "  " + m.Name + ": ";
                if (m.DLL.Info.Type != MachineType.Master)
                    lines.Add($"{at}data {Hex(core.MachineManager.GetMachineData(m))}");
                foreach (var (p, track) in m.AllParametersAndTracks().Where(x => x.Item1.Flags.HasFlag(ParameterFlags.State)).Select(x => (x.Item1, x.Item2)))
                    lines.Add($"{at}{p.Group.Type} {p.Name}[{track}] = {p.GetValue(track)}");
                foreach (var c in m.Outputs.Where(c => !((MachineCore)c.Destination).Hidden).OrderBy(c => c.Destination.Name, StringComparer.Ordinal))
                    lines.Add($"{at}wire to {c.Destination.Name}: amp {c.Amp}, pan {c.Pan}");
                foreach (var p in m.Clips.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    lines.Add($"{at}pattern {p.Name}: {p.Length} ticks");
                    foreach (var c in p.ClipEditorMachineEvents.Where(c => c.Events.Any()))       // clips CU3: an editor that views the store holds no empty columns of its own
                        lines.Add($"{at}pattern {p.Name}, editor column {c.Parameter.Name}: {string.Join(" ", c.Events.Select(e => $"{e.Time}={e.Value}/{e.Duration}"))}");
                    foreach (var c in p.Columns)
                        lines.Add($"{at}pattern {p.Name}, column {c.Type} {c.Machine?.Name} {c.Parameter?.Name}[{c.Track}]: {string.Join(" ", c.GetEvents(0, int.MaxValue).Select(e => $"{e.Time}={e.Value}/{e.Duration}"))}");
                }
                if (m.EditorMachine != null && m.Clips.Count > 0)
                    lines.Add($"{at}pattern editor {m.EditorMachine.DLL.Name}: {Hex(core.MachineManager.GetMachineData(m.EditorMachine))}");
            }
            foreach (var s in song.Sequences)
                lines.Add($"sequence {s.Machine.Name}{(s.IsDisabled ? " (disabled)" : "")}: " +
                          string.Join(" ", s.Events.Select(e => $"{e.Key}:{(e.Value.Type == SequenceEventType.PlayClip ? e.Value.Clip.Name : e.Value.Type.ToString())}")));
            var layout = core.SessionLayout;
            for (int t = 0; t < layout.TrackCount; t++)
                lines.Add($"session track {layout.SequenceOf(t)?.Machine.Name}: " +
                          string.Join(" ", Enumerable.Range(0, layout.SceneCount).Select(s => layout.ClipAt(t, s)?.Name ?? "-")));
            return lines;
        }

        static void AssertSame(List<string> before, List<string> after, string what)
        {
            var lost = before.Except(after).Take(8).ToList();
            var gained = after.Except(before).Take(8).ToList();
            Assert.That(lost.Count + gained.Count, Is.EqualTo(0),
                $"{what}: the song changed.\n  before only:\n    {string.Join("\n    ", lost)}\n  after only:\n    {string.Join("\n    ", gained)}");
            Assert.That(after, Is.EqualTo(before), what + ": the same, in the same order");
        }

        // ------------------------------------------------------------------ 1. a song from scratch, by the UI alone

        [AvaloniaTest]
        public void ASongFromScratchByTheUiAloneSavesAndOpensAsItWas()
        {
            using var restore = Studio.AfterTest(PutBack);
            var (window, view) = OpenEmpty();
            ToolTip.SetServiceEnabled(window, false);
            var master = Machine("Master");
            // The transport as the app starts: looping off. It isn't part of a song (a new song keeps it), so it is
            // whatever the tests before left, and a failed [AvaloniaTest] skips the TearDown that would put it back.
            Core.Looping = false;
            try
            {
                Studio.Screenshot(window, "exit-1-empty");

                // ---- The machine view: four machines from the gear menu.
                Menu(window, view, view.PointOf(-0.8, -0.5, window), null, "New", "Generators", "Neo Poly");
                var poly = MachineOf("Neo Poly");
                Assert.That(poly, Is.Not.Null, "Neo Poly from the gear menu: " + Graph());
                Menu(window, view, view.PointOf(-0.8, 0.45, window), null, "New", "Generators", "Keys Test");
                var bass = MachineOf("Keys Test");
                Assert.That(bass, Is.Not.Null, "Keys Test from the gear menu: " + Graph());
                Menu(window, view, view.PointOf(-0.35, 0, window), null, "New", "Effects", "Neo Ensemble");
                var chorus = MachineOf("Neo Ensemble");
                Assert.That(chorus, Is.Not.Null, "Neo Ensemble from the gear menu");
                Menu(window, view, view.PointOf(0.15, 0, window), null, "New", "Effects", "Neo Verb");
                var verb = MachineOf("Neo Verb");
                Assert.That(verb, Is.Not.Null, "Neo Verb from the gear menu");
                // As in WPF: generators added to a song without effects are wired to Master.
                Assert.That(Graph(), Is.EqualTo(string.Join("; ", new[] { chorus.Name, "Master", poly.Name, bass.Name, verb.Name }.OrderBy(n => n, StringComparer.Ordinal))
                                                + "; " + string.Join("; ", new[] { $"{poly.Name}→Master", $"{bass.Name}→Master" }.OrderBy(n => n, StringComparer.Ordinal))));

                // Master to the right, by drag; then the chain by Shift-drag: synths → Chorus → Verb → Master.
                var masterAt = view.PointOf(master, window);
                Drag(window, masterAt, view.PointOf(0.65, 0, window));
                Assert.That(master.Position.Item1, Is.EqualTo(0.65).Within(0.01), "Master moved");
                Drag(window, view.PointOf(poly, window), view.PointOf(chorus, window), mods: RawInputModifiers.Shift);
                Drag(window, view.PointOf(bass, window), view.PointOf(chorus, window), mods: RawInputModifiers.Shift);
                Drag(window, view.PointOf(chorus, window), view.PointOf(verb, window), mods: RawInputModifiers.Shift);
                Drag(window, view.PointOf(verb, window), view.PointOf(master, window), mods: RawInputModifiers.Shift);
                // The synths' direct wires to Master go, with the amp control's disconnect.
                foreach (var synth in new[] { poly, bass })
                {
                    Click(window, view.ArrowOf(Wire(synth.Name, "Master"), window), MouseButton.Right);
                    Assert.That(view.Amp.IsVisible, Is.True, "a right click on the arrow opens the amp control");
                    Click(window, AmpPart(view, window, "disconnect"));
                }
                string chain = $"{poly.Name}→{chorus.Name}; {bass.Name}→{chorus.Name}; {chorus.Name}→{verb.Name}; {verb.Name}→Master";
                Assert.That(Graph().Split("; ").Where(x => x.Contains('→')).OrderBy(x => x), Is.EqualTo(chain.Split("; ").OrderBy(x => x)), "the chain");

                // A wire's amp: Keys Test → Ensemble, its arrow pressed and dragged down (the amp knob); and the
                // pan of Verb → Master (a stereo input) with the pan knob. Leaving the control commits.
                var arrow = view.ArrowOf(Wire(bass.Name, chorus.Name), window);
                Drag(window, arrow, arrow + new Vector(0, 20));
                Assert.That(view.Amp.AmpTextValue, Is.EqualTo("-12.2dB"));
                window.MouseMove(arrow + new Vector(300, 250));
                Settle();
                Assert.That(Wire(bass.Name, chorus.Name).Amp, Is.EqualTo(4025), "Keys Test's wire at -12.2 dB");
                var verbOut = view.ArrowOf(Wire(verb.Name, "Master"), window);
                Click(window, verbOut, MouseButton.Right);
                Assert.That(view.Amp.IsVisible, Is.True);
                var pan = AmpPart(view, window, "pan");
                Drag(window, pan, pan + new Vector(0, 20));
                window.MouseMove(verbOut + new Vector(-300, 250));
                Settle();
                Assert.That(Wire(verb.Name, "Master").Pan, Is.LessThan(0x4000), "Verb panned left a little");
                int pannedTo = Wire(verb.Name, "Master").Pan;
                Studio.Screenshot(window, "exit-2-machines");

                // ---- Machine windows: a knob in Neo Poly's own editor (a double click opens it).
                DoubleClick(window, view.PointOf(poly, window));
                var editor = StudioMachineUiHost.GuiWindowOf(poly);
                Assert.That(editor?.IsVisible, Is.True, "a double click opens Neo Poly's editor");
                Assert.That(StudioMachineUiHost.ContentOf(editor)?.GetType().Name, Is.EqualTo("NeoPolyEditor"));
                SessionRig.Quiet(editor);
                Dispatcher.UIThread.RunJobs();
                editor.UpdateLayout();
                var knobs = editor.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == "ParamKnob").ToList();
                var knob = knobs.FirstOrDefault(k => (AutomationProperties.GetName(k) ?? "").Contains("utoff"));
                Assert.That(knob, Is.Not.Null, "the cutoff knob: " + string.Join(", ", knobs.Select(k => AutomationProperties.GetName(k))));
                var globals = poly.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters;
                var before = globals.Select(p => p.GetValue(0)).ToArray();
                int undoSteps = Core.SongCore.ActionStack.Actions.Count();
                var c0 = CentreOf(knob!, editor);
                editor.MouseDown(c0, MouseButton.Left);
                for (int dy = 8; dy <= 40; dy += 8) editor.MouseMove(c0 - new Point(0, dy), RawInputModifiers.LeftMouseButton);
                editor.MouseUp(c0 - new Point(0, 40), MouseButton.Left);
                Settle();
                var changed = globals.Where((p, i) => p.GetValue(0) != before[i]).ToList();
                Assert.That(changed.Select(p => p.Name), Is.EqualTo(new[] { "F1 cutoff" }), "the knob turned F1 cutoff alone");
                Assert.That(changed[0].GetValue(0), Is.GreaterThan(before[globals.IndexOf(changed[0])]), "up, brighter");
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(undoSteps + 1), "one undo step");
                Studio.Screenshot(editor, "exit-3-neo-poly-editor");

                // ---- The Pattern tab (F2): notes typed into each synth's pattern. A click on empty space
                // in the machine view first: the keyboard follows the window clicked last.
                Click(window, view.PointOf(0.3, 0.6, window));
                Key(window, PhysicalKey.F2);
                var page = ClipView.Page;
                Assert.That(window.Detail.IsMaximised, Is.True, "F2: the Tracker over the whole window");
                var boxes = page.GetLogicalDescendants().OfType<ComboBox>().ToList();
                ComboBox machineBox = boxes[0], clipBox = boxes[1];

                // Click the grid's first field on row 0 (track 0's Note column: the notes come first), type.
                void TypeNotes(params PhysicalKey[] keys)
                {
                    Settle();
                    dynamic grid = ((dynamic)page.EditorControl).TrackerGrid;
                    var body = (Control)grid.Body;
                    Point field = grid.FieldCenter(0, 0, 0, 0);
                    Click(window, body.TranslatePoint(field, window)!.Value);
                    Keys(window, keys);
                }
                (int, int)[] Notes(IClip p) => p.ClipEditorMachineEvents.First(c => c.Parameter.Name == "Note").Events.Select(e => (e.Time, e.Value)).OrderBy(e => e.Time).ToArray();
                const PhysicalKey down = PhysicalKey.ArrowDown;

                Choose(machineBox, poly.Name);
                Choose(clipBox, "00");
                Assert.That(page.Clip, Is.SameAs(poly.Clips.Single(p => p.Name == "00")), "Poly's pattern from the gear menu");
                TypeNotes(PhysicalKey.Z, down, down, down, PhysicalKey.C, down, down, down, PhysicalKey.B, down, down, down, PhysicalKey.Q);
                Assert.That(Notes(page.Clip), Is.EqualTo(new[] { (0, BuzzNote.Parse("C-4")), (4 * Row, BuzzNote.Parse("E-4")), (8 * Row, BuzzNote.Parse("G-4")), (12 * Row, BuzzNote.Parse("C-5")) }));
                Studio.Screenshot(window, "exit-5-pattern-poly");

                // New pattern (the button): Poly's 01.
                ClickControl(page.GetLogicalDescendants().OfType<Button>().Single(b => b.Content as string == "New pattern"));
                Settle();
                var poly01 = poly.Clips.SingleOrDefault(p => p.Name == "01");
                Assert.That(poly01, Is.Not.Null, "New pattern made 01");
                Assert.That(page.Clip, Is.SameAs(poly01));
                TypeNotes(PhysicalKey.X, down, PhysicalKey.V, down, down, PhysicalKey.N, down, down, down, down, PhysicalKey.X, down, down, PhysicalKey.Digit1);
                Assert.That(Notes(poly01), Is.EqualTo(new[] { (0, BuzzNote.Parse("D-4")), (2 * Row, BuzzNote.Parse("F-4")), (5 * Row, BuzzNote.Parse("A-4")), (10 * Row, BuzzNote.Parse("D-4")), (13 * Row, BuzzNote.Off) }));

                // Keys Test's 00, in its Note column (it plays notes only from there).
                Choose(machineBox, bass.Name);
                Choose(clipBox, "00");
                TypeNotes(PhysicalKey.Z, down, PhysicalKey.Digit1, down, PhysicalKey.B, down, PhysicalKey.Digit1, down, PhysicalKey.Z, down, PhysicalKey.Digit1, down, PhysicalKey.N, down, PhysicalKey.Digit1);
                var bass00 = bass.Clips.Single(p => p.Name == "00");
                Assert.That(Notes(bass00).Length, Is.EqualTo(8), "four notes and their offs");
                Studio.Screenshot(window, "exit-6-pattern-keys");

                // ---- The Sequence tab (F4): the patterns placed by their keys, then the loop and the song end.
                Key(window, PhysicalKey.F4);
                var sequence = (SequenceView)((TabItem)window.Tabs.SelectedItem!).Content!;
                var canvas = sequence.Canvas;
                Point Cell(int time, int row)
                {
                    Settle();
                    var g = canvas.Geometry;
                    return canvas.TranslatePoint(new Point(g.XOf(time) + 6, g.YOf(row) + 0.6 * g.TrackHeight), window)!.Value;
                }
                void Type(string text)
                {
                    foreach (char ch in text) window.KeyTextInput(ch.ToString());
                    Settle();
                }
                string Events(int row) => string.Join(" ", Core.Song.Sequences[row].Events.Select(e => $"{e.Key}:{e.Value.Clip?.Name}"));
                Assert.That(Core.Song.Sequences.Select(s => s.Machine), Is.EqualTo(new[] { poly, bass }), "a track for each synth (the gear menu made them)");
                Assert.That(Events(0), Is.EqualTo("0:00"), "the first machine's pattern plays from the start (as in WPF)");
                Click(window, Cell(16, 0));
                Type("1");
                Click(window, Cell(0, 1));
                Type("00");
                Assert.That(Events(0), Is.EqualTo("0:00 16:01"));
                Assert.That(Events(1), Is.EqualTo("0:00 16:00"));
                var song = Core.SongCore;
                Click(window, Cell(16, 0));
                Key(window, PhysicalKey.B, Cmd);
                Click(window, Cell(48, 0));
                Key(window, PhysicalKey.E, Cmd);
                Assert.That((song.LoopStart, song.LoopEnd, song.SongEnd), Is.EqualTo((16, 64, 64)), "Ctrl+B, Ctrl+E: the loop (and the song end with it)");
                Key(window, PhysicalKey.ArrowLeft);
                Key(window, PhysicalKey.ArrowLeft);
                Key(window, PhysicalKey.E, Cmd);
                Key(window, PhysicalKey.E, Cmd);
                Assert.That((song.LoopStart, song.LoopEnd, song.SongEnd), Is.EqualTo((16, 32, 32)), "the loop's end, then the song's (Ctrl+E twice)");

                // The tempo, typed into the transport's BPM box; looping on with its button.
                var bpmBox = window.Transport.Tempo.Box;
                MachineViewTests.DoubleClick(window, CentreOf(bpmBox, window));           // a double click on the tempo types it (a drag changes it)
                bpmBox.SelectAll();
                Type("132");
                Key(window, PhysicalKey.Enter);
                Assert.That(Core.BPM, Is.EqualTo(132), "the BPM box");
                // Loop on failed now and then on CI, Looping false after the click: Looping was already on, left by a test
                // before (run 41644026501: the session rig, before it put it back; 41866682998, 41926371493, 41937966540,
                // 41938676130, 41953730788: AudioHygieneTests' first play had failed, its TearDown skipped), so the click
                // turned it off. The test starts with it off now; a failure still says what the button saw.
                var loopButton = window.Transport.GetVisualDescendants().OfType<ToggleButton>().Single(b => b.Content as string == "Loop");
                string beforeClick = $"before the click: Looping {Core.Looping}, the button {loopButton.IsChecked}, focus on {window.FocusManager?.GetFocusedElement()?.GetType().Name ?? "nothing"}, " +
                                $"under the pointer {(window.InputHitTest(CentreOf(loopButton, window)) as Visual)?.GetType().Name ?? "nothing"}";
                var seen = new List<string>();
                EventHandler<PointerPressedEventArgs> pressed = (_, e) => seen.Add($"pressed (handled {e.Handled})");
                EventHandler<PointerReleasedEventArgs> released = (_, e) => seen.Add($"released (handled {e.Handled})");
                EventHandler<Avalonia.Interactivity.RoutedEventArgs> clicked = (_, _) => seen.Add($"click (the button {loopButton.IsChecked}, Looping {Core.Looping})");
                loopButton.AddHandler(InputElement.PointerPressedEvent, pressed, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                loopButton.AddHandler(InputElement.PointerReleasedEvent, released, Avalonia.Interactivity.RoutingStrategies.Bubble, handledEventsToo: true);
                loopButton.Click += clicked;
                ClickControl(loopButton);
                loopButton.RemoveHandler(InputElement.PointerPressedEvent, pressed);
                loopButton.RemoveHandler(InputElement.PointerReleasedEvent, released);
                loopButton.Click -= clicked;
                Assert.That(Core.Looping, Is.True, $"Loop on; {beforeClick}; the button saw: {string.Join(", ", seen)}; after it: the button {loopButton.IsChecked}");
                canvas.Focus();
                Studio.Screenshot(window, "exit-7-arranged");

                // ---- Play: through the engine, from the start, past the loop's end (it goes back to 16).
                bool looped = false;
                int last = 0;
                var played = Play(window, 48, tick =>
                {
                    if (song.PlayPosition < last) looped = true;
                    last = song.PlayPosition;
                    if (tick == 24) Studio.Screenshot(window, "exit-8-playing");
                });
                Assert.That(looped, Is.True, "the loop took the play position back");
                Assert.That(played.All(float.IsFinite), Is.True, "no NaN or infinity");
                var (peak, rms) = Measure(played);
                TestContext.Out.WriteLine($"played {played.Length / 2} frames: peak {20 * Math.Log10(peak):F1} dBFS, RMS {rms:F1} dBFS");
                Assert.That(peak, Is.GreaterThan(0.05), "the Master is audible");
                Assert.That(peak, Is.LessThan(4.0), "nothing diverged (under +12 dBFS)");
                Assert.That(rms, Is.GreaterThan(-45));

                // ---- The Session View (Ctrl+Tab): a pattern into an empty slot, and launched there.
                Key(window, PhysicalKey.Tab, RawInputModifiers.Control);
                var session = (SessionView)((TabItem)window.Tabs.SelectedItem!).Content!;
                Settle();
                var layout = Core.SessionLayout;
                var slot = session.Slots.First(s => s.Track == 1 && s.Clip == null && s.Scene > 0);
                int scene = slot.Scene;
                ClickControl(slot, MouseButton.Right, fx: 0.6);
                Assert.That(session.ClickMenuItem("Assign pattern/00"), Is.True, "the slot's menu assigns Keys Test's 00");
                Settle();
                Assert.That(layout.ClipAt(1, scene), Is.SameAs(bass00), "the slot holds it");
                SessionCore.Timestamp = () => 1_000_000;          // heard = rendered, as SessionRig does
                using (var pump = new SequenceEditorTests.Player())
                {
                    ClickControl(session.SlotAt(1, scene).LaunchArea);
                    var engine = (SessionCore)Core.Session;
                    Assert.That(engine.GetState(layout.SequenceOf(1), bass00), Is.EqualTo(ClipState.Queued).Or.EqualTo(ClipState.Playing), "▶ launches it");
                    pump.Ticks(20);
                    session.RefreshState();
                    Assert.That(engine.GetState(layout.SequenceOf(1), bass00), Is.EqualTo(ClipState.Playing), "it plays");
                    Studio.Screenshot(window, "exit-9-session");
                    ClickControl(session.StopAllButton);
                    pump.Ticks(4);
                    ClickControl(session.BackButton);
                    pump.Ticks(2);
                }
                Key(window, PhysicalKey.F8);
                Key(window, PhysicalKey.F8);
                SessionCore.Timestamp = Stopwatch.GetTimestamp;

                // The playing left stateless parameters (notes too) at their last values, and the song
                // files keep them. Opening a song mustn't play them again: the loader skips them. The renders after
                // reopening, bit-identical to the one made before the session played, show it.
                int left = 0;
                foreach (var m in Core.SongCore.MachinesList.Where(m => !m.Hidden))
                    foreach (var (p, track) in m.AllParametersAndTracks().Where(x => x.Item1.Group.Type != ParameterGroupType.Input && !x.Item1.Flags.HasFlag(ParameterFlags.State)).Select(x => (x.Item1, x.Item2)))
                        if (p.GetValue(track) != p.NoValue)
                        {
                            TestContext.Out.WriteLine($"{m.Name}: {p.Name}[{track}] was left at {p.GetValue(track)} by the playing");
                            left++;
                        }
                Assert.That(left, Is.GreaterThan(0), "the playing left a stateless value for the files to keep (what this checks)");

                // ---- Save as .nbl (Shift+Ctrl/Command+S), a new song (Ctrl/Command+N), each opened again (Ctrl/Command+O).
                var made = Snapshot();
                Assert.That(made.Count(l => l.StartsWith("machine ")), Is.EqualTo(5));
                string dir = Folder("from-scratch");
                foreach (var ext in new[] { ".nbl" })
                {
                    string path = Path.Combine(dir, "exit song" + ext);
                    SongFiles.Picker = saving => saving ? path : null;
                    Key(window, PhysicalKey.S, Cmd | RawInputModifiers.Shift);
                    Assert.That(File.Exists(path), Is.True, ext + " saved");
                    Assert.That(Core.Modified, Is.False);
                }
                foreach (var ext in new[] { ".nbl" })
                {
                    string path = Path.Combine(dir, "exit song" + ext);
                    Key(window, PhysicalKey.N, Cmd);
                    Assert.That(Graph(), Is.EqualTo("Master"), "a new song");
                    SongFiles.Picker = saving => saving ? null : path;
                    Key(window, PhysicalKey.O, Cmd);
                    Settle();
                    Assert.That(Core.SongCore.SongName, Is.EqualTo(path), ext + " opened");
                    AssertSame(made, Snapshot(), ext);
                    var again = Play(window, 48);
                    Assert.That(again.Length, Is.EqualTo(played.Length), ext + ": as many samples");
                    int first = Enumerable.Range(0, played.Length).FirstOrDefault(i => BitConverter.SingleToInt32Bits(played[i]) != BitConverter.SingleToInt32Bits(again[i]), -1);
                    Assert.That(first, Is.EqualTo(-1), $"{ext}: the render is bit-identical (first difference at frame {first / 2}: {(first >= 0 ? played[first] : 0)} vs {(first >= 0 ? again[first] : 0)})");
                    Assert.That(Wire(verb.Name, "Master").Pan, Is.EqualTo(pannedTo));
                    window.ShowView("Sequence");
                    Settle();
                    Studio.Screenshot(window, "exit-11-reopened");
                }
            }
            finally
            {
                foreach (var m in Core.SongCore.MachinesList.ToList()) Core.UiHost.ReleaseMachine(m);
                Close(window);
                SongFiles.New().Wait();
            }
        }

        [AvaloniaTest]
        public void TheAgentStartupOptionAllowsAgentsForOneRunWithoutSavingIt()
        {
            using var restore = Studio.AfterTest(PutBack);
            // ReBuzz.Studio --agent-port <p> --agent-token <t>, as CI starts the app for the live
            // scenarios (tools/headless-live.py --studio). Here a second server on the shared engine.
            int port;
            using (var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
            }
            const string token = "s9-startup-option-token-0123456789";
            Assert.That(new AgentSettings().AllowAgents, Is.False, "agents aren't allowed in the saved settings");
            AgentServer.StartupOption = (port, token);
            AgentServer server = null;
            using var client = new System.Net.Http.HttpClient();
            try
            {
                server = new AgentServer(Studio.Host);
                Assert.That(server.Listening, Is.True, "the option allows agents");
                Assert.That((server.Url, server.Token, server.Allowed), Is.EqualTo(($"http://127.0.0.1:{port}/mcp", token, true)));
                Assert.That(server.ConnectCommand, Does.Contain(token));
                Assert.That(new AgentSettings().AllowAgents, Is.False, "and saves nothing");

                int Status(string bearer)
                {
                    var r = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Post, server.Url)
                    {
                        Content = new System.Net.Http.StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"s9\",\"version\":\"1\"}}}",
                            System.Text.Encoding.UTF8, "application/json"),
                    };
                    r.Headers.Accept.ParseAdd("application/json, text/event-stream");
                    r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
                    var task = client.SendAsync(r);
                    for (var until = DateTime.UtcNow.AddSeconds(30); !task.IsCompleted && DateTime.UtcNow < until;)
                    {
                        Dispatcher.UIThread.RunJobs();
                        System.Threading.Thread.Sleep(5);
                    }
                    Assert.That(task.IsCompleted, Is.True, "the server answered");
                    return (int)task.Result.StatusCode;
                }
                Assert.That(Status(token), Is.EqualTo(200), "the option's token connects");
                Assert.That(Status("wrong"), Is.EqualTo(401));

                // A choice on Preferences ▸ Agent ends the option: the saved settings rule again.
                server.Set(false);
                Assert.That(server.Listening, Is.False);
                Assert.That(server.Allowed, Is.False);
                Assert.That(server.Token, Is.Not.EqualTo(token), "the saved token again");
            }
            finally
            {
                AgentServer.StartupOption = null;
                server?.Dispose();
                Studio.Host.Agent.Set(false, AgentServer.DefaultPort.ToString());
            }
        }
    }
}
