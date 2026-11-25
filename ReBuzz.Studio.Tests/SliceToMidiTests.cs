// Fork (docs/specs/audio-to-midi.md, "Slice to new MIDI track (SL1 as built)"): Slice to new MIDI track in the studio's host. The agent's audio_slice_to_midi on synthetic
// loops (a click track with known hits, a drum loop, a warped clip at a song tempo that isn't the loop's): the slices' boundaries, the waves, the Neo Sampler in Kit mode, the
// clip's notes on the right ticks, one undo step, save and reopen as .nbl, the null test against the source clip's own render, and the menus and the dialog.

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
using ReBuzz.Agent.Audio;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.SessionTests.Convert;
using ReBuzz.Studio.Shell;
using NeoBuzz.TestSupport;

namespace ReBuzz.Studio.Tests
{
    public class SliceToMidiTests
    {
        const int FileRate = ConvertSignals.Rate;

        static string Work(string name)
        {
            string dir = TestTemp.Path($"slice-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        static string Wav(string name, float[] mono, int rate = FileRate, bool stereo = false)
        {
            string path = Work(name);
            using var f = SoundFile.Create(path, rate, stereo ? 2 : 1, Format.SF_FORMAT_WAV | Format.SF_FORMAT_FLOAT);
            if (!stereo) f.WriteFloat(mono, 0, mono.Length);
            else
            {
                var both = new float[mono.Length * 2];
                for (int i = 0; i < mono.Length; i++) both[2 * i] = both[2 * i + 1] = mono[i];
                f.WriteFloat(both, 0, mono.Length);
            }
            return path;
        }

        /// <summary>A click: a 2 kHz tone that starts at zero and dies in a few milliseconds, at known times and levels.</summary>
        static float[] Clicks(IEnumerable<(double At, double Level)> hits, double seconds)
        {
            var x = new float[(int)(FileRate * seconds)];
            foreach (var (at, level) in hits)
            {
                int a = (int)Math.Round(at * FileRate);
                for (int i = 0; i < FileRate / 50 && a + i < x.Length; i++)
                {
                    double t = i / (double)FileRate;
                    x[a + i] += (float)(level * Math.Sin(2 * Math.PI * 2000 * t) * Math.Exp(-t / 0.003));
                }
            }
            return x;
        }

        static readonly double[] Hits = { 0.0, 0.26, 0.5, 0.77, 1.0, 1.25, 1.5, 1.74 };

        static JsonObject Tool(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(180);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        static JsonObject Ok(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>() ?? false, Is.False, r.ToJsonString());
            return r["structuredContent"]!.AsObject();
        }

        static string Error(JsonObject r)
        {
            Assert.That(r["isError"]?.GetValue<bool>(), Is.True, "refused: " + r.ToJsonString());
            return r["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        }

        static int Visible(ReBuzzCore core) => core.SongCore.MachinesList.Count(m => !m.Hidden);

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

        /// <summary>The audio clip of a file at a Session slot and in the arrangement, its tempo set so a loop is the bars it is; (machine, pattern, the clip, the song's rate).</summary>
        static (string Machine, string Pattern, IPattern Clip, int Rate, JsonObject Imported) Import(ReBuzzCore core, string file, double bpm = 120, int slot = 1, double atBeats = 0, string warp = null)
        {
            var imported = Ok(Tool("audio_import", new JsonObject { ["path"] = file, ["slot"] = slot, ["at_beats"] = atBeats }));
            string machine = (string)imported["machine"], pattern = (string)imported["pattern"];
            var set = new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["tempo_bpm"] = bpm };
            if (warp != null) set["warp"] = warp;
            var clipJson = Ok(Tool("audio_clip_set", set));
            var clip = core.SongCore.MachinesList.First(m => m.Name == machine).PatternsList.First(p => p.Name == pattern);
            return (machine, pattern, clip, (int)clipJson["audio"]!["sample_rate"]!, imported);
        }

        static JsonObject Slice(string machine, string pattern, string by = "transients", bool apply = true) =>
            Tool("audio_slice_to_midi", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["by"] = by, ["apply"] = apply });

        static MachineCore MachineNamed(ReBuzzCore core, string name) => core.SongCore.MachinesList.First(m => m.Name == name);

        static int Param(IMachine m, string name) =>
            m.ParameterGroups.First(g => g.Type == ParameterGroupType.Global).Parameters.First(p => p.Name == name).GetValue(0);

        static float[] WaveOf(IWave w, int channel = 0)
        {
            var layer = w.Layers.First();
            var x = new float[layer.SampleCount];
            layer.GetDataAsFloat(x, 0, 1, channel, 0, x.Length);
            return x;
        }

        static int FilledWaves(ReBuzzCore core) => core.SongCore.Wavetable.Waves.Count(w => w != null && w.Layers.Count > 0);

        static string Hear(JsonObject render, string key = "file") => (string)Ok(render)[key]!;

        // ------------------------------------------------------------------ the song

        [AvaloniaTest]
        public void AClickTrackIsSlicedOnItsHitsIntoWavesAKitAndNotesInOneUndoStep()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, pattern, clip, rate, imported) = Import(core, Wav("clicks.wav", Clicks(Hits.Select(h => (h, 0.4 + 0.05 * (h % 3))), 2.0)));
            var layout = core.SessionLayout;
            int machines = Visible(core), sequences = core.SongCore.SequencesList.Count, waves = FilledWaves(core);
            var sourceBefore = Ok(Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["pattern"] = pattern })).ToJsonString();
            var sourceEvent = core.SongCore.SequencesList.First(q => q.Machine.Name == machine).Events.Single(e => e.Value.Pattern == clip);

            var o = Ok(Slice(machine, pattern));
            Assert.That((int)o["count"], Is.EqualTo(8));
            Assert.That((bool)o["capped"], Is.False);
            Assert.That((string)o["machine"], Is.EqualTo(pattern + " slices"), "named after the clip");
            Assert.That((int)o["slot"], Is.EqualTo((int)imported["slot"]), "the source's Session scene");

            // the boundaries: within a millisecond of the known hits
            var slices = o["slices"]!.AsArray();
            for (int i = 0; i < 8; i++)
            {
                Assert.That((long)slices[i]!["start"]! * 1000.0 / rate, Is.EqualTo(Hits[i] * 1000).Within(1.0), $"slice {i + 1} starts on its hit");
                Assert.That((int)slices[i]!["key"]!, Is.EqualTo(36 + i), "keys from Kit base upward");
            }
            Assert.That(slices.Sum(s => (long)s!["length"]!), Is.GreaterThan((long)(1.74 * rate)), "the slices run to the end of the clip");

            // the waves: the first free slots, named after the clip, the length of the slice
            Assert.That(FilledWaves(core), Is.EqualTo(waves + 8));
            for (int i = 0; i < 8; i++)
            {
                int slot = (int)slices[i]!["slot"]!;
                Assert.That(slot, Is.EqualTo(waves + 1 + i), "the first free slots in order");
                var w = core.SongCore.Wavetable.Waves[slot - 1];
                Assert.That(w.Name, Is.EqualTo($"{pattern} {i + 1:00}"));
                Assert.That(w.Layers.First().SampleCount, Is.EqualTo((long)slices[i]!["length"]!));
                Assert.That(w.Layers.First().SampleRate, Is.EqualTo(rate));
            }

            // the track: Neo Sampler, Kit mode, zone n on slot n, one choke group, velocity doesn't change the level
            var sampler = MachineNamed(core, (string)o["machine"]);
            Assert.That(sampler.DLL.Name, Is.EqualTo("Neo Sampler"));
            Assert.That(Param(sampler, "Mode"), Is.EqualTo(1), "Kit");
            Assert.That(Param(sampler, "Kit base"), Is.EqualTo(36));
            Assert.That(Param(sampler, "Vel → amp"), Is.EqualTo(0));
            for (int z = 1; z <= 16; z++)
            {
                Assert.That(Param(sampler, $"Z{z} wave"), Is.EqualTo(z <= 8 ? (int)slices[z - 1]!["slot"]! : 0), $"zone {z}");
                if (z <= 8) Assert.That(Param(sampler, $"Z{z} choke"), Is.EqualTo(1), $"zone {z} in choke group 1");
            }
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequences + 1));

            // the clip: same length, scene and arrangement position; one note a slice, at its place in beats, as long as the slice, velocity 100
            var midi = sampler.PatternsList.Single(p => p.Name == (string)o["pattern"]);
            Assert.That(midi.Length, Is.EqualTo(clip.Length));
            Assert.That(layout.IsAudioClip(midi), Is.False);
            Assert.That(layout.PatternAt(layout.FirstTrackOf(sampler), (int)imported["slot"] - 1), Is.SameAs(midi), "in the same scene");
            var at = core.SongCore.SequencesList.First(q => q.Machine == sampler).Events.Single();
            Assert.That(at.Key, Is.EqualTo(sourceEvent.Key), "at the same arrangement position");
            Assert.That(at.Value.Pattern, Is.SameAs(midi));
            var notes = layout.NotesOf(midi);
            Assert.That(notes.Count, Is.EqualTo(8));
            double ticksPerSecond = 120 / 60.0 * core.TPB * SessionRig.TB;
            for (int i = 0; i < 8; i++)
            {
                Assert.That(notes[i].Note, Is.EqualTo(36 + i));
                Assert.That(notes[i].Velocity, Is.EqualTo(100));
                Assert.That(notes[i].Time, Is.EqualTo(Hits[i] * ticksPerSecond).Within(0.002 * ticksPerSecond), $"note {i + 1} is on its hit's tick");
                double next = i < 7 ? Hits[i + 1] : 2.0;
                Assert.That(notes[i].Duration, Is.EqualTo((next - Hits[i]) * ticksPerSecond).Within(0.002 * ticksPerSecond), $"note {i + 1} lasts as long as the slice");
            }
            double[] beats = slices.Select(s => (double)s!["beat"]!).ToArray();
            Assert.That(beats[1], Is.EqualTo(0.52).Within(0.005), "the beat is in the clip's beats");

            // the source clip is as it was
            Assert.That(Ok(Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["pattern"] = pattern })).ToJsonString(), Is.EqualTo(sourceBefore));

            // one undo step takes the track, its clip, the waves and the settings away
            core.SongCore.ActionStack.Undo();
            Assert.That(Visible(core), Is.EqualTo(machines));
            Assert.That(core.SongCore.SequencesList.Count, Is.EqualTo(sequences));
            Assert.That(FilledWaves(core), Is.EqualTo(waves), "the waves are gone");
            Assert.That(core.SongCore.MachinesList.Any(m => m.Name == (string)o["machine"]), Is.False);
            Assert.That(layout.PatternAt(layout.FirstTrackOf(MachineNamed(core, machine)), (int)imported["slot"] - 1), Is.SameAs(clip), "the source is in its slot");
            Assert.That(Ok(Tool("audio_clip_get", new JsonObject { ["machine"] = machine, ["pattern"] = pattern })).ToJsonString(), Is.EqualTo(sourceBefore), "the source is as it was");
            Assert.That(core.SongCore.SequencesList.First(q => q.Machine.Name == machine).Events.Single().Value.Pattern, Is.SameAs(clip));

            // redo brings it all back
            core.SongCore.ActionStack.Redo();
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
            Assert.That(FilledWaves(core), Is.EqualTo(waves + 8));
            var again = MachineNamed(core, (string)o["machine"]);
            Assert.That(Param(again, "Z8 wave"), Is.EqualTo((int)slices[7]!["slot"]!), "the zones are set again");
            Assert.That(Param(again, "Z1 choke"), Is.EqualTo(1));
            Assert.That(layout.NotesOf(again.PatternsList.Single(p => !layout.IsAudioClip(p))).Count, Is.EqualTo(8), "the notes are back");
        }

        [AvaloniaTest]
        public void EachModeGivesTheRightCountTheCapKeepsTheStrongestAndAGridOverSixteenIsRefused()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            // 24 clicks over 3 s (6 beats at 120 BPM), the levels all different
            var hits = Enumerable.Range(0, 24).Select(i => (At: i * 0.125, Level: 0.2 + 0.03 * ((i * 7) % 24))).ToArray();
            var (machine, pattern, clip, rate, _) = Import(core, Wav("many.wav", Clicks(hits, 3.0)));
            int machines = Visible(core), waves = FilledWaves(core);

            var capped = Ok(Slice(machine, pattern, "transients", apply: false));
            Assert.That((int)capped["count"], Is.EqualTo(16));
            Assert.That((int)capped["found"], Is.EqualTo(24));
            Assert.That((bool)capped["capped"], Is.True);
            var kept = capped["slices"]!.AsArray().Skip(1).Select(s => (long)s!["start"]! / (double)rate).ToList();
            var strongest = hits.Skip(1).OrderByDescending(h => h.Level).Take(15).Select(h => h.At).OrderBy(a => a).ToList();
            for (int i = 0; i < 15; i++) Assert.That(kept[i], Is.EqualTo(strongest[i]).Within(0.001), $"kept hit {i + 1}");
            Assert.That(Visible(core), Is.EqualTo(machines), "apply=false changes nothing");
            Assert.That(FilledWaves(core), Is.EqualTo(waves));

            var capApplied = Ok(Slice(machine, pattern, "transients"));
            Assert.That(capApplied["slices"]!.AsArray().Count, Is.EqualTo(16));
            Assert.That(Param(MachineNamed(core, (string)capApplied["machine"]), "Z16 wave"), Is.EqualTo((int)capApplied["slices"]![15]!["slot"]!), "all 16 zones are used");
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
            core.SongCore.ActionStack.Undo();

            // grids: 6 beats
            Assert.That((int)Ok(Slice(machine, pattern, "bar", false))["count"], Is.EqualTo(2));
            Assert.That((int)Ok(Slice(machine, pattern, "1/4", false))["count"], Is.EqualTo(6));
            Assert.That((int)Ok(Slice(machine, pattern, "1/8", false))["count"], Is.EqualTo(12));
            string refused = Error(Slice(machine, pattern, "1/16"));
            Assert.That(refused, Does.Contain("24").And.Contain("16"), "the message says how many it would make: " + refused);
            Assert.That(Visible(core), Is.EqualTo(machines), "a refused call changes nothing");
            Assert.That(FilledWaves(core), Is.EqualTo(waves));
            Assert.That(Error(Slice(machine, pattern, "warp_markers")), Does.Contain("warp markers"));
            Assert.That(Error(Tool("audio_slice_to_midi", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["by"] = "1/32" })), Does.Contain("by:"));
            Assert.That(Error(Tool("audio_slice_to_midi", new JsonObject { ["machine"] = machine })), Does.Contain("pattern or as slot"));

            // warp markers: two inside the clip are three slices
            Ok(Tool("audio_clip_set", new JsonObject
            {
                ["machine"] = machine, ["pattern"] = pattern,
                ["warp_markers"] = new JsonArray(
                    new JsonObject { ["sample"] = 0.0, ["beat"] = 0.0 }, new JsonObject { ["sample"] = (double)rate, ["beat"] = 2.0 },
                    new JsonObject { ["sample"] = (double)(int)(2.2 * rate), ["beat"] = 4.4 }, new JsonObject { ["sample"] = 3.0 * rate, ["beat"] = 6.0 }),
            }));
            var marked = Ok(Slice(machine, pattern, "warp_markers", false));
            Assert.That((int)marked["count"], Is.EqualTo(3));
            Assert.That(marked["slices"]!.AsArray().Select(s => (long)s!["start"]!), Is.EqualTo(new long[] { 0, rate, (long)(2.2 * rate) }));
            Assert.That(marked["slices"]!.AsArray().Select(s => (double)s!["beat"]!), Is.EqualTo(new[] { 0.0, 2.0, 4.4 }));
        }

        [AvaloniaTest]
        public void ADrumLoopIsCutOnItsKicksSnaresAndHatsAndTheClipSLoopIsTheRegion()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var hits = new List<(string Drum, double At)>();
            for (int bar = 0; bar < 2; bar++)
            {
                double t0 = bar * 2.0;
                hits.Add(("kick", t0)); hits.Add(("hat", t0 + 0.25)); hits.Add(("snare", t0 + 0.5)); hits.Add(("hat", t0 + 0.75));
                hits.Add(("kick", t0 + 1.0)); hits.Add(("hat", t0 + 1.25)); hits.Add(("snare", t0 + 1.5)); hits.Add(("hat", t0 + 1.75));
            }
            var (machine, pattern, clip, rate, _) = Import(core, Wav("beat.wav", ConvertSignals.Drums(hits, 4.0)));
            var o = Ok(Slice(machine, pattern, "transients", apply: false));
            Assert.That((int)o["count"], Is.EqualTo(16), "two bars of eight hits");
            var slices = o["slices"]!.AsArray();
            for (int i = 0; i < 16; i++)
                Assert.That((long)slices[i]!["start"]! * 1000.0 / rate, Is.EqualTo(hits[i].At * 1000).Within(1.0), $"slice {i + 1} ({hits[i].Drum}) starts on its hit");

            // the clip's loop is the region: the second bar only
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["loop"] = true, ["region"] = new JsonObject { ["start_beats"] = 4.0, ["loop_start_beats"] = 4.0, ["loop_end_beats"] = 8.0 } }));
            var looped = Ok(Slice(machine, pattern, "transients", apply: false));
            Assert.That((int)looped["count"], Is.EqualTo(8), "one bar of the two");
            for (int i = 0; i < 8; i++)
                Assert.That((long)looped["slices"]![i]!["start"]! * 1000.0 / rate, Is.EqualTo(hits[8 + i].At * 1000).Within(1.0), $"looped slice {i + 1}");
            Assert.That((double)looped["slices"]![0]!["beat"]!, Is.EqualTo(4.0).Within(0.01));
            var applied = Ok(Slice(machine, pattern, "transients"));
            var midi = MachineNamed(core, (string)applied["machine"]).PatternsList.Single(p => !core.SessionLayout.IsAudioClip(p));
            var settings = core.SessionLayout.ClipOf(midi);
            Assert.That(settings.Loop, Is.True, "the same loop");
            Assert.That(settings.LoopStart, Is.EqualTo(4 * core.TPB));
            Assert.That(settings.LoopEnd, Is.EqualTo(8 * core.TPB));
            Assert.That(settings.Audio, Is.Null, "a MIDI clip");
            Assert.That(core.SessionLayout.NotesOf(midi).Count, Is.EqualTo(8));
        }

        [AvaloniaTest]
        public void TheSlicesAreCutFromTheAudioAsTheClipPlaysItGainReverseAndFadesAndTheKitFollowsTheTranspose()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            // a steady tone cut into beats: not on a zero crossing, so the fades show
            var tone = new float[2 * FileRate];
            for (int i = 0; i < tone.Length; i++) tone[i] = (float)(0.5 * Math.Sin(2 * Math.PI * 441 * i / FileRate + 1.0));
            var (machine, pattern, clip, rate, _) = Import(core, Wav("tone.wav", tone));
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["gain_db"] = -6.0, ["transpose"] = 3.0, ["detune"] = 20.0 }));
            var o = Ok(Slice(machine, pattern, "1/4"));
            Assert.That((int)o["count"], Is.EqualTo(4));
            var source = core.SessionLayout.AudioSourceOf(clip)!.Left;
            var w0 = WaveOf(core.SongCore.Wavetable.Waves[(int)o["slices"]![0]!["slot"]! - 1]);
            float gain = (float)Math.Pow(10, -6.0 / 20);
            int one = (int)Math.Round(0.001 * rate), two = (int)Math.Round(0.002 * rate);
            Assert.That(w0.Length, Is.EqualTo(rate / 2).Within(1));
            for (int i = one + 5; i < w0.Length - two - 5; i += 97)
                Assert.That(w0[i], Is.EqualTo(source[i] * gain).Within(1e-6), $"sample {i} is the audio times the clip's gain");
            Assert.That(Math.Abs(w0[0]), Is.LessThan(0.02f), "a fade in of 1 ms");
            Assert.That(Math.Abs(w0[^1]), Is.LessThan(0.02f), "a fade out of 2 ms");
            Assert.That(Math.Abs(w0[one / 2]), Is.LessThan(Math.Abs(source[one / 2] * gain)), "still fading in half way");
            var w1 = WaveOf(core.SongCore.Wavetable.Waves[(int)o["slices"]![1]!["slot"]! - 1]);
            Assert.That(Math.Abs(w1[0]), Is.LessThan(0.02f), "every slice has its fade in");

            // the clip's transpose and detune retune the zones (the slices aren't warped)
            var sampler = MachineNamed(core, (string)o["machine"]);
            Assert.That(Param(sampler, "Z1 tune"), Is.EqualTo(48 + 3));
            Assert.That(Param(sampler, "Z4 fine"), Is.EqualTo(1000 + 20 * 10));
            core.SongCore.ActionStack.Undo();

            // reverse: the slices are cut from the audio backwards, the first of them the end of the audio reversed
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["gain_db"] = 0.0, ["transpose"] = 0.0, ["detune"] = 0.0, ["reverse"] = true }));
            var back = Ok(Slice(machine, pattern, "1/4"));
            var r0 = WaveOf(core.SongCore.Wavetable.Waves[(int)back["slices"]![0]!["slot"]! - 1]);
            for (int i = one + 5; i < r0.Length - two - 5; i += 97)
                Assert.That(r0[i], Is.EqualTo(source[source.Length - 1 - i]).Within(1e-6), $"reversed sample {i}");
            Assert.That(Param(MachineNamed(core, (string)back["machine"]), "Z1 tune"), Is.EqualTo(48), "no transpose");
        }

        [AvaloniaTest]
        public void AWarpedClipAtASongTempoThatIsNotTheLoopsPlacesTheNotesByTheClipsBeatsAndTheSlicesPlayUnwarped()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            Ok(Tool("song_set", new JsonObject { ["tempo_bpm"] = 90 }));
            // the loop's own tempo is 120 BPM: a clip of 4 beats that the song plays at 90
            var (machine, pattern, clip, rate, _) = Import(core, Wav("warped.wav", Clicks(Hits.Select(h => (h, 0.5)), 2.0)), bpm: 120, warp: "Repitch");
            var o = Ok(Slice(machine, pattern));
            Assert.That((int)o["count"], Is.EqualTo(8));
            var notes = core.SessionLayout.NotesOf(MachineNamed(core, (string)o["machine"]).PatternsList.Single(p => !core.SessionLayout.IsAudioClip(p)));
            double ticksPerClipSecond = 120 / 60.0 * core.TPB * SessionRig.TB;           // the clip's beats: the audio's own tempo, not the song's
            for (int i = 0; i < 8; i++)
                Assert.That(notes[i].Time, Is.EqualTo(Hits[i] * ticksPerClipSecond).Within(0.002 * ticksPerClipSecond), $"note {i + 1}");

            // in the render the slices start where the song's beats put them (a beat is 2/3 s at 90 BPM), each at its own speed
            var render = Ok(Tool("render", new JsonObject { ["from"] = 0, ["to"] = 4, ["tail_seconds"] = 0, ["solo"] = new JsonArray((string)o["machine"]) }));
            var audio = Wav_Read((string)render["file"]!);
            double secondsPerBeat = 60.0 / 90;
            for (int i = 0; i < 8; i++)
            {
                double expected = Hits[i] * 2 * secondsPerBeat;
                int from = (int)((expected - 0.004) * audio.Rate), to = (int)((expected + 0.006) * audio.Rate);
                int first = -1;
                for (int k = Math.Max(0, from); k < to && k < audio.Left.Length; k++) if (Math.Abs(audio.Left[k]) > 0.02f) { first = k; break; }
                Assert.That(first, Is.GreaterThanOrEqualTo(0), $"slice {i + 1} sounds near beat {Hits[i] * 2:0.00}");
                double dev = (first - expected * audio.Rate) / audio.Rate * 1000;
                TestContext.Out.WriteLine($"[notice] slice {i + 1}: sounds {dev:0.00} ms from its beat");
                Assert.That(dev, Is.InRange(-6.0, 3.5), $"slice {i + 1} starts on its beat, the notes sit on the engine's sub-tick grid (ms)");
            }
        }

        static StereoAudio Wav_Read(string file) => ReBuzz.Agent.Audio.Wav.Read(file);

        // ------------------------------------------------------------------ the null test

        [AvaloniaTest]
        public void AtTheLoopSOwnTempoTheNewTracksRenderMatchesTheSourceClipsOutsideThreeMillisecondsAroundEachSliceEdge() => NullTest(false);

        [AvaloniaTest]
        public void TheNullTestHoldsForAStereoLoopToo() => NullTest(true);

        static void NullTest(bool stereo)
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            Ok(Tool("song_set", new JsonObject { ["tempo_bpm"] = 120 }));
            var hits = new List<(string Drum, double At)>();
            for (int bar = 0; bar < 2; bar++)
            {
                double t0 = bar * 2.0;
                hits.Add(("kick", t0)); hits.Add(("hat", t0 + 0.25)); hits.Add(("snare", t0 + 0.5)); hits.Add(("hat", t0 + 0.75));
                hits.Add(("kick", t0 + 1.0)); hits.Add(("hat", t0 + 1.25)); hits.Add(("snare", t0 + 1.5)); hits.Add(("open", t0 + 1.75));
            }
            var (machine, pattern, clip, rate, _) = Import(core, Wav(stereo ? "null-stereo.wav" : "null.wav", ConvertSignals.Drums(hits, 4.0), stereo: stereo), bpm: 120, warp: "Off");
            var o = Ok(Slice(machine, pattern));
            TestContext.Out.WriteLine("[notice] starts (ms): " + string.Join(" ", o["slices"]!.AsArray().Select(x => ((long)x!["start"]! * 1000.0 / rate).ToString("0.0"))));
            Assert.That((int)o["count"], Is.EqualTo(16));
            string slicesTrack = (string)o["machine"];

            string Render(string solo)
            {
                var other = solo == machine ? slicesTrack : machine;
                var r = Ok(Tool("render", new JsonObject { ["from"] = 0, ["to"] = 8, ["tail_seconds"] = 0, ["solo"] = new JsonArray(solo), ["mute"] = new JsonArray(other), ["name"] = "null-" + solo.Replace(' ', '-') }));
                return (string)r["file"]!;
            }
            var source = Wav_Read(Render(machine));
            var played = Wav_Read(Render(slicesTrack));
            Assert.That(source.Rate, Is.EqualTo(played.Rate));
            int rateOut = source.Rate, guard = (int)Math.Round(0.003 * rateOut), end = 4 * rateOut;
            Assert.That(Math.Min(source.Left.Length, played.Left.Length), Is.GreaterThanOrEqualTo(end));
            var edges = o["slices"]!.AsArray().Select(x => (long)((long)x!["start"]! / (double)rate * rateOut)).Append(end).ToList();
            double sig = 0, err = 0, worstSlice = double.MinValue;
            int exact = 0, maxLag = 0;
            for (int k = 0; k + 1 < edges.Count; k++)
            {
                // The engine starts a note on its sub-tick grid (about 6 ms) and a tick's start is a whole number of samples: a slice can sound a few
                // samples to a few milliseconds from its place. What is measured is the replay itself: each slice against the source at its own offset.
                int lag = 0;
                double best = double.MinValue;
                for (int l = -400; l <= 400; l++)
                {
                    double c = 0;
                    for (long i = edges[k] + guard + 400; i < edges[k + 1] - guard - 400; i += 2) c += source.Left[i] * (double)played.Left[i + l];
                    if (c > best) { best = c; lag = l; }
                }
                if (Math.Abs(lag) <= 1) exact++;
                maxLag = Math.Max(maxLag, Math.Abs(lag));
                double es = 0, ee = 0;
                for (long i = edges[k] + guard + Math.Abs(lag); i < edges[k + 1] - guard - Math.Abs(lag); i++)
                {
                    double a = source.Left[i], b = played.Left[i + lag];
                    es += a * a;
                    ee += (a - b) * (a - b);
                }
                sig += es; err += ee;
                double dbk = 10 * Math.Log10(Math.Max(ee, 1e-30) / Math.Max(es, 1e-30));
                worstSlice = Math.Max(worstSlice, dbk);
                TestContext.Out.WriteLine($"[notice] slice {k + 1} at {edges[k] * 1000.0 / rateOut:0.0} ms: offset {lag} samples, residual {dbk:0.0} dB");
            }
            double residual = 10 * Math.Log10(Math.Max(err, 1e-30) / sig);
            TestContext.Out.WriteLine($"[notice] Slice null test ({(stereo ? "stereo" : "mono")}): residual {residual:0.0} dB overall, worst slice {worstSlice:0.0} dB, {exact} of {edges.Count - 1} slices exactly where the source has them, the largest offset {maxLag} samples ({maxLag * 1000.0 / rateOut:0.0} ms)");
            Assert.That(sig, Is.GreaterThan(0), "the source plays");
            Assert.That(maxLag, Is.LessThanOrEqualTo(300), "no slice more than a sub-tick from its place");
            Assert.That(exact, Is.GreaterThanOrEqualTo(10), "most slices sound exactly where the source has them");
            Assert.That(worstSlice, Is.LessThan(-50), "every slice replays its part of the loop, outside 3 ms around its edges");
        }

        // ------------------------------------------------------------------ save and reopen

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        [AvaloniaTest]
        public void TheSlicesSurviveSaveAndReopenAsNbl()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, pattern, clip, rate, imported) = Import(core, Wav("saved.wav", Clicks(Hits.Select(h => (h, 0.5)), 2.0)));
            var o = Ok(Slice(machine, pattern));
            string sampler = (string)o["machine"], midiName = (string)o["pattern"];
            var slots = o["slices"]!.AsArray().Select(s => (int)s!["slot"]!).ToArray();
            var notesBefore = core.SessionLayout.NotesOf(MachineNamed(core, sampler).PatternsList.Single(p => p.Name == midiName));
            var wave0 = WaveOf(core.SongCore.Wavetable.Waves[slots[0] - 1]);

            void Check(string what)
            {
                var m = MachineNamed(core, sampler);
                Assert.That(m.DLL.Name, Is.EqualTo("Neo Sampler"), what);
                Assert.That(Param(m, "Mode"), Is.EqualTo(1), what + ": Kit");
                for (int z = 1; z <= 8; z++)
                {
                    Assert.That(Param(m, $"Z{z} wave"), Is.EqualTo(slots[z - 1]), $"{what}: zone {z}");
                    Assert.That(Param(m, $"Z{z} choke"), Is.EqualTo(1), $"{what}: zone {z} choke");
                    var w = core.SongCore.Wavetable.Waves[slots[z - 1] - 1];
                    Assert.That(w, Is.Not.Null, $"{what}: wave {slots[z - 1]}");
                    Assert.That(w.Name, Is.EqualTo($"{pattern} {z:00}"), what);
                }
                var back = WaveOf(core.SongCore.Wavetable.Waves[slots[0] - 1]);
                Assert.That(back.Length, Is.EqualTo(wave0.Length), what);
                for (int i = 0; i < back.Length; i += 53) Assert.That(back[i], Is.EqualTo(wave0[i]).Within(1e-4), $"{what}: sample {i}");
                var notes = core.SessionLayout.NotesOf(m.PatternsList.Single(p => p.Name == midiName));
                Assert.That(notes, Is.EqualTo(notesBefore), what + ": the same notes");
                var layout = core.SessionLayout;
                Assert.That(layout.PatternAt(layout.FirstTrackOf(m), (int)imported["slot"] - 1)?.Name, Is.EqualTo(midiName), what + ": still in the scene");
            }

            string nbl = Work("sliced.nbl");
            core.SaveSongCopy(nbl);
            Reopen(core, nbl);
            Check(".nbl");
        }

        // ------------------------------------------------------------------ the menus and the dialog

        [AvaloniaTest]
        public void TheMenuOffersSliceNextToConvertAndTheDialogShowsTheCountAndDisablesWhatIsOverTheLimit()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; SliceFlow.Responder = null; core.Modified = false; });
            var (machine, pattern, clip, rate, imported) = Import(core, Wav("flow.wav", Clicks(Hits.Select(h => (h, 0.5)), 2.0)));
            var view = rig.Show();
            var layout = core.SessionLayout;
            int track = layout.FirstTrackOf(MachineNamed(core, machine));
            view.ShowSlotMenu(view.SlotAt(track, (int)imported["slot"] - 1));
            var items = view.LastMenu.Items.OfType<MenuItem>().Select(i => i.Tag as string).ToList();
            Assert.That(items, Does.Contain("Convert to MIDI"));
            Assert.That(items.IndexOf("Slice to new MIDI track"), Is.EqualTo(items.IndexOf("Convert to MIDI") + 1), "next to Convert to MIDI");
            Assert.That(view.LastMenu.Items.OfType<MenuItem>().Single(i => i.Tag as string == "Slice to new MIDI track").Header, Is.EqualTo("Slice to new MIDI track…"));

            // the dialog: six choices, the count of slices, the tooltip of a disabled one
            var source = layout.SliceSourceOfClip(clip, out string error);
            Assert.That(source, Is.Not.Null, error);
            var dialog = new SliceFlow.SliceWindow(layout, source!);
            dialog.Show();
            Pump(dialog.Ready);
            rig.Settle();
            try
            {
                var choices = dialog.ByBox.Items.OfType<ComboBoxItem>().ToList();
                Assert.That(choices.Select(c => (string)c.Tag!.ToString()!), Is.EqualTo(new[] { "Transients", "WarpMarkers", "Bar", "Quarter", "Eighth", "Sixteenth" }));
                Assert.That(dialog.ByBox.SelectedIndex, Is.EqualTo(0), "Transients is the default");
                Assert.That(dialog.CountText.Text, Does.StartWith("8 slices"));
                Assert.That(choices.Select(c => c.IsEnabled), Is.EqualTo(new[] { true, false, false, true, true, true }), "warp markers: none inside; a bar is the whole clip: one slice; 1/16 of 4 beats is 16, allowed");
                Assert.That(ToolTip.GetTip(choices[1]) as string, Does.Contain("warp markers"));
                Assert.That(dialog.GoButton.IsEnabled, Is.True);
                dialog.ByBox.SelectedIndex = 5;
                rig.Settle();
                Assert.That(dialog.CountText.Text, Does.StartWith("16 slices"));
            }
            finally { dialog.Close(); }

            // 8 beats: 1/16 would make 32
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["tempo_bpm"] = 240.0 }));
            var longer = layout.SliceSourceOfClip(clip, out error)!;
            var big = new SliceFlow.SliceWindow(layout, longer);
            big.Show();
            Pump(big.Ready);
            try
            {
                var choices = big.ByBox.Items.OfType<ComboBoxItem>().ToList();
                Assert.That(choices[5].IsEnabled, Is.False, "1/16 would make 32 slices");
                Assert.That(ToolTip.GetTip(choices[5]) as string, Does.Contain("32"), "the tooltip shows the count");
                Assert.That(choices[5].Content as string, Does.Contain("32"));
                Assert.That(choices[4].IsEnabled, Is.True, "1/8 makes 16");
            }
            finally { big.Close(); }

            // the flow
            Ok(Tool("audio_clip_set", new JsonObject { ["machine"] = machine, ["pattern"] = pattern, ["tempo_bpm"] = 120.0 }));
            string asked = null;
            SliceFlow.Responder = name => { asked = name; return SliceBy.Quarter; };
            int machines = Visible(core);
            Pump(SliceFlow.SliceClip(clip));
            Assert.That(asked, Is.EqualTo(pattern));
            Assert.That(SliceFlow.LastOutcome, Is.Not.Null, SliceFlow.LastSummary);
            Assert.That(SliceFlow.LastOutcome.Slices.Count, Is.EqualTo(4));
            Assert.That(SliceFlow.LastSummary, Does.Contain("Neo Sampler"));
            Assert.That(Visible(core), Is.EqualTo(machines + 1));

            SliceFlow.Responder = _ => null;
            Pump(SliceFlow.SliceClip(clip));
            Assert.That(Visible(core), Is.EqualTo(machines + 1), "cancel changes nothing");

            // a clip that isn't audio is refused with the reason
            SliceFlow.Responder = _ => throw new InvalidOperationException("must not ask");
            Dialogs.Responder = (m, c, b) => UserAnswer.Ok;
            Pump(SliceFlow.SliceClip(SliceFlow.LastOutcome.Pattern));
            Assert.That(SliceFlow.LastSummary, Does.Contain("isn't an audio clip"));
            Assert.That(Visible(core), Is.EqualTo(machines + 1));
        }

        [AvaloniaTest]
        public void TheWavetableRunningOutOfSlotsIsSaidAndNothingChanges()
        {
            using var untitled = SnapshotTests.Untitled(out _);
            using var rig = new SessionRig();
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var (machine, pattern, clip, rate, _) = Import(core, Wav("full.wav", Clicks(Hits.Select(h => (h, 0.5)), 2.0)));
            var table = core.SongCore.Wavetable;
            var filler = new List<int>();
            // fill all but five slots with a one-sample wave
            for (int i = 0; i < 195; i++)
            {
                core.SongCore.WavetableCore.AllocateWave(i, "", "filler", 16, WaveFormat.Float32, false, 65, false, false);
                filler.Add(i);
            }
            try
            {
                int machines = Visible(core);
                string why = Error(Slice(machine, pattern));
                Assert.That(why, Does.Contain("5 free slots").And.Contain("need 8"), why);
                Assert.That(Visible(core), Is.EqualTo(machines));
            }
            finally
            {
                foreach (int i in filler) table.LoadWave(i, null, null, false);
            }
        }
    }
}
