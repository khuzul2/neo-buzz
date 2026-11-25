// Fork (docs/specs/drag-drop.md, DD1): drag and drop into the Arrangement, the Session View and the Machine View: every row of the brief's rules
// table in both views (what a generator, an instrument plug-in, an effect, audio files, a pattern, a MIDI file and a preset do on empty space and
// on a track), each as one undo step with redo, kept by a save and a reload, the drag's feedback (the lit track, the line for a new track, the
// snap, the refusal and its reason), the files from the desktop and the agent's drop_item.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent;
using ReBuzz.Agent.Browser;
using ReBuzz.Core;
using ReBuzz.Core.Drop;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Views.Browser;
using ReBuzz.Studio.Views.Drop;
using ReBuzz.Studio.Views.Sequence;
using ReBuzz.Studio.Views.Session;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class DragDropTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        // ------------------------------------------------------------------ what is dragged

        static BrowserDragData Gear(string name) => new(BrowserKind.Machine, name);
        static BrowserDragData Audio(string path) => new(BrowserKind.Audio, path);
        static BrowserDragData Midi(string path) => new(BrowserKind.Midi, path);
        static BrowserDragData Pat(string machine, string pattern) => new(BrowserKind.Pattern, machine + "/" + pattern);
        static IReadOnlyList<BrowserDragData> One(BrowserDragData d) => new[] { d };

        static string Work(string name)
        {
            string dir = NeoBuzz.TestSupport.TestTemp.Path($"dd1-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        /// <summary>A WAV of a sine (16-bit mono).</summary>
        static string Sine(string name, double seconds = 1, double hz = 440, int rate = 44100)
        {
            string path = Work(name);
            int frames = (int)(seconds * rate);
            using var w = new BinaryWriter(File.Create(path));
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + frames * 2); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(frames * 2);
            for (int i = 0; i < frames; i++) w.Write((short)(Math.Sin(2 * Math.PI * hz * i / rate) * 20000));
            return path;
        }

        /// <summary>A format 0 MIDI file at 96 per quarter: 120 BPM, 3/4, and a note each for the given pitches, a beat long, one after another.</summary>
        static string MidiFile(string name, params int[] pitches)
        {
            var track = new List<byte> { 0, 0xFF, 0x51, 3, 0x07, 0xA1, 0x20, 0, 0xFF, 0x58, 4, 3, 2, 24, 8 };
            foreach (int p in pitches)
            {
                track.AddRange(new byte[] { 0, 0x90, (byte)p, 100 });
                track.AddRange(new byte[] { 96, 0x80, (byte)p, 0 });
            }
            track.AddRange(new byte[] { 0, 0xFF, 0x2F, 0 });
            var bytes = new List<byte>();
            bytes.AddRange(Encoding.ASCII.GetBytes("MThd"));
            bytes.AddRange(new byte[] { 0, 0, 0, 6, 0, 0, 0, 1, 0, 96 });
            bytes.AddRange(Encoding.ASCII.GetBytes("MTrk"));
            bytes.AddRange(new[] { (byte)(track.Count >> 24), (byte)(track.Count >> 16), (byte)(track.Count >> 8), (byte)track.Count });
            bytes.AddRange(track);
            string path = Work(name);
            File.WriteAllBytes(path, bytes.ToArray());
            return path;
        }

        static IReadOnlyList<BrowserDragData> Files(params string[] paths) => paths.Select(p => p.EndsWith(".mid") ? Midi(p) : Audio(p)).ToList();

        // ------------------------------------------------------------------ the song

        static IMachine AddTrack(string dll, string name, bool connect = true, bool sequence = true)
        {
            var song = Core.SongCore;
            song.CreateMachine(dll, null, name, null, null, null, -1, -0.5f + 0.12f * song.Machines.Count, 0.35f);
            var machine = song.Machines.Last(x => x.Name == name);
            var master = song.Machines.First(x => x.DLL.Info.Type == MachineType.Master);
            if (connect) song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            if (sequence && !song.Sequences.Any(s => s.Machine == machine)) song.AddSequence(machine, song.Sequences.Count);
            return machine;
        }

        /// <summary>Everything a drop can change, as text: the graph, the sequences' events, the machines' patterns, the Session's slots and the audio store.</summary>
        static string State()
        {
            var sb = new StringBuilder(Graph());
            sb.Append(" | ");
            foreach (var s in Core.SongCore.Sequences)
            {
                sb.Append(s.Machine.Name).Append(':');
                foreach (var (time, e) in s.Events) sb.Append(time).Append('=').Append(e.Pattern?.Name).Append(',');
                sb.Append(';');
            }
            sb.Append(" | ");
            foreach (var m in Core.SongCore.Machines) sb.Append(m.Name).Append('[').Append(string.Join(",", m.Patterns.Select(p => p.Name + "/" + p.Length))).Append("];");
            sb.Append(" | ");
            if (Core.SessionLayout is { Document: not null } layout)
                for (int t = 0; t < layout.TrackCount; t++)
                    for (int s = 0; s < layout.SceneCount; s++)
                        if (layout.PatternAt(t, s) is { } p) sb.Append(t).Append(',').Append(s).Append('=').Append(p.Name).Append(';');
            return sb.ToString();
        }

        static (string Before, string After) UndoRedo(Action<string> afterUndo = null)
        {
            string after = State();
            Core.SongCore.ActionStack.Undo();
            Settle();
            afterUndo?.Invoke(State());
            string undone = State();
            Core.SongCore.ActionStack.Redo();
            Settle();
            Assert.That(State(), Is.EqualTo(after), "redo does the drop again");
            return (undone, after);
        }

        /// <summary>One drop is one undo step: undo gives the song back as it was, redo does it again.</summary>
        /// <summary>The picture of a window; also into GALLERY_DIR when the gallery run sets it (the CI publishes that folder).</summary>
        static void Shot(Avalonia.Controls.TopLevel window, string name)
        {
            string path = Studio.Screenshot(window, name);
            if (System.Environment.GetEnvironmentVariable("GALLERY_DIR") is { Length: > 0 } dir)
            {
                Directory.CreateDirectory(dir);
                File.Copy(path, Path.Combine(dir, name + ".png"), true);
            }
        }

        static void AssertOneStep(string before)
        {
            var (undone, after) = UndoRedo();
            Assert.That(undone, Is.EqualTo(before), "one undo step puts everything back");
            Assert.That(after, Is.Not.EqualTo(before));
        }

        // ------------------------------------------------------------------ the two surfaces

        abstract class Surface : IDisposable
        {
            public abstract string Name { get; }
            public abstract DropResult OnEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0);
            public abstract DropResult OnTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0);
            public abstract DropPlan PlanEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0);
            public abstract DropPlan PlanTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0);

            /// <summary>The clips of a track: where each is (the time in ticks; the scene) and its pattern.</summary>
            public abstract List<(int Pos, string Pattern)> Clips(IMachine track);

            /// <summary>A drop on empty space puts the first pattern of a new instrument track at the drop time (the arrangement); the session's slots stay empty.</summary>
            public abstract bool NewTrackHasAClip { get; }
            public abstract void Dispose();
        }

        sealed class ArrangementSurface : Surface
        {
            public const int Bar = 16;
            readonly MainWindow window;
            readonly SequenceView view;
            readonly bool looping;

            public ArrangementSurface()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                looping = Core.Looping;
                Core.ExecuteCommand(BuzzCommand.Stop);
                Core.Modified = false;
                Core.ExecuteCommand(BuzzCommand.NewFile);
                Settle();
                window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                ToolTip.SetServiceEnabled(window, false);
                window.Show();
                var tab = window.Tabs.Items.Cast<TabItem>().Single(t => (string)t.Header == "Arrangement");
                tab.IsSelected = true;
                Settle();
                view = (SequenceView)tab.Content!;
                var p = view.ArrangementModeButton.TranslatePoint(new Point(view.ArrangementModeButton.Bounds.Width / 2, view.ArrangementModeButton.Bounds.Height / 2), window)!.Value;
                window.MouseMove(p); window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left);
                Settle();
                Assert.That(view.IsArrangementMode, Is.True);
            }

            public ArrangementCanvas Canvas => view.Arrangement;
            public MainWindow Window => window;
            public override string Name => "the Arrangement";
            public override bool NewTrackHasAClip => true;

            Point Track(IMachine m, int pos)
            {
                Settle();
                var row = Canvas.Rows.FirstOrDefault(r => r.Machine == m && r.Kind == ArrangementRowKind.Lane) ?? throw new InvalidOperationException("no row for " + m.Name + "/" + m.GetHashCode() + " in " + string.Join(", ", Canvas.Rows.Select(r => r.Kind + ":" + r.Machine?.Name + "/" + r.Machine?.GetHashCode())) + " seqs " + string.Join(",", Core.SongCore.Sequences.Select(q => q.Machine.Name + "/" + q.Machine.GetHashCode())));
                return new Point(Canvas.Geometry.XOf(pos * Bar) + 0.5 * Canvas.Geometry.TickWidth, Canvas.YOf(row) + row.Height / 2);
            }

            Point Empty(int pos)
            {
                Settle();
                var last = Canvas.Rows[^1];
                return new Point(Canvas.Geometry.XOf(pos * Bar) + 0.5 * Canvas.Geometry.TickWidth, Canvas.YOf(last) + last.Height + 12);
            }

            public override DropResult OnEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0) { var r = Canvas.DropAt(items, Empty(pos)); Settle(); return r; }
            public override DropResult OnTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0) { var r = Canvas.DropAt(items, Track(track, pos)); Settle(); return r; }
            public override DropPlan PlanEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0) => Canvas.PlanAt(items, Empty(pos));
            public override DropPlan PlanTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0) => Canvas.PlanAt(items, Track(track, pos));

            public override List<(int Pos, string Pattern)> Clips(IMachine track) =>
                Core.SongCore.Sequences.Where(s => s.Machine == track).SelectMany(s => Canvas.Editor.Read(s)).OrderBy(i => i.Time).Select(i => (i.Time, i.Pattern.Name)).ToList();

            public override void Dispose()
            {
                Core.ExecuteCommand(BuzzCommand.Stop);
                window.Close();
                Settle();
                Dialogs.Responder = null;
                Core.Looping = looping;
            }
        }

        sealed class SessionSurface : Surface
        {
            readonly SessionRig rig = new();
            SessionView view;

            public SessionSurface() => view = rig.Show();

            public SessionRig Rig => rig;
            public SessionView View => view;
            public override string Name => "the Session";
            public override bool NewTrackHasAClip => false;

            Point Track(IMachine m, int pos)
            {
                rig.Settle();
                view.Rebuild();
                rig.Settle();
                int t = rig.Layout.FirstTrackOf(m);
                return view.DropPointOf(view.SlotAt(t, pos));
            }

            Point Empty() { rig.Settle(); view.Rebuild(); rig.Settle(); return view.DropPointOnEmptySpace(); }

            public override DropResult OnEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0) { var r = view.DropAt(items, Empty()); rig.Settle(); return r; }
            public override DropResult OnTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0) { var r = view.DropAt(items, Track(track, pos)); rig.Settle(); return r; }
            public override DropPlan PlanEmpty(IReadOnlyList<BrowserDragData> items, int pos = 0) => view.PlanAt(items, Empty());
            public override DropPlan PlanTrack(IMachine track, IReadOnlyList<BrowserDragData> items, int pos = 0) => view.PlanAt(items, Track(track, pos));

            public override List<(int Pos, string Pattern)> Clips(IMachine track)
            {
                var layout = rig.Layout;
                int t = layout.FirstTrackOf(track);
                var list = new List<(int, string)>();
                for (int s = 0; t >= 0 && s < layout.SceneCount; s++)
                    if (layout.PatternAt(t, s) is { } p) list.Add((s, p.Name));
                return list;
            }

            public override void Dispose() => rig.Dispose();
        }

        static Surface Open(string which) => which == "arrangement" ? new ArrangementSurface() : new SessionSurface();

        // ------------------------------------------------------------------ generators

        void GeneratorRows(string which)
        {
            using var s = Open(which);
            using var restore = Studio.AfterTest(() => DropSupport.LastInstrument = null);
            Settle();
            string before = State();

            // On empty space: a new machine and track, wired to Master; the arrangement's first clip is an empty pattern at the drop time.
            var made = s.OnEmpty(One(Gear("Neo Poly")), 2);
            Assert.That(made.Ok, Is.True, made.Message);
            Assert.That(made.Action, Is.EqualTo(DropAction.NewTrack));
            var lead = made.Machine;
            Assert.That(lead.DLL.Name, Is.EqualTo("Neo Poly"));
            Assert.That(lead.Outputs.Select(c => c.Destination.Name), Is.EqualTo(new[] { "Master" }), "wired to Master");
            Assert.That(Core.SongCore.Sequences.Any(q => q.Machine == lead), Is.True, "with a track");
            if (s.NewTrackHasAClip)
            {
                Assert.That(s.Clips(lead), Is.EqualTo(new[] { (2 * ArrangementSurface.Bar, "00") }), "a clip of the empty pattern at the drop time");
                Assert.That(lead.Patterns.Single().Length, Is.GreaterThan(0));
            }
            AssertOneStep(before);

            // On an existing track: its generator is replaced, the wires and the patterns kept.
            var track = AddTrack("Neo Poly", "Lead");
            track.CreatePattern("Verse", 32);
            Settle();
            string beforeSwap = State();
            var swapped = s.OnTrack(track, One(Gear("Neo Drums")));
            Assert.That(swapped.Ok, Is.True, swapped.Message);
            Assert.That(swapped.Action, Is.EqualTo(DropAction.Replace));
            var now = Machine("Lead");
            Assert.That(now.DLL.Name, Is.EqualTo("Neo Drums"), "the track plays the new generator");
            Assert.That(now.Outputs.Select(c => c.Destination.Name), Is.EqualTo(new[] { "Master" }), "connections kept");
            Assert.That(now.Patterns.Select(p => p.Name + "/" + p.Length), Does.Contain("Verse/32"), "patterns kept where they fit");
            Assert.That(Core.SongCore.Sequences.Count(q => q.Machine == now), Is.EqualTo(1), "and its track");
            AssertOneStep(beforeSwap);

            // The same gear again says so; an audio track isn't a generator's.
            Assert.That(s.PlanTrack(Machine("Lead"), One(Gear("Neo Drums"))).Refusal, Does.Contain("already"));
            var audio = Core.SessionLayout.NewAudioTrack("Loops");
            Settle();
            var plan = s.PlanTrack(audio, One(Gear("Neo Poly")));
            Assert.That(plan.Allowed, Is.False);
            Assert.That(plan.Refusal, Does.Contain("audio track"), "an audio track keeps its audio: a new track is on empty space");
        }

        [AvaloniaTest] public void AGeneratorIsANewTrackOrReplacesTheTracksGeneratorInTheArrangement() => GeneratorRows("arrangement");
        [AvaloniaTest] public void AGeneratorIsANewTrackOrReplacesTheTracksGeneratorInTheSession() => GeneratorRows("session");

        [AvaloniaTest]
        public void AnInstrumentPlugInIsANeoPlugWithItLoadedOnANewTrack()
        {
            string synth = Core.Instruments.FirstOrDefault(i => i.MachineDLL?.Name == "Neo Plug" && i.Name != null && i.Name.Contains("NeoPlug Test Synth"))?.Name;
            if (synth == null) Assert.Ignore("the plug-in host's library or test plug-ins aren't here");
            using var s = new ArrangementSurface();
            string before = State();
            var made = s.OnEmpty(One(new BrowserDragData(BrowserKind.Plugin, GearNames.Display(Core.Instruments.First(i => i.Name == synth)))), 1);
            Assert.That(made.Ok, Is.True, made.Message);
            Assert.That(made.Machine.DLL.Name, Is.EqualTo("Neo Plug"));
            Assert.That(((MachineCore)made.Machine).InstrumentName, Is.EqualTo(synth), "the plug-in is loaded in it");
            Assert.That(s.Clips(made.Machine), Is.EqualTo(new[] { (ArrangementSurface.Bar, "00") }));
            AssertOneStep(before);
        }

        // ------------------------------------------------------------------ effects

        void EffectRows(string which)
        {
            using var s = Open(which);
            using var restore = Studio.AfterTest(() => DropSupport.ChoiceResponder = null);
            var lead = AddTrack("Neo Poly", "Lead");
            Settle();

            // Refused on empty space, with the hint.
            var empty = s.PlanEmpty(One(Gear("Neo Comp")));
            Assert.That(empty.Allowed, Is.False);
            Assert.That(empty.Refusal, Does.Contain("Drop on a track"));
            string before = State();
            var refused = s.OnEmpty(One(Gear("Neo Comp")));
            Assert.That(refused.Ok, Is.False);
            Assert.That(State(), Is.EqualTo(before), "a refusal changes nothing");

            // On a track: between the track's last machine and Master; the next one after that.
            var first = s.OnTrack(lead, One(Gear("Neo Comp")));
            Assert.That(first.Ok, Is.True, first.Message);
            Assert.That(first.Action, Is.EqualTo(DropAction.InsertEffect));
            Assert.That(Graph(), Does.Contain("Lead→" + first.Machine.Name).And.Contain(first.Machine.Name + "→Master"));
            Assert.That(Graph(), Does.Not.Contain("Lead→Master"));
            Assert.That(first.Machine.DLL.Name, Is.EqualTo("Neo Comp"));
            var second = s.OnTrack(lead, One(Gear("Neo Eq")));
            Assert.That(second.Ok, Is.True, second.Message);
            Assert.That(Graph(), Does.Contain(first.Machine.Name + "→" + second.Machine.Name).And.Contain(second.Machine.Name + "→Master"), "after the last machine of the chain");
            Core.SongCore.ActionStack.Undo();
            Settle();
            Core.SongCore.ActionStack.Undo();
            Settle();
            Assert.That(Graph(), Does.Contain("Lead→Master"), "two drops, two undo steps");
            Assert.That(Core.SongCore.Machines.Count, Is.EqualTo(2));
            Core.SongCore.ActionStack.Redo(); Core.SongCore.ActionStack.Redo();
            Settle();

            // A track that branches asks which branch.
            var aux = AddTrack("Neo Poly", "Aux");
            var send = AddTrack("Neo Verb", "Send", sequence: false);
            Core.SongCore.ConnectMachines(aux, send, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            Settle();
            var plan = s.PlanTrack(aux, One(Gear("Neo Utility")));
            Assert.That(plan.Choices, Is.EquivalentTo(new[] { "Master", "Send" }), "the branches are named");
            string asked = null;
            DropSupport.ChoiceResponder = (title, choices) => { asked = title; return "Send"; };
            string beforeBranch = State();
            var branch = s.OnTrack(aux, One(Gear("Neo Utility")));
            Assert.That(branch.Ok, Is.True, branch.Message);
            Assert.That(asked, Does.Contain("branch"));
            Assert.That(Graph(), Does.Contain("Send→" + branch.Machine.Name).And.Contain(branch.Machine.Name + "→Master"), "after the chosen branch's last machine");
            Assert.That(Graph(), Does.Contain("Aux→Master"), "the other branch stays");
            AssertOneStep(beforeBranch);
            DropSupport.ChoiceResponder = (t, c) => null;
            string beforeCancel = State();
            Assert.That(s.OnTrack(aux, One(Gear("Neo Utility"))).Ok, Is.False, "cancelled");
            Assert.That(State(), Is.EqualTo(beforeCancel));
        }

        [AvaloniaTest] public void AnEffectNeedsATrackAndGoesAfterTheTracksLastMachineInTheArrangement() => EffectRows("arrangement");
        [AvaloniaTest] public void AnEffectNeedsATrackAndGoesAfterTheTracksLastMachineInTheSession() => EffectRows("session");

        // ------------------------------------------------------------------ audio

        void AudioRows(string which)
        {
            using var s = Open(which);
            Settle();
            string a = Sine("a.wav", 1), b = Sine("b.wav", 2, 330), c = Sine("c.wav", 1, 660);
            string before = State();

            // On empty space: a new Neo Audio track.
            var made = s.OnEmpty(Files(a), 1);
            Assert.That(made.Ok, Is.True, made.Message);
            Assert.That(made.Action, Is.EqualTo(DropAction.NewAudioTrack));
            var track = made.Machine;
            Assert.That(track.DLL.Name, Is.EqualTo("Neo Audio"));
            Assert.That(track.Outputs.Select(x => x.Destination.Name), Is.EqualTo(new[] { "Master" }));
            var clips = s.Clips(track);
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Pos, Is.EqualTo(which == "arrangement" ? ArrangementSurface.Bar : 0), "at the drop time, or in the first scene");
            AssertOneStep(before);
            // (the redo brought it back: the machine is the one the song has now)
            track = Core.SongCore.Machines.First(m => m.DLL.Name == "Neo Audio");

            // On an audio track: clips there, several one after another (arrangement) or down the scenes (session).
            string beforeMore = State();
            var more = s.OnTrack(track, Files(b, c), 3);
            Assert.That(more.Ok, Is.True, more.Message);
            Assert.That(more.Action, Is.EqualTo(DropAction.AudioClips));
            Assert.That(more.Machine, Is.SameAs(track));
            var all = s.Clips(track);
            Assert.That(all.Count, Is.EqualTo(3));
            if (which == "arrangement")
            {
                Assert.That(all[1].Pos, Is.EqualTo(3 * ArrangementSurface.Bar), "the first at the drop time");
                Assert.That(all[2].Pos, Is.EqualTo(all[1].Pos + more.Patterns[0].Length), "the next where the one before ends");
            }
            else Assert.That(all.Select(x => x.Pos), Is.EqualTo(new[] { 0, 3, 4 }), "down the scenes from the drop slot");
            AssertOneStep(beforeMore);

            // Any other track: a new audio track.
            var lead = AddTrack("Neo Poly", "Lead");
            Settle();
            int audioTracks = Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Audio");
            var other = s.OnTrack(lead, Files(a));
            Assert.That(other.Ok, Is.True, other.Message);
            Assert.That(other.Action, Is.EqualTo(DropAction.NewAudioTrack));
            Assert.That(Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Audio"), Is.EqualTo(audioTracks + 1), "the generator's track is untouched");
            Assert.That(lead.DLL.Name, Is.EqualTo("Neo Poly"));

            // What isn't audio or what isn't there is refused with its reason.
            Assert.That(s.PlanEmpty(Files(Path.Combine(Path.GetTempPath(), "dd1-nothing.wav"))).Refusal, Does.Contain("No file"));
            string text = Work("notes.txt");
            File.WriteAllText(text, "x");
            Assert.That(s.PlanEmpty(One(Audio(text))).Refusal, Does.Contain("WAV"));
        }

        [AvaloniaTest] public void AudioFilesAreNewAudioTracksOrClipsInTheArrangement() => AudioRows("arrangement");
        [AvaloniaTest] public void AudioFilesAreNewAudioTracksOrClipsInTheSession() => AudioRows("session");

        void SamplerRow(string which)
        {
            using var s = Open(which);
            var sampler = AddTrack("Neo Sampler", "Keys");
            Settle();
            string kick = Sine("kick.wav", 0.5, 60), snare = Sine("snare.wav", 0.5, 200);
            string before = State();
            int Wave(int zone) => sampler.AllNonInputParameters().First(p => p.Name == $"Z{zone} wave").GetValue(0);
            int used = Core.SongCore.Wavetable.Waves.Count(w => w != null);
            var plan = s.PlanTrack(sampler, Files(kick));
            Assert.That(plan.Action, Is.EqualTo(DropAction.LoadSample));
            var r = s.OnTrack(sampler, Files(kick, snare));
            Assert.That(r.Ok, Is.True, r.Message);
            Assert.That(Core.SongCore.Wavetable.Waves.Count(w => w != null), Is.EqualTo(used + 2), "both are waves of the song");
            Assert.That(Wave(1), Is.GreaterThan(0).And.LessThanOrEqualTo(200), "the first in zone 1");
            Assert.That(Wave(2), Is.GreaterThan(Wave(1)), "the next in zone 2");
            Assert.That(Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Audio"), Is.EqualTo(0), "no audio track is made");
            Core.SongCore.ActionStack.Undo();
            Settle();
            Assert.That(Core.SongCore.Wavetable.Waves.Count(w => w != null), Is.EqualTo(used), "both files come out in one undo step");
            Assert.That(State(), Is.EqualTo(before));
            Core.SongCore.ActionStack.Redo();
            Settle();
            Assert.That(Core.SongCore.Wavetable.Waves.Count(w => w != null), Is.EqualTo(used + 2), "and back in with a redo");
        }

        [AvaloniaTest] public void AudioOnASamplerLoadsTheSamplesInTheArrangement() => SamplerRow("arrangement");
        [AvaloniaTest] public void AudioOnASamplerLoadsTheSamplesInTheSession() => SamplerRow("session");

        // ------------------------------------------------------------------ patterns

        void PatternRows(string which)
        {
            using var s = Open(which);
            var lead = AddTrack("Neo Poly", "Lead");
            var bass = AddTrack("Neo Poly", "Bass");
            lead.CreatePattern("Verse", 32);
            Settle();
            string before = State();
            var ok = s.OnTrack(lead, One(Pat("Lead", "Verse")), 1);
            Assert.That(ok.Ok, Is.True, ok.Message);
            Assert.That(ok.Action, Is.EqualTo(DropAction.Clip));
            Assert.That(s.Clips(lead), Is.EqualTo(new[] { (which == "arrangement" ? ArrangementSurface.Bar : 1, "Verse") }));
            AssertOneStep(before);
            Assert.That(s.PlanTrack(bass, One(Pat("Lead", "Verse"))).Refusal, Does.Contain("belongs to Lead"), "on another machine's track");
            Assert.That(s.PlanEmpty(One(Pat("Lead", "Verse"))).Refusal, Does.Contain("Lead's track"), "nowhere on empty space");
        }

        [AvaloniaTest] public void APatternIsAClipOnItsOwnMachinesTrackInTheArrangement() => PatternRows("arrangement");
        [AvaloniaTest] public void APatternIsAClipOnItsOwnMachinesTrackInTheSession() => PatternRows("session");

        // ------------------------------------------------------------------ MIDI

        static List<(int Time, int Pitch)> NotesOf(IPattern p)
        {
            var input = MachineNotes.For((MachineCore)p.Machine);
            return MachineNotes.Read(p, input).Select(n => (n.Time / 240, n.Pitch)).OrderBy(n => n.Item1).ToList();
        }

        void MidiRows(string which)
        {
            using var s = Open(which);
            using var restore = Studio.AfterTest(() => DropSupport.LastInstrument = null);
            string file = MidiFile("tune.mid", 60, 64, 67);
            int tpb = Core.TPB;
            string before = State();

            // On empty space: a new track with the notes on Neo Poly.
            var made = s.OnEmpty(Files(file), 1);
            Assert.That(made.Ok, Is.True, made.Message);
            Assert.That(made.Action, Is.EqualTo(DropAction.MidiNewTrack));
            Assert.That(made.Machine.DLL.Name, Is.EqualTo("Neo Poly"), "the default instrument");
            var pattern = made.Patterns.Single();
            Assert.That(NotesOf(pattern).Select(n => n.Pitch), Is.EqualTo(new[] { 60, 64, 67 }));
            Assert.That(NotesOf(pattern).Select(n => n.Time), Is.EqualTo(new[] { 0, tpb, 2 * tpb }), "a beat apart, in the song's ticks (the file's tempo is not applied)");
            Assert.That(made.Message, Does.Contain("120").And.Contain("3/4"), "its tempo and meter are only reported");
            Assert.That(made.Machine.Patterns.Count, Is.EqualTo(1), "the notes' pattern is the track's only one");
            var clips = s.Clips(made.Machine);
            Assert.That(clips.Count, Is.EqualTo(1));
            Assert.That(clips[0].Pos, Is.EqualTo(which == "arrangement" ? ArrangementSurface.Bar : 0));
            AssertOneStep(before);

            // On a track: the notes in a new pattern at the drop time.
            var lead = AddTrack("Neo Poly", "Lead");
            lead.CreatePattern("Verse", 32);
            Settle();
            string beforeTrack = State();
            var on = s.OnTrack(lead, Files(file), 2);
            Assert.That(on.Ok, Is.True, on.Message);
            Assert.That(on.Action, Is.EqualTo(DropAction.MidiNewPattern));
            Assert.That(on.Machine, Is.SameAs(lead));
            Assert.That(lead.Patterns.Count, Is.EqualTo(2), "a new pattern beside the old");
            Assert.That(NotesOf(on.Patterns.Single()).Count, Is.EqualTo(3));
            Assert.That(s.Clips(lead).Single().Pos, Is.EqualTo(which == "arrangement" ? 2 * ArrangementSurface.Bar : 2));
            AssertOneStep(beforeTrack);

            // The last instrument a drop made is the next default.
            var drumsDrop = s.OnEmpty(One(Gear("Neo Drums")), 4);
            Assert.That(drumsDrop.Ok && drumsDrop.Action == DropAction.NewTrack, Is.True, drumsDrop.Action + " " + drumsDrop.Message + " " + drumsDrop.Machine?.DLL?.Info?.Type);
            Assert.That(DropSupport.LastInstrument, Is.EqualTo("Neo Drums"));
            var again = s.OnEmpty(Files(file), 5);
            Assert.That(again.Machine.DLL.Name, Is.EqualTo("Neo Drums"), "the last instrument used");

            // Not on an audio track; not a file that is no MIDI file.
            var audio = Core.SessionLayout.NewAudioTrack("Loops");
            Settle();
            Assert.That(s.PlanTrack(audio, Files(file)).Refusal, Does.Contain("doesn't play notes"));
            string junk = Work("junk.mid");
            File.WriteAllBytes(junk, new byte[] { 1, 2, 3 });
            Assert.That(s.PlanEmpty(Files(junk)).Refusal, Does.Contain("can't be read"));

        }

        [AvaloniaTest] public void AMidiFileIsANewTrackWithItsNotesOrANewPatternInTheArrangement() => MidiRows("arrangement");
        [AvaloniaTest] public void AMidiFileIsANewTrackWithItsNotesOrANewPatternInTheSession() => MidiRows("session");

        [Test]
        public void TheMidiReaderTakesTheNotesTheTempoAndTheMeter()
        {
            var f = ReBuzz.Core.Drop.MidiFile.Read(MidiFile("reader.mid", 60, 62));
            Assert.That(f.Notes.Select(n => (n.Start, n.Length, n.Pitch, n.Velocity)), Is.EqualTo(new[] { (0.0, 1.0, 60, 100), (1.0, 1.0, 62, 100) }));
            Assert.That(f.Bpm, Is.EqualTo(120));
            Assert.That(f.Meter, Is.EqualTo((3, 4)));
            Assert.That(f.LengthBeats, Is.EqualTo(2));
            Assert.Throws<InvalidDataException>(() => ReBuzz.Core.Drop.MidiFile.Parse(new byte[] { 1, 2, 3 }));
            Assert.Throws<InvalidDataException>(() => ReBuzz.Core.Drop.MidiFile.Parse(new byte[] { (byte)'M', (byte)'T', (byte)'h', (byte)'d', 0, 0, 0, 6, 0, 0, 0, 1, 0xE2, 0x78 }), "SMPTE time isn't beats");
        }

        // ------------------------------------------------------------------ presets

        static BrowserDragData Preset(int skip = 0)
        {
            var model = MachineBrowserModel.For(Core, Studio.Host.Registry);
            var p = model.PresetItems().Where(x => x.Id == "Neo Poly").Skip(skip).First();
            return p.Drag;
        }

        void PresetRows(string which)
        {
            using var s = Open(which);
            string before = State();
            var first = Preset(0);
            var made = s.OnEmpty(One(first), 1);
            Assert.That(made.Ok, Is.True, made.Message);
            Assert.That(made.Action, Is.EqualTo(DropAction.NewTrackWithPreset));
            Assert.That(made.Machine.DLL.Name, Is.EqualTo("Neo Poly"));
            Assert.That(Core.SongCore.Sequences.Any(q => q.Machine == made.Machine), Is.True, "with a track");
            var loaded = made.Machine.AllNonInputParameters().Select(p => p.GetValue(0)).ToList();
            AssertOneStep(before);

            // On a track of that machine: it loads.
            var juno = AddTrack("Neo Poly", "Juno");
            Settle();
            var plain = juno.AllNonInputParameters().Select(p => p.GetValue(0)).ToList();
            string beforeLoad = State();
            var r = s.OnTrack(juno, One(Preset(1)));
            Assert.That(r.Ok, Is.True, r.Message);
            Assert.That(r.Action, Is.EqualTo(DropAction.LoadPreset));
            Assert.That(juno.AllNonInputParameters().Select(p => p.GetValue(0)).ToList(), Is.Not.EqualTo(plain), "the preset's values are in");
            Core.SongCore.ActionStack.Undo();
            Settle();
            Assert.That(juno.AllNonInputParameters().Select(p => p.GetValue(0)).ToList(), Is.EqualTo(plain), "one undo step puts them back");
            Core.SongCore.ActionStack.Redo();
            Settle();

            // On a track of another machine: refused, with why.
            var lead = AddTrack("Neo Drums", "Lead", connect: false);
            Settle();
            var plan = s.PlanTrack(lead, One(first));
            Assert.That(plan.Allowed, Is.False);
            Assert.That(plan.Refusal, Does.Contain("isn't a Neo Poly"));
        }

        [AvaloniaTest] public void APresetIsANewTrackWithItOrLoadsIntoItsMachineInTheArrangement() => PresetRows("arrangement");
        [AvaloniaTest] public void APresetIsANewTrackWithItOrLoadsIntoItsMachineInTheSession() => PresetRows("session");

        // ------------------------------------------------------------------ save and reload

        void SaveAndReload(string which)
        {
            using var s = Open(which);
            string wav = Sine("keep.wav", 1), mid = MidiFile("keep.mid", 60, 67);
            var lead = AddTrack("Neo Poly", "Lead");
            Settle();
            s.OnEmpty(One(Gear("Neo Drums")), 0);
            s.OnTrack(lead, One(Gear("Neo Comp")));
            var audio = s.OnEmpty(Files(wav), 2).Machine;
            s.OnEmpty(Files(mid), 4);
            string expected = State();
            string path = Work($"keep-{which}.nbl");
            Core.SaveSongCopy(path);
            Core.NewSong();
            Settle();
            Assert.That(State(), Is.Not.EqualTo(expected));
            Core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(Core.TryOpenSongFile(path), Is.True);
            Settle();
            Assert.That(State(), Is.EqualTo(expected), "the song comes back as the drops left it");
            Assert.That(Core.AudioAssets.Count, Is.EqualTo(1), "the dropped audio is in the song's store");
            Assert.That(Core.SongCore.Machines.Count(m => m.DLL.Name == "Neo Audio"), Is.EqualTo(1));
            Assert.That(audio.Name, Is.EqualTo(Core.SongCore.Machines.First(m => m.DLL.Name == "Neo Audio").Name));
        }

        [AvaloniaTest] public void WhatWasDroppedSurvivesASaveAndAReloadInTheArrangement() => SaveAndReload("arrangement");
        [AvaloniaTest] public void WhatWasDroppedSurvivesASaveAndAReloadInTheSession() => SaveAndReload("session");

        // ------------------------------------------------------------------ feedback

        [AvaloniaTest, Category("Gallery")]
        public void TheArrangementShowsWhereAnythingWillGoAndWhyNotBeforeItIsDropped()
        {
            using var s = new ArrangementSurface();
            var lead = AddTrack("Neo Poly", "Lead");
            Settle();
            var canvas = s.Canvas;
            var row = canvas.Rows.First(r => r.Machine == lead && r.Kind == ArrangementRowKind.Lane);
            var onTrack = new Point(canvas.Geometry.XOf(32) + 1, canvas.YOf(row) + 20);
            var below = new Point(canvas.Geometry.XOf(32) + 1, canvas.YOf(canvas.Rows[^1]) + canvas.Rows[^1].Height + 12);

            // The track under the pointer is lit.
            var plan = canvas.ShowDropAt(One(Gear("Neo Comp")), onTrack);
            Settle();
            Assert.That(plan.Allowed, Is.True);
            Assert.That(canvas.DropLitMachine, Is.SameAs(lead), "the target track is lit");
            Assert.That(plan.MakesTrack, Is.False);
            Shot(s.Window, "drag-drop-arrangement-lit-track");

            // A line where a new track will go.
            var fresh = canvas.ShowDropAt(One(Gear("Neo Poly")), below);
            Settle();
            Assert.That(fresh.MakesTrack, Is.True, "an instrument on empty space makes a track: its insertion line is drawn");
            Assert.That(fresh.Describe, Does.Contain("New track"));
            Shot(s.Window, "drag-drop-arrangement-new-track");

            // A refusal with its reason.
            var refused = canvas.ShowDropAt(One(Gear("Neo Comp")), below);
            Settle();
            Assert.That(refused.Allowed, Is.False);
            Assert.That(canvas.DropFeedback.Refusal, Does.Contain("Drop on a track"));
            Shot(s.Window, "drag-drop-arrangement-refused");

            // The time snaps to the beat; Shift keeps the tick.
            double beat = Core.TPB;
            var off = new Point(canvas.Geometry.XOf(32) + 2.4 * canvas.Geometry.TickWidth, onTrack.Y);
            Assert.That(canvas.TargetAt(off, free: false).Time, Is.EqualTo(32), "snapped to the beat");
            Assert.That(canvas.TargetAt(off, free: true).Time, Is.EqualTo(34), "Shift: the tick");
            canvas.ShowDropAt(Files(Sine("snap.wav")), off);
            Shot(s.Window, "drag-drop-arrangement-audio-snap");

            canvas.ClearDropFeedback();
            Settle();
            Assert.That(canvas.DropFeedback, Is.Null, "leaving the canvas clears it");
            _ = beat;
        }

        [AvaloniaTest, Category("Gallery")]
        public void TheSessionLightsTheSlotsTheTrackAndTheLineForANewTrack()
        {
            using var s = new SessionSurface();
            var lead = AddTrack("Neo Poly", "Lead");
            Settle();
            s.Rig.Settle();
            var view = s.View;
            int track = s.Rig.Layout.FirstTrackOf(lead);
            string a = Sine("s1.wav"), b = Sine("s2.wav", 2);

            var plan = view.ShowDropAt(Files(a, b), view.DropPointOf(view.SlotAt(track, 1)));
            s.Rig.Settle();
            Assert.That(plan.Action, Is.EqualTo(DropAction.NewAudioTrack), "audio on a generator's slot makes an audio track");
            var audio = Core.SessionLayout.NewAudioTrack("Loops");
            s.Rig.Settle();
            int at = s.Rig.Layout.FirstTrackOf(audio);
            plan = view.ShowDropAt(Files(a, b), view.DropPointOf(view.SlotAt(at, 1)));
            s.Rig.Settle();
            Assert.That(plan.Action, Is.EqualTo(DropAction.AudioClips));
            Assert.That(view.DropSlots, Is.EqualTo(new[] { (at, 1), (at, 2) }), "the slots the files would fill are lit");
            Shot(s.Rig.Window, "drag-drop-session-slots");

            var fresh = view.ShowDropAt(One(Gear("Neo Poly")), view.DropPointOnEmptySpace());
            s.Rig.Settle();
            Assert.That(fresh.MakesTrack, Is.True, "a line where the new track will go");
            Shot(s.Rig.Window, "drag-drop-session-new-track");

            var refused = view.ShowDropAt(One(Gear("Neo Comp")), view.DropPointOnEmptySpace());
            s.Rig.Settle();
            Assert.That(refused.Allowed, Is.False);
            Assert.That(refused.Refusal, Does.Contain("Drop on a track"));
            Shot(s.Rig.Window, "drag-drop-session-refused");

            view.ClearDropFeedback();
            Assert.That(view.DropFeedback, Is.Null);
            Assert.That(view.DropSlots, Is.Empty);
        }

        [AvaloniaTest]
        public void FilesFromTheDesktopAreReadAsAudioOrMidi()
        {
            string wav = Sine("desk.wav"), mid = MidiFile("desk.mid", 60), txt = Work("desk.txt");
            File.WriteAllText(txt, "x");
            Assert.That(DropRouter.FromFile(wav)!.Kind, Is.EqualTo(BrowserKind.Audio));
            Assert.That(DropRouter.FromFile(mid)!.Kind, Is.EqualTo(BrowserKind.Midi));
            Assert.That(DropRouter.FromFile(txt), Is.Null, "anything else is not a drop");
            Assert.That(DropSupport.Read(null), Is.Empty);
            Assert.That(DropSupport.Read(new DataTransfer()), Is.Empty);
            var browser = DropSupport.Read(BrowserDragSource.Package(Gear("Neo Poly")));
            Assert.That(browser, Is.EqualTo(new[] { Gear("Neo Poly") }), "a browser item is read as it is");
        }

        // ------------------------------------------------------------------ the Machine View

        [AvaloniaTest, Category("Gallery")]
        public void TheMachineViewTakesAudioMidiAndPresetsThroughTheSameRules()
        {
            var (window, view) = OpenEmpty();
            try
            {
                var empty = new Point(80, 90);
                string before = Graph();
                var audio = view.Canvas.DropItems(Files(Sine("mv.wav")), empty);
                Settle();
                Assert.That(audio.Ok, Is.True, audio.Message);
                Assert.That(audio.Machine.DLL.Name, Is.EqualTo("Neo Audio"), "audio on empty canvas: a new audio track");
                Assert.That(audio.Machine.Outputs.Select(c => c.Destination.Name), Is.EqualTo(new[] { "Master" }));
                Core.SongCore.ActionStack.Undo();
                Settle();
                Assert.That(Graph(), Is.EqualTo(before), "one undo step");

                var sampler = AddTrack("Neo Sampler", "Keys");
                Settle();
                var load = view.Canvas.DropItems(Files(Sine("mv2.wav")), new Point(0, 0) + view.PointOf(sampler, view.Canvas) - new Point(0, 0));
                Settle();
                Assert.That(load.Action, Is.EqualTo(DropAction.LoadSample), "on a sampler's box: its sample");

                var poly = AddTrack("Neo Poly", "Lead");
                Settle();
                var notes = view.Canvas.DropItems(Files(MidiFile("mv.mid", 60, 64)), view.PointOf(poly, view.Canvas));
                Settle();
                Assert.That(notes.Ok, Is.True, notes.Message);
                Assert.That(notes.Action, Is.EqualTo(DropAction.MidiNewPattern), "a MIDI file on a box: a new pattern with the notes");
                Assert.That(NotesOf(notes.Patterns.Single()).Count, Is.EqualTo(2));

                var track = view.Canvas.DropItems(One(Gear("Neo Drums")), new Point(300, 200));
                Settle();
                Assert.That(track.Action, Is.EqualTo(DropAction.NewTrack), "a generator on empty canvas: a machine and its track, wired to Master");
                Assert.That(track.Machine.Outputs.Select(c => c.Destination.Name), Is.EqualTo(new[] { "Master" }));
                Assert.That(Core.SongCore.Sequences.Any(q => q.Machine == track.Machine), Is.True);

                var plan = view.Canvas.ShowDropAt(One(Gear("Neo Comp")), view.PointOf(poly, view.Canvas));
                Assert.That(plan.Allowed, Is.False, "an effect can't replace a generator");
                Assert.That(view.Canvas.DropFeedback.Refusal, Does.Contain("generator"));
                Shot(window, "drag-drop-machine-refused");
            }
            finally
            {
                Close(window);
            }
        }

        // ------------------------------------------------------------------ the agent

        static System.Text.Json.Nodes.JsonObject Tool(string tool, System.Text.Json.Nodes.JsonObject args)
        {
            var message = new System.Text.Json.Nodes.JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new ReBuzz.Agent.Mcp.McpSession(), _ => System.Threading.Tasks.Task.CompletedTask, System.Threading.CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                System.Threading.Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        [AvaloniaTest]
        public void DropItemDoesWhatADropDoesAndSaysWhatItWouldDo()
        {
            using var s = new ArrangementSurface();
            string wav = Sine("agent.wav");
            string before = State();
            var plan = Tool("drop_item", new() { ["view"] = "arrangement", ["kind"] = "audio", ["path"] = wav, ["plan"] = true });
            Assert.That(plan["isError"]?.GetValue<bool>() ?? false, Is.False, plan.ToJsonString());
            Assert.That((string)plan["structuredContent"]!["action"]!, Is.EqualTo("NewAudioTrack"));
            Assert.That(State(), Is.EqualTo(before), "plan=true changes nothing");
            var done = Tool("drop_item", new() { ["view"] = "arrangement", ["kind"] = "audio", ["path"] = wav, ["target"] = new System.Text.Json.Nodes.JsonObject { ["at_beats"] = 4 } });
            Assert.That(done["isError"]?.GetValue<bool>() ?? false, Is.False, done.ToJsonString());
            var track = Core.SongCore.Machines.First(m => m.DLL.Name == "Neo Audio");
            Assert.That(s.Clips(track).Single().Pos, Is.EqualTo(4 * Core.TPB), "at the beat given");
            Core.SongCore.ActionStack.Undo();
            Settle();
            Assert.That(State(), Is.EqualTo(before), "one undo step");
            var refused = Tool("drop_item", new() { ["view"] = "session", ["kind"] = "machine", ["id"] = "Neo Comp" });
            Assert.That(refused["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(refused.ToJsonString(), Does.Contain("Drop on a track"));
            var mid = Tool("drop_item", new() { ["view"] = "arrangement", ["kind"] = "midi", ["path"] = MidiFile("agent.mid", 60, 64, 67) });
            Assert.That(mid["isError"]?.GetValue<bool>() ?? false, Is.False, mid.ToJsonString());
            Assert.That((string)mid["structuredContent"]!["action"]!, Is.EqualTo("MidiNewTrack"));
        }
    }
}
