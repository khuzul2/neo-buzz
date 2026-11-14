// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor in the studio, headless. Every
// curated portable machine (the ten Pedal ones) opens it on a double click instead of the parameter
// list (a screenshot each); its knobs show the agent server's values and take typed ones as param_set
// reads them, one undo step; envelopes get their picture, Pedal EQ and Pedal Filter a computed response,
// the others markers; a draft opens its parameter list until the per-gear setting says otherwise, and
// the machine menu has "Editor..." and that setting; the plain list stays a click away.

using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Agent;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    using Control = Avalonia.Controls.Control;

    public class ManifestEditorTests
    {
        /// <summary>The curated manifests (mcp-server.md §12.9) of machines the studio has: the ten Pedal ones, and since K2 batch a the other nine.</summary>
        public static readonly string[] CuratedPortable =
        {
            "Pedal Chorus", "Pedal Comp", "Pedal Drum Matrix", "Pedal EQ", "Pedal Filter", "Pedal Plate",
            "Pedal FM", "Pedal Juno106", "Pedal M1", "Pedal SH101",
            "Pedal Gate", "Pedal Gain", "Pedal Gain Multi", "Pedal Shaper", "Pedal MComp", "Pedal Tracker", "Pedal Chord", "Pedal LFO", "Pedal Peer Control",
        };

        static ReBuzzCore Core => Studio.Host.Core;

        static MachineCore Add(string gear, string name, float x = 0.3f, float y = 0.3f)
        {
            Core.SongCore.CreateMachine(gear, null, name, null, null, null, -1, x, y);
            var m = Core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);
            Assert.That(m, Is.Not.Null, $"{gear} loads");
            return m!;
        }

        static void Remove(params MachineCore[] machines)
        {
            foreach (var m in machines) Core.UiHost.ReleaseMachine(m);
            Core.SongCore.DeleteMachines(machines);
            Dispatcher.UIThread.RunJobs();
        }

        /// <summary>The machine opened as a double click opens it (StudioMachineUiHost.OpenGui), drawn once.</summary>
        static (Window Window, ManifestEditor Editor) Open(MachineCore m)
        {
            Core.UiHost.OpenGui(m, null);
            var window = StudioMachineUiHost.EditorWindowOf(m);
            Assert.That(window, Is.Not.Null, $"{m.DLL.Name}: its manifest editor opened");
            Assert.That(StudioMachineUiHost.ContentOf(window), Is.InstanceOf<ManifestEditor>());
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            window.CaptureRenderedFrame();
            return (window!, (ManifestEditor)StudioMachineUiHost.ContentOf(window)!);
        }

        static ReBuzz.Audio.WorkManager work;

        /// <summary>Runs the engine for a moment (no audio device here), then the UI's queue: parameter events reach the controls.</summary>
        static void Run(double seconds = 0.05)
        {
            work ??= new ReBuzz.Audio.WorkManager(Core, new ReBuzz.Audio.WorkThreadEngine(1), 0, Core.EngineSettings);
            var buffer = new float[512];
            int blocks = (int)System.Math.Ceiling(seconds * ReBuzzCore.masterInfo.SamplesPerSec / 256.0);
            lock (ReBuzzCore.AudioLock)
            {
                for (int i = 0; i < blocks; i++) work.MainAudioFillBuffer(buffer, 0, 512);
            }
            Dispatcher.UIThread.RunJobs();
        }

        static string Shot(string gear) => "manifest-editor-" + gear.Replace("Pedal ", "pedal-").Replace(" ", "-").ToLowerInvariant();

        static IEnumerable<Control> All(Window window, string type) =>
            window.GetVisualDescendants().OfType<Control>().Where(c => c.GetType().Name == type);

        static Point Centre(Control c, Visual top) => c.TranslatePoint(new Point(c.Bounds.Width / 2, c.Bounds.Height / 2), top)!.Value;

        [AvaloniaTest]
        public void EveryCuratedPortableMachineOpensItsEditorWithTheServersValues()
        {
            Assert.Multiple(() =>
            {
                foreach (var gear in CuratedPortable)
                {
                    var m = Add(gear, "Face " + gear);
                    try
                    {
                        Assert.That(ManifestEditorSettings.OpensEditor(m.DLL), Is.True, $"{gear}: curated, so on by default");
                        var (window, editor) = Open(m);
                        Assert.That(StudioMachineUiHost.ParameterWindowOf(m), Is.Null, $"{gear}: the editor instead of the parameter list");
                        Studio.Screenshot(window, Shot(gear));
                        var face = editor.Face;
                        Assert.That(face.Quality, Is.EqualTo("curated"));
                        Assert.That(All(window, "TextBlock").Select(t => ((TextBlock)t).Text ?? ""), Has.None.StartsWith("Editor error"), $"{gear}: the body built");
                        Assert.That(All(window, "Section").Count(), Is.EqualTo(face.Sections.Count + (face.Response != null ? 1 : 0)), $"{gear}: a section per group");
                        Assert.That(All(window, "PresetBar").Count(), Is.EqualTo(1), $"{gear}: the preset bar");
                        // Every parameter the server shows has its control, its value as the server says it.
                        var server = SongModel.Params(m).Where(p => (p.Parameter.Flags & ParameterFlags.State) != 0).ToList();
                        Assert.That(face.Params.Select(p => p.Name), Is.EqualTo(server.Select(p => p.Name)), $"{gear}: the server's parameters, in order");
                        foreach (var (ap, p) in server.Zip(face.Params))
                        {
                            Assert.That(editor.Controls.ContainsKey(p), Is.True, $"{gear}: {p.Label} has a control");
                            int raw = ap.Parameter.GetValue(0);
                            string text = ap.Info.Describe(raw);
                            if (editor.Controls[p] is Knob knob) Assert.That(knob.ValueText, Is.EqualTo(text), $"{gear}: {p.Label} reads as param_get does");
                            if (ap.Manifest is { PlainName.Length: > 0 } e) Assert.That(p.Label, Is.EqualTo(e.PlainName), $"{gear}: the curated plain name");
                        }
                        window.Close();
                    }
                    finally
                    {
                        Remove(m);
                    }
                }
            });
        }

        [Test]
        public void TheCuratedEditorWinsADoubleClickOnlyWhereThereIsNoGuiOfItsOwn()
        {
            Assert.That(ManifestEditorSettings.DefaultFor(curated: true, hasOwnGui: false), Is.True, "curated, no GUI: the manifest editor");
            Assert.That(ManifestEditorSettings.DefaultFor(curated: true, hasOwnGui: true), Is.False, "curated with a GUI (Pedal LFO, Chord, Peer Control, Tracker): its own GUI");
            Assert.That(ManifestEditorSettings.DefaultFor(curated: false, hasOwnGui: false), Is.False, "not curated: the parameter list");
        }

        [AvaloniaTest]
        public void ADoubleClickInTheMachineViewOpensTheEditorAndTheMenuKeepsTheList()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var bass = (MachineCore)Add("Pedal SH101", "Bass", -0.5f, 0);
                Settle();
                Assert.That(ManifestEditorSettings.HasOwnGui(bass.DLL), Is.False, "SH101 has no GUI of its own in the studio");
                DoubleClick(window, view.PointOf(bass, window));
                var editorWindow = StudioMachineUiHost.EditorWindowOf(bass);
                Assert.That(editorWindow?.IsVisible, Is.True, "a double click opens SH101's manifest editor");
                Assert.That(StudioMachineUiHost.ParameterWindowOf(bass), Is.Null, "not its parameter list");
                // The plain list: on the machine's menu, and the editor's button.
                Menu(window, view, view.PointOf(bass, window), "manifest-editor-menu", "Parameters...");
                Assert.That(StudioMachineUiHost.ParameterWindowOf(bass)?.IsVisible, Is.True, "Parameters... opens the plain list");
                StudioMachineUiHost.ParameterWindowOf(bass)!.Close();
                Settle();
                var editor = (ManifestEditor)StudioMachineUiHost.ContentOf(editorWindow)!;
                editorWindow.UpdateLayout();
                var list = All(editorWindow, "ActionButton").Single(b => AutomationProperties.GetName(b) == "Parameter list");
                Click(editorWindow, Centre(list, editorWindow));
                Assert.That(StudioMachineUiHost.ParameterWindowOf(bass)?.IsVisible, Is.True, "the editor's Parameter list button opens it too");
                StudioMachineUiHost.ParameterWindowOf(bass)!.Close();
                editorWindow.Close();
                Settle();
                // "Editor..." from the menu.
                Menu(window, view, view.PointOf(bass, window), null, "Editor...");
                Assert.That(StudioMachineUiHost.EditorWindowOf(bass)?.IsVisible, Is.True, "Editor... opens it");
                StudioMachineUiHost.EditorWindowOf(bass)!.Close();
                Settle();
            }
            finally
            {
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ADraftOpensItsListUntilTheSettingIsTurnedOn()
        {
            var (window, view) = OpenEmpty();
            // U6 (mcp-server.md §12.13, §12.15), K2 batch a: every Pedal machine's manifest is curated now,
            // so Pedal Tracker's is made a draft (its manifest as read, marked a draft) for this test.
            var gain = (MachineCore)Add("Pedal Tracker", "Trim", 0, 0);
            var cache = (ConcurrentDictionary<string, MachineManifest>)typeof(SongModel)
                .GetField("manifests", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
            var real = SongModel.Manifest(gain.DLL)!;
            string text = File.ReadAllText(MachineManifest.PathFor(gain.DLL.Path, gain.DLL.Name));
            Assert.That(text, Does.Contain("\"status\": \"curated\""));
            cache[gain.DLL.Path] = MachineManifest.Parse(text.Replace("\"status\": \"curated\"", "\"status\": \"draft\""));
            try
            {
                Settle();
                Assert.That(SongModel.Manifest(gain.DLL)!.IsDraft, Is.True, "Pedal Tracker's manifest is a draft");
                Assert.That(ManifestEditorSettings.OpensEditor(gain.DLL), Is.False, "off by default for drafts");
                DoubleClick(window, view.PointOf(gain, window));
                // P3e: the list is the device view in the Detail panel, not the classic parameter window.
                Assert.That(window.Detail.IsOpen && window.Detail.IsDevice, Is.True, "a double click opens the device view");
                Assert.That(((ReBuzz.Studio.Views.Device.DeviceDock)window.Detail.DevicePage).Page?.Machine, Is.SameAs(gain));
                Assert.That(StudioMachineUiHost.ParameterWindowOf(gain), Is.Null);
                Assert.That(StudioMachineUiHost.EditorWindowOf(gain), Is.Null);
                window.Detail.Close();
                Settle();

                // The setting, from the machine's menu: a check item.
                Click(window, view.PointOf(gain, window), MouseButton.Right);
                var item = view.Canvas.OpenMenu!.Items.OfType<MenuItem>().Single(x => x.Header as string == "Double-click Opens Editor");
                Assert.That(item.IsChecked, Is.False);
                ClickItem(item);
                Assert.That(ManifestEditorSettings.OpensEditor(gain.DLL), Is.True, "the menu turned it on, for the gear");
                DoubleClick(window, view.PointOf(gain, window));
                var editorWindow = StudioMachineUiHost.EditorWindowOf(gain);
                Assert.That(editorWindow?.IsVisible, Is.True, "now a double click opens the editor");
                var editor = (ManifestEditor)StudioMachineUiHost.ContentOf(editorWindow)!;
                Assert.That(editor.Face.Quality, Is.EqualTo("draft"));
                Studio.Screenshot(editorWindow, "manifest-editor-draft-pedal-tracker");
                editorWindow.Close();
                Settle();
            }
            finally
            {
                cache[gain.DLL.Path] = real;
                ManifestEditorSettings.Set(gain.DLL, true); // the curated default, so later tests see the gear as it ships
                Close(window);
            }
        }

        [AvaloniaTest]
        public void ANeoMachineKeepsItsOwnEditor()
        {
            // Neo Delay, as every Neo machine: an editor of its own, a manifest generated from its Params.def (neither
            // curated nor a draft), so the setting is off; and even turned on, a double click opens its own editor, as
            // it does for any machine with a GUI. Editor... still makes the manifest one on request.
            var (window, view) = OpenEmpty();
            var delay = (MachineCore)Add("Neo Delay", "Echo", 0, 0);
            try
            {
                Settle();
                var manifest = SongModel.Manifest(delay.DLL)!;
                Assert.That(manifest.IsCurated || manifest.IsDraft, Is.False, "Neo Delay's manifest is generated");
                Assert.That(ManifestEditorSettings.OpensEditor(delay.DLL), Is.False, "off by default: not curated");
                ManifestEditorSettings.Set(delay.DLL, true);
                DoubleClick(window, view.PointOf(delay, window));
                var gui = StudioMachineUiHost.GuiWindowOf(delay);
                Assert.That(gui?.IsVisible, Is.True, "a double click opens its own editor");
                gui!.UpdateLayout();
                Assert.That(All(gui, "NeoDelayEditor").Count(), Is.EqualTo(1), "Neo Delay's editor");
                Assert.That(StudioMachineUiHost.EditorWindowOf(delay), Is.Null, "not the manifest editor, even with the setting on");
                Assert.That(StudioMachineUiHost.ParameterWindowOf(delay), Is.Null, "nor its parameter list");
                gui.Close();
                Settle();
                Menu(window, view, view.PointOf(delay, window), null, "Editor...");
                Assert.That((StudioMachineUiHost.ContentOf(StudioMachineUiHost.EditorWindowOf(delay)) as ManifestEditor)?.Face.Quality, Is.EqualTo("generated"),
                    "Editor... makes its manifest editor on request");
                StudioMachineUiHost.EditorWindowOf(delay)!.Close();
                Settle();
            }
            finally
            {
                ManifestEditorSettings.Set(delay.DLL, false);
                Close(window);
            }
        }

        [AvaloniaTest]
        public void AKnobTakesATypedValueAsParamSetReadsItInOneUndoStep()
        {
            var m = Add("Pedal Juno106", "Typed Juno");
            try
            {
                var (window, editor) = Open(m);
                var cutoff = editor.Face.Params.Single(p => p.Role == "filter.cutoff");
                var knob = (Knob)editor.Controls[cutoff];
                var ap = SongModel.Params(m).Single(p => p.Name == cutoff.Name);
                int before = ap.Parameter.GetValue(0);
                int steps = Core.SongCore.ActionStack.Actions.Count();

                // A right click on the knob puts it in the value box, focused; type, then Enter.
                Click(window, Centre(knob, window), MouseButton.Right);
                Assert.That(editor.Selected, Is.SameAs(cutoff));
                Assert.That(editor.Entry.Text, Is.EqualTo(ap.Info.Describe(before)));
                editor.Entry.SelectAll();
                window.KeyTextInput("800 Hz");
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Dispatcher.UIThread.RunJobs();
                int expected = ValueResolver.Resolve(ap.Info, System.Text.Json.Nodes.JsonValue.Create("800 Hz"), before);
                Assert.That(ap.Parameter.GetValue(0), Is.EqualTo(expected), "the raw value param_set gives 800 Hz");
                Assert.That(knob.ValueText, Is.EqualTo(ap.Info.Describe(expected)).And.Contain("Hz"));
                Assert.That(Core.SongCore.ActionStack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
                Studio.Screenshot(window, "manifest-editor-typed-value");

                // What param_set refuses, the box refuses, with its reason; nothing changes.
                Assert.That(editor.Apply("loud"), Is.False);
                Assert.That(editor.Message, Does.Contain("Filter cutoff").Or.Contain("VCF Freq"));
                Assert.That(ap.Parameter.GetValue(0), Is.EqualTo(expected));

                Core.SongCore.ActionStack.Undo();
                Run();
                Assert.That(ap.Parameter.GetValue(0), Is.EqualTo(before), "undo restores it");
                Assert.That(knob.ValueText, Is.EqualTo(ap.Info.Describe(before)), "and the knob follows");

                // A drag is one undo step too (its press a first click, HeadlessInput.cs).
                var c = Centre(knob, window);
                window.MouseDown(c, MouseButton.Left);
                for (int dy = 8; dy <= 40; dy += 8) window.MouseMove(c - new Point(0, dy), RawInputModifiers.LeftMouseButton);
                window.MouseUp(c - new Point(0, 40), MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.That(ap.Parameter.GetValue(0), Is.GreaterThan(before), "dragged up, brighter");
                Assert.That(editor.Selected, Is.SameAs(cutoff), "the value box follows the knob turned");
                Core.SongCore.ActionStack.Undo();
                Run();
                Assert.That(ap.Parameter.GetValue(0), Is.EqualTo(before), "one undo step takes the whole drag back");
                window.Close();
            }
            finally
            {
                Remove(m);
            }
        }

        [AvaloniaTest]
        public void LongLabelsTakeTwoLinesAndNotesForDevelopersStayBehindAnInfoMark()
        {
            // UI gallery review: long labels were cut with "…" (mac-460 "Pulse width / PWM d…", win-901, win-903, win-909),
            // and notes for whoever checks the manifest stood in the editor ("Curated manifest: names, units and roles
            // checked…", "markers only … no known design to compute it from"). Labels show whole (a knob's on two lines);
            // the notes are an ⓘ's tooltip.
            var sh = Add("Pedal SH101", "Label SH");
            var eq = Add("Pedal EQ", "Label EQ", 0.5f, 0.3f);
            try
            {
                var (w, editor) = Open(sh);
                Studio.Screenshot(w, "manifest-editor-pedal-sh101-labels");
                var texts = All(w, "TextBlock").Select(t => ((TextBlock)t).Text ?? "").ToList();
                Assert.That(texts, Has.None.Contains("Curated manifest").And.None.Contains("Markers only").And.None.Contains("Computed from"), "no notes for developers on show");
                var about = All(w, "TextBlock").Where(t => AutomationProperties.GetName(t) == "About").ToList();
                Assert.That(about, Has.Count.EqualTo(1), "an ⓘ by the summary");
                Assert.That(ToolTip.GetTip(about[0]) as string, Does.StartWith("Curated manifest"), "the manifest's provenance in its tooltip");
                var knobs = editor.Controls.Values.OfType<Knob>().ToList();
                Assert.That(knobs.Select(k => k.Label), Has.None.Contains("…"), "no label cut");
                var pwm = knobs.Single(k => k.Label == "Pulse width / PWM depth");
                Assert.That(pwm.LabelLines, Has.Length.EqualTo(2), "a long label on two lines");
                Assert.That(pwm.Bounds.Width, Is.LessThanOrEqualTo(ManifestEditor.KnobLabelWidth + 6.5), "and the knob keeps its width");
                Assert.That(pwm.Bounds.Height, Is.EqualTo(pwm.Diameter + 34 + KnobModel.LineHeight).Within(0.5));
                w.Close();

                var (w2, eqEditor) = Open(eq);
                string caption = eqEditor.Face.Response!.Caption;
                Assert.That(caption, Does.StartWith("Computed from"));
                Assert.That(All(w2, "TextBlock").Select(t => ((TextBlock)t).Text ?? ""), Has.None.Contains("Computed from"), "the plot's caption isn't on show");
                var plot = All(w2, "GraphView").Single(g => AutomationProperties.GetName(g) == "Response");
                Assert.That(ToolTip.GetTip(plot), Is.EqualTo(caption), "the plot's tooltip says how it is made");
                Assert.That(All(w2, "TextBlock").Count(t => AutomationProperties.GetName(t) == "About" && ToolTip.GetTip(t) as string == caption), Is.EqualTo(1), "and an ⓘ under it");
                w2.Close();
            }
            finally
            {
                Remove(sh, eq);
            }
        }

        [AvaloniaTest]
        public void EnvelopesGetPicturesAndOnlyKnownDesignsGetACurve()
        {
            var juno = Add("Pedal Juno106", "Env Juno");
            var eq = Add("Pedal EQ", "Plot EQ", 0.5f, 0.2f);
            var filter = Add("Pedal Filter", "Plot Filter", 0.5f, 0.5f);
            var sh = Add("Pedal SH101", "Plot SH", 0.1f, 0.6f);
            var chorus = Add("Pedal Chorus", "Plot Chorus", 0.6f, 0.7f);
            try
            {
                var (w1, junoEditor) = Open(juno);
                Assert.That(junoEditor.Face.Sections.SelectMany(s => s.Envelopes).Select(e => e.Title), Is.EqualTo(new[] { "Envelope" }));
                Assert.That(All(w1, "GraphView").Count(g => AutomationProperties.GetName(g) == "Envelope"), Is.EqualTo(1), "Juno106's ADSR picture");
                Assert.That(junoEditor.Face.Response, Is.Null, "Juno106's filter: no plot");
                w1.Close();

                var (w2, eqEditor) = Open(eq);
                Assert.That(eqEditor.Face.Response?.Computed, Is.True, "Pedal EQ: computed from its design");
                // A +12 dB low-mid band: the curve passes through +12 dB at its frequency.
                var lmGain = eqEditor.Face.Params.Single(p => p.Role == "eq.lowmid.gain");
                var lmFreq = eqEditor.Face.Params.Single(p => p.Role == "eq.lowmid.freq");
                Assert.That(eqEditor.Apply(null), Is.False, "nothing selected");
                eqEditor.Select(lmGain, focus: false);
                Assert.That(eqEditor.Apply("+12 dB"), Is.True);
                double hz = lmFreq.Value(SongModel.Params(eq).Single(p => p.Name == lmFreq.Name).Parameter.GetValue(0));
                Assert.That(eqEditor.Face.Response!.DbAt!(hz), Is.EqualTo(12).Within(0.3));
                w2.UpdateLayout();
                Studio.Screenshot(w2, "manifest-editor-pedal-eq-boost");
                w2.Close();

                var (w3, filterEditor) = Open(filter);
                Assert.That(filterEditor.Face.Response?.Computed, Is.True, "Pedal Filter: computed from its design");
                Assert.That(filterEditor.Face.Response!.DbAt!(20), Is.EqualTo(0).Within(1), "its default low pass passes the lows");
                w3.Close();

                var (w4, shEditor) = Open(sh);
                Assert.That(shEditor.Face.Response, Is.Null, "a synth: its filter moves with every note, so no plot (markers are for effects)");
                w4.Close();

                var (w5, chorusEditor) = Open(chorus);
                Assert.That(chorusEditor.Face.Response, Is.Null, "a chorus: no filter or EQ frequencies, no plot");
                w5.Close();
            }
            finally
            {
                Remove(juno, eq, filter, sh, chorus);
            }
        }
    }
}
