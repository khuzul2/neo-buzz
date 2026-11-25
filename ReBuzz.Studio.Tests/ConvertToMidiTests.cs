// Fork (docs/specs/pitch.md section 9.4 and 9.5): Convert to MIDI in the studio's host: the agent's audio_convert_to_midi (a new MIDI track with an
// instrument, a clip where the source is, one undo step), the notes in the clip, save and reopen, the menus and the dialog's flow, cancelling,
// and the work staying off the UI thread.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using libsndfile;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using ReBuzz.Core.Session;
using ReBuzz.SessionTests.Convert;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class ConvertToMidiTests
    {
        static string Work(string name)
        {
            string dir = TestTemp.Path($"convert-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static string Wav(string name, float[] mono, int rate)
        {
            string path = Work(name);
            using var f = SoundFile.Create(path, rate, 1, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT);
            f.WriteFloat(mono, 0, mono.Length);
            return path;
        }

        static readonly (double At, double Length, double Midi)[] Melody =
            { (0.5, 0.5, 60), (1.2, 0.4, 64), (1.7, 0.6, 67), (2.5, 0.3, 72), (3.0, 0.8, 69) };

        static string MelodyFile(string name = "tune.wav") => Wav(name, ConvertSignals.Line(Melody, 4.2), ConvertSignals.Rate);

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static void Pump(Task t)
        {
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!t.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail("the task didn't finish in time");
                Thread.Sleep(2);
            }
            t.GetAwaiter().GetResult();
        }

        static int Visible(ReBuzzCore core) => core.SongCore.MachinesList.Count(m => !m.Hidden);

        static JsonObject Ok(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            return r["structuredContent"]!.AsObject();
        }

        /// <summary>An audio clip of a file at a Session slot and in the arrangement, as (machine, clip, the clip).</summary>
        static (string Machine, string ClipName, IClip Clip, JsonObject Imported) Import(ReBuzzCore core, string file, int slot = 3, double atBeats = 4)
        {
            var imported = Ok(Tool("audio_import", new JsonObject { ["path"] = file, ["slot"] = slot, ["at_beats"] = atBeats }));
            string machine = (string)imported["machine"], clipName = (string)imported["clip"];
            return (machine, clipName, core.SongCore.MachinesList.First(m => m.Name == machine).ClipsList.First(p => p.Name == clipName), imported);
        }

        static List<ClipNote> NotesOf(ReBuzzCore core, string machine, string clipName) =>
            core.SessionLayout.NotesOf(core.SongCore.MachinesList.First(m => m.Name == machine).ClipsList.First(p => p.Name == clipName));

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        [AvaloniaTest]
        public void AMelodyBecomesAMidiTrackWithAClipWhereTheSourceIsInOneUndoStep()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, clipName, clip, imported) = Import(core, MelodyFile());
            var layout = core.SessionLayout;
            int machinesBefore = Visible(core), sequencesBefore = core.SongCore.SequencesList.Count;
            var sourceEvent = core.SongCore.SequencesList.First(q => q.Machine.Name == machine).Events.Single(e => e.Value.Clip == clip);

            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody" }));
            Assert.That((int)o["notes"], Is.EqualTo(Melody.Length));
            Assert.That((string)o["instrument"], Is.EqualTo("Neo Poly"));
            Assert.That((string)o["machine"], Does.StartWith("Melody - "), "named after the mode and the clip");
            Assert.That((int)o["slot"], Is.EqualTo((int)imported["slot"]), "the source's Session scene");
            Assert.That(Visible(core), Is.EqualTo(machinesBefore + 1));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequencesBefore + 1));

            var made = core.SongCore.MachinesList.First(m => m.Name == (string)o["machine"]);
            Assert.That(made.DLL.Name, Is.EqualTo("Neo Poly"), "an instrument on the track");
            var midi = made.ClipsList.Single(p => p.Name == (string)o["clip"]);
            Assert.That(midi.Length, Is.EqualTo(clip.Length), "the same length");
            Assert.That(layout.IsAudioClip(midi), Is.False);
            Assert.That(layout.ClipAt(layout.FirstTrackOf(made), (int)imported["slot"] - 1), Is.SameAs(midi), "in the same scene");
            var at = core.SongCore.SequencesList.First(q => q.Machine == made).Events.Single();
            Assert.That(at.Key, Is.EqualTo(sourceEvent.Key), "at the same arrangement position");
            Assert.That(at.Value.Clip, Is.SameAs(midi));

            double bpm = layout.AudioOf(clip).Bpm;
            var notes = layout.NotesOf(midi);
            Assert.That(notes.Select(n => n.Note), Is.EqualTo(Melody.Select(m => (int)m.Midi)));
            for (int i = 0; i < Melody.Length; i++)
            {
                double expected = Melody[i].At * bpm / 60 * core.TPB * SessionRig.TB;
                double tolerance = 0.03 * bpm / 60 * core.TPB * SessionRig.TB;
                Assert.That(notes[i].Time, Is.EqualTo(expected).Within(tolerance), $"note {i} start (clip time)");
                Assert.That(notes[i].Duration, Is.EqualTo(Melody[i].Length * bpm / 60 * core.TPB * SessionRig.TB).Within(0.06 * bpm / 60 * core.TPB * SessionRig.TB), $"note {i} length");
                Assert.That(notes[i].Velocity, Is.InRange(1, 127));
            }

            // one undo step takes the track, its clip and its notes away; redo brings them back
            core.SongCore.ActionStack.Undo();
            Assert.That(Visible(core), Is.EqualTo(machinesBefore));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequencesBefore));
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == (string)o["machine"]), Is.False);
            core.SongCore.ActionStack.Redo();
            Assert.That(Visible(core), Is.EqualTo(machinesBefore + 1));
            var again = core.SongCore.MachinesList.First(m => m.Name == (string)o["machine"]);
            Assert.That(layout.NotesOf(again.ClipsList.Single(p => !layout.IsAudioClip(p))).Count, Is.EqualTo(Melody.Length), "the notes are back");
        }

        [AvaloniaTest]
        public void TheConversionSurvivesSaveAndReopen()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, clipName, clip, imported) = Import(core, MelodyFile("saved.wav"));
            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody", ["pitch_bends"] = false }));
            string newMachine = (string)o["machine"], newClip = (string)o["clip"];
            var before = NotesOf(core, newMachine, newClip);
            Assert.That(before.Count, Is.EqualTo(Melody.Length));
            string path = Work("converted.nbl");
            core.SaveSongCopy(path);
            Reopen(core, path);
            var back = NotesOf(core, newMachine, newClip);
            Assert.That(back, Is.EqualTo(before), "the same notes after saving and opening");
            var made = core.SongCore.MachinesList.First(m => m.Name == newMachine);
            Assert.That(made.DLL.Name, Is.EqualTo("Neo Poly"));
            var layout = core.SessionLayout;
            Assert.That(layout.ClipAt(layout.FirstTrackOf(made), (int)imported["slot"] - 1)?.Name, Is.EqualTo(newClip), "still in the scene");
        }

        [AvaloniaTest]
        public void TheClipsTransposeIsAddedAndQuantizeSnapsTheStarts()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, clipName, clip, _) = Import(core, MelodyFile("shifted.wav"));
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["transpose"] = 2 }));
            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody", ["quantize"] = "1/4", ["strength"] = 1 }));
            var notes = NotesOf(core, (string)o["machine"], (string)o["clip"]);
            Assert.That(notes.Select(n => n.Note), Is.EqualTo(Melody.Select(m => (int)m.Midi + 2)), "transposed by the clip's two semitones");
            int beat = core.TPB * SessionRig.TB;
            Assert.That(notes.Select(n => n.Time % beat), Is.All.EqualTo(0), "every start is on a beat");
        }

        [AvaloniaTest]
        public void DrumsGoToANeoDrumsTrackAsGeneralMidiNotes()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var hits = new List<(string, double)>();
            for (int bar = 0; bar < 2; bar++)
            {
                double t0 = 0.3 + bar * 2.0;
                hits.Add(("kick", t0)); hits.Add(("snare", t0 + 0.5)); hits.Add(("kick", t0 + 1.0)); hits.Add(("snare", t0 + 1.5));
                for (int i = 0; i < 4; i++) hits.Add(("hat", t0 + 0.25 + i * 0.5));
            }
            var (machine, clipName, _, _) = Import(core, Wav("beat.wav", ConvertSignals.Drums(hits, 4.5), ConvertSignals.Rate));
            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "drums" }));
            Assert.That((string)o["instrument"], Is.EqualTo("Neo Drums"));
            Assert.That((int)o["notes"], Is.EqualTo(hits.Count));
            var notes = NotesOf(core, (string)o["machine"], (string)o["clip"]);
            Assert.That(notes.Count(n => n.Note == 36), Is.EqualTo(4), "kicks");
            Assert.That(notes.Count(n => n.Note == 38), Is.EqualTo(4), "snares");
            Assert.That(notes.Count(n => n.Note == 42), Is.EqualTo(8), "closed hats");
            Assert.That(core.SongCore.MachinesList.First(m => m.Name == (string)o["machine"]).DLL.Name, Is.EqualTo("Neo Drums"));
        }

        [AvaloniaTest]
        public void HarmonyFindsChordsAndApplyFalseChangesNothing()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var chords = new[] { (0.3, 1.6, new[] { 60, 64, 67 }), (2.2, 1.6, new[] { 57, 60, 64 }) };
            var (machine, clipName, _, _) = Import(core, Wav("chords.wav", ConvertSignals.Chords(chords, 4.2), 22050));
            int before = Visible(core);
            var look = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "harmony", ["apply"] = false }));
            Assert.That(Visible(core), Is.EqualTo(before), "apply=false changes nothing");
            var found = look["found"]!.AsArray().Select(f => (int)f!["note"]).ToList();
            foreach (var chord in chords) foreach (int p in chord.Item3) Assert.That(found, Does.Contain(p), MidiNames.Of(p));
            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "harmony" }));
            Assert.That((string)o["instrument"], Is.EqualTo("Neo Poly"));
            var notes = NotesOf(core, (string)o["machine"], (string)o["clip"]);
            foreach (var chord in chords) foreach (int p in chord.Item3) Assert.That(notes.Any(n => n.Note == p), Is.True, MidiNames.Of(p));
        }

        [AvaloniaTest]
        public void ASlideKeepsOneNoteWithPitchBendEvents()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, clipName, _, _) = Import(core, Wav("slide.wav", ConvertSignals.Glide(0.5, 1.0, 60, 61.6, 2), ConvertSignals.Rate));
            var o = Ok(Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody", ["pitch_bends"] = true }));
            Assert.That((int)o["notes"], Is.EqualTo(1));
            Assert.That((int)o["pitch_bends"], Is.GreaterThan(2));
            var midi = core.SongCore.MachinesList.First(m => m.Name == (string)o["machine"]).ClipsList.Single(p => !core.SessionLayout.IsAudioClip(p));
            var column = midi.Columns.First(c => c.Type == ClipColumnType.MIDI);
            int bends = column.GetEvents(int.MinValue, int.MaxValue).Count(e => (e.Value & 0xF0) == 0xE0);
            Assert.That(bends, Is.GreaterThan(2), "pitch-bend events in the clip's MIDI column");
        }

        [AvaloniaTest]
        public void TheAgentRefusesWhatItCannotDoAndSaysWhy()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig("Lead");
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, clipName, _, _) = Import(core, MelodyFile("refuse.wav"));
            int machines = Visible(core);
            var notAudio = Tool("audio_convert_to_midi", new JsonObject { ["machine"] = "Lead", ["clip"] = "00", ["mode"] = "melody" });
            Assert.That(notAudio["isError"]?.GetValue<bool>(), Is.True, "a clip of an instrument isn't audio");
            var mode = Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "singing" });
            Assert.That(mode["isError"]?.GetValue<bool>(), Is.True);
            var quantize = Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody", ["quantize"] = "1/7" });
            Assert.That(quantize["isError"]?.GetValue<bool>(), Is.True);
            var range = Tool("audio_convert_to_midi", new JsonObject { ["machine"] = machine, ["clip"] = clipName, ["mode"] = "melody", ["range"] = "tuba" });
            Assert.That(range["isError"]?.GetValue<bool>(), Is.True);
            Assert.That(Visible(core), Is.EqualTo(machines), "a refused call changes nothing");
        }

        [AvaloniaTest]
        public void TheMenuOffersTheThreeModesAndTheDialogsFlowPutsTheNotesOnATrack()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; ConvertFlow.Responder = null; core.Modified = false; });
            var (machine, clipName, clip, imported) = Import(core, MelodyFile("flow.wav"));
            var view = rig.Show();
            var layout = core.SessionLayout;
            int track = layout.FirstTrackOf(core.SongCore.MachinesList.First(m => m.Name == machine));
            view.ShowSlotMenu(view.SlotAt(track, (int)imported["slot"] - 1));
            var convert = view.LastMenu.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Convert to MIDI");
            Assert.That(convert.Items.OfType<MenuItem>().Select(i => i.Tag as string),
                Is.EqualTo(new[] { "Convert to MIDI: Melody", "Convert to MIDI: Harmony", "Convert to MIDI: Drums" }));

            string asked = null;
            ConvertMode askedMode = default;
            ConvertFlow.Responder = (name, mode) => { asked = name; askedMode = mode; return new ConvertChoice(new ConvertOptions(), new ConvertApply { QuantizeDivision = 2, QuantizeStrength = 1 }); };
            int machines = Visible(core);
            Pump(ConvertFlow.ConvertClip(clip, ConvertMode.Melody));
            Assert.That(asked, Is.EqualTo("flow"));
            Assert.That(askedMode, Is.EqualTo(ConvertMode.Melody));
            Assert.That(ConvertFlow.LastOutcome, Is.Not.Null, ConvertFlow.LastSummary);
            Assert.That(ConvertFlow.LastSummary, Does.Contain("Neo Poly"));
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
            var notes = layout.NotesOf(ConvertFlow.LastOutcome.Clip);
            Assert.That(notes.Count, Is.EqualTo(Melody.Length));
            int half = core.TPB * SessionRig.TB / 2;
            Assert.That(notes.Select(n => n.Time % half), Is.All.EqualTo(0), "quantized to eighths");

            ConvertFlow.Responder = (_, _) => null;
            Pump(ConvertFlow.ConvertClip(clip, ConvertMode.Drums));
            Assert.That(Visible(core), Is.EqualTo(machines + 1), "cancel changes nothing");

            // a clip that isn't audio is refused with the reason, and nothing changes
            ConvertFlow.Responder = (_, _) => throw new InvalidOperationException("must not ask");
            Dialogs.Responder = (m, c, b) => UserAnswer.Ok;
            var instrumentClip = ConvertFlow.LastOutcome.Clip;
            Pump(ConvertFlow.ConvertClip(instrumentClip, ConvertMode.Melody));
            Assert.That(ConvertFlow.LastSummary, Does.Contain("isn't an audio clip"));
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
        }

        [AvaloniaTest]
        public void TheWorkRunsOffTheUiThreadAndCancellingStopsItWithoutChangingTheSong()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            // a minute of chords: the harmony model takes a while, so the call has to return before it is done
            var long1 = ConvertSignals.Chords(new[] { (0.2, 59.0, new[] { 60, 64, 67 }) }, 60, 22050);
            var (machine, clipName, clip, _) = Import(core, Wav("long.wav", long1, 22050));
            var layout = core.SessionLayout;
            var source = layout.ConvertSourceOfClip(clip, out string error);
            Assert.That(source, Is.Not.Null, error);
            int ui = Environment.CurrentManagedThreadId;
            var worker = new HashSet<int>();
            using var cts = new CancellationTokenSource();
            int machines = Visible(core);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var task = layout.ConvertAsync(source, new ConvertOptions { Mode = ConvertMode.Harmony }, f => { lock (worker) worker.Add(Environment.CurrentManagedThreadId); if (f > 0.15) cts.Cancel(); }, cts.Token);
            Assert.That(clock.ElapsedMilliseconds, Is.LessThan(500), "starting it doesn't wait for the work");
            Assert.That(task.IsCompleted, Is.False, "the work is still going on");
            var until = DateTime.UtcNow.AddSeconds(120);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();                    // the UI keeps being served while the worker runs
                if (DateTime.UtcNow > until) Assert.Fail("the conversion didn't stop");
                Thread.Sleep(2);
            }
            Assert.That(task.IsCanceled || task.IsFaulted && task.Exception!.InnerException is OperationCanceledException, Is.True, "cancelled");
            lock (worker) Assert.That(worker, Is.Not.Empty.And.No.Member(ui), "progress came from a worker thread");
            Assert.That(Visible(core), Is.EqualTo(machines), "a cancelled conversion puts nothing in the song");
        }
    }
}
