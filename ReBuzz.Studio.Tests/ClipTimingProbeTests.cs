// Fork (docs/specs/clips.md §5.6, wave CU1): the timing probe. A value that sits on a row can be played by the
// engine (an engine column: the block that holds it, before CallTick) or by the Modern Pattern Editor (its own
// data, from its Work, after CallTick). This measures, to the sample, whether they land together.
//
// The probe machine (TestMachines/ClipProbe) puts what it is told on its output, so the audio shows where a value
// arrived: the first sample where it changes. Per case a pattern holds one event, on a tick boundary (a row, at 4 rows
// a beat the editor's row is a tick), and the engine renders it as a device's thread does (a WorkManager, calls of the
// block size given). For each of
//   - a classic state parameter (Level, set at the tick),
//   - a classic Note track parameter,
//   - a MIDI note (the engine's MIDI column against the editor's MIDI note lane),
// the event is played by each player, at 64, 256 and 1024 frames a call, at 120 and 125 BPM (tick lengths 5512.5
// and 5292 samples), on a tick that starts inside a call and on one that starts at the edge of one. Each result
// is the sample of the first change, the tick's start, and the lag of the editor behind the engine.
//
// The table is written to the test log and, when CLIP_PROBE_OUT names a file, to it. The tests assert only that
// the probe works (the engine lands on the tick's first sample and every value arrives); how far the editor lags
// is the result, not a pass or fail.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Headless.NUnit;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Audio;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Studio.Tests
{
    public class ClipTimingProbeTests
    {
        const int TB = PatternEvent.TimeBase;
        static ReBuzzCore Core => Studio.Host.Core;

        enum What { Level, Note, Midi }
        enum Who { Engine, Migrated }

        sealed class Landing
        {
            public int Sample = -1;             // the first sample where the output moved; -1: it never did
            public float Peak;
            public int Ons;                     // the MIDI note-ons the machine took (the probe counts them)
            public int[] TickStart;             // where each tick began (as the engine's tick lengths give them)
        }

        sealed class Row
        {
            public What What;
            public int Bpm, Tpb, Block, Tick;
            public bool Edge;
            public double TickSamples;
            public int Expected, Engine;
        }

        /// <summary>The start of each tick, in samples, as the engine cuts them (WorkManager: whole samples, the fraction carried).</summary>
        static int[] TickStarts(double averageSamplesPerTick, int ticks)
        {
            var starts = new int[ticks + 1];
            double carry = 0;
            long at = 0;
            for (int i = 0; i < ticks; i++)
            {
                starts[i] = (int)at;
                double exact = averageSamplesPerTick + carry;
                int samples = Math.Max(1, (int)Math.Floor(exact));
                carry = Math.Clamp(exact - samples, 0, 0.999999);
                at += samples;
            }
            starts[ticks] = (int)at;
            return starts;
        }

        /// <summary>The probe's pattern with one event on tick <paramref name="tick"/>, played by <paramref name="who"/>, and the song rendered in calls of <paramref name="block"/> frames.</summary>
        static Landing Play(What what, Who who, int bpm, int tpb, int tick, int block, int ticks)
        {
            AudioHygieneTests.NewSong();
            var core = Core;
            var song = core.SongCore;
            core.BPM = bpm;
            core.TPB = tpb;
            int time = tick * TB;
            int midi = 60, velocity = 100, buzzNote = BuzzNote.FromMIDINote(midi);
            // The editor reads its data when its machine is made, as on a song's load: Level is the first global parameter, Note
            // the first track parameter, which the editor counts after the globals (index 1).
            byte[] editorData = null;
            if (who == Who.Migrated)
            {
                var columns = what switch
                {
                    What.Level => new[] { ("Probe", 0, 0, new[] { (time, 500) }) },
                    What.Note => new[] { ("Probe", 1, 0, new[] { (time, buzzNote) }) },
                    _ => new[] { ("Probe", -128, 0, new[] { (time, buzzNote) }), ("Probe", -129, 0, new[] { (time, velocity) }) },
                };
                editorData = EditorData("P", (ticks + 3) / 4, columns);
            }
            // A song's machine is made while it loads (the editor is given the data of the file, and keeps it for the migration).
            bool outer = core.SongLoading;
            core.SongLoading = who == Who.Migrated;
            try { song.CreateMachine("Clip Probe", null, "Probe", null, "Modern Pattern Editor", editorData, -1, 0.3f, 0.3f); }
            finally { core.SongLoading = outer; }
            var machine = song.MachinesList.First(m => m.Name == "Probe");
            song.ConnectMachines(machine, song.MachinesList[0], new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            Assert.That(machine.EditorMachine, Is.Not.Null, "the probe has a pattern editor (the Modern Pattern Editor)");
            machine.CreatePattern("P", ticks);
            var pattern = machine.Patterns.First(p => p.Name == "P");
            var level = machine.ParameterGroupsList[1].ParametersList.First(p => p.Name == "Level");
            var note = machine.ParameterGroupsList[2].ParametersList.First(p => p.Name == "Note");
            Assert.That((level.IndexInGroup, note.IndexInGroup, machine.ParameterGroupsList[1].ParametersList.Count), Is.EqualTo((0, 0, 1)));

            if (who == Who.Engine)
            {
                switch (what)
                {
                    case What.Level:
                        SessionLayout.ColumnFor(pattern, new ColumnKey(1, level.IndexInGroup, 0), create: true).SetEvents(new[] { new PatternEvent(time, 500) }.ToList(), true);
                        break;
                    case What.Note:
                        SessionLayout.ColumnFor(pattern, new ColumnKey(2, note.IndexInGroup, 0), create: true).SetEvents(new[] { new PatternEvent(time, buzzNote) }.ToList(), true);
                        break;
                    case What.Midi:
                        // The engine's MIDI column: a note-on message (status | note << 8 | velocity << 16) with its length.
                        SessionLayout.EnsureMidiColumn(pattern).SetEvents(new[] { new PatternEvent(time, 0x90 | (midi << 8) | (velocity << 16), TB * 2) }.ToList(), true);
                        break;
                }
            }
            else
            {
                // Migration on open: the editor's data becomes lanes of the pattern, and nothing else plays it.
                ReBuzz.Core.ClipOpen.Run(core, false);
                int held = pattern.Columns.Sum(c => c.GetEvents(int.MinValue, int.MaxValue).Count());
                Assert.That(held, Is.GreaterThan(0), $"the lanes hold the data ({what}): {string.Join(", ", pattern.Columns.Select(c => c.Type + ":" + c.GetEvents(int.MinValue, int.MaxValue).Count()))}");
            }

            song.AddSequence(machine, song.Sequences.Count);
            song.Sequences.Last().SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, pattern));

            var landing = new Landing();
            int total = 0;
            var left = new List<float>();
            var right = new List<float>();
            var buffer = new float[block * 2];
            lock (ReBuzzCore.AudioLock)
            {
                var wm = new WorkManager(core, new WorkThreadEngine(1), 0, core.EngineSettings);
                try
                {
                    core.Playing = false;
                    core.Looping = false;
                    song.LoopStart = 0;
                    song.LoopEnd = ticks;
                    song.SongEnd = ticks;
                    song.PlayPosition = 0;
                    ReBuzzCore.masterInfo.PosInTick = 0;
                    var sub = ReBuzzCore.subTickInfo;
                    sub.PosInSubTick = 0;
                    sub.CurrentSubTick = 0;
                    sub.SubTickReminderCounter = 0;
                    core.Playing = true;
                    var info = ReBuzzCore.masterInfo;
                    var small = new float[512];
                    for (int guard = 0; !core.Playing && guard < 100000; guard++)
                    {
                        int pos = info.PosInTick;
                        int n = pos == 0 ? 1 : Math.Clamp(info.SamplesPerTick - pos, 1, 256);
                        wm.MainAudioFillBuffer(small, 0, n * 2);
                    }
                    Assert.That(core.Playing, Is.True, "playing");
                    landing.TickStart = TickStarts(60.0 * info.SamplesPerSec / (bpm * tpb), ticks);
                    int want = landing.TickStart[Math.Min(ticks, tick + 3)] + 2 * block;
                    while (total < want)
                    {
                        wm.MainAudioFillBuffer(buffer, 0, block * 2);
                        for (int i = 0; i < block; i++)
                        {
                            left.Add(buffer[2 * i]);
                            right.Add(buffer[2 * i + 1]);
                        }
                        total += block;
                    }
                }
                finally
                {
                    core.Playing = false;
                    wm.Stop();
                }
            }

            landing.Ons = right.Count == 0 ? 0 : (int)Math.Round(right.Max() * 32768f / 100000f);       // the master scales the samples to the range -1..1
            landing.Peak = right.Count == 0 ? 0 : right.Max();
            var channel = what == What.Level ? left : right;
            for (int i = 0; i < channel.Count; i++)
            {
                if (Math.Abs(channel[i]) > 1e-9f)
                {
                    landing.Sample = i;
                    break;
                }
            }
            AudioHygieneTests.NewSong();
            return landing;
        }

        /// <summary>The Modern Pattern Editor's data (PatternEditorUtils: 255, version 1, size, patterns) for one pattern at 4 rows a beat.</summary>
        static byte[] EditorData(string pattern, int beats, IEnumerable<(string Machine, int Index, int Track, (int Time, int Value)[] Events)> columns)
        {
            var body = new MemoryStream();
            using (var w = new BinaryWriter(body, Encoding.ASCII, leaveOpen: true))
            {
                w.Write(1);
                w.Write(Encoding.ASCII.GetBytes(pattern));
                w.Write((byte)0);
                var list = columns.ToList();
                w.Write(beats);
                w.Write(4);
                w.Write(list.Count);
                foreach (var c in list)
                {
                    w.Write(Encoding.ASCII.GetBytes(c.Machine));
                    w.Write((byte)0);
                    w.Write(c.Index);
                    w.Write(c.Track);
                    w.Write((byte)0);
                    w.Write(c.Events.Length);
                    foreach (var (t, v) in c.Events)
                    {
                        w.Write(t);
                        w.Write(v);
                    }
                    for (int b = 0; b < beats; b++) w.Write(4);
                }
            }
            var data = new MemoryStream();
            using (var w = new BinaryWriter(data, Encoding.ASCII, leaveOpen: true))
            {
                w.Write((byte)255);
                w.Write((byte)1);
                w.Write((int)(body.Length + 6));
                w.Write(body.ToArray());
            }
            return data.ToArray();
        }

        /// <summary>Ticks of a tempo that begin inside a call of <paramref name="block"/> frames, and at its edge: the first of each at or after tick 3.</summary>
        static (int Inside, int Edge) Pick(int[] starts, int block, int limit)
        {
            int inside = -1, edge = -1;
            for (int t = 3; t < limit && (inside < 0 || edge < 0); t++)
            {
                int m = starts[t] % block;
                if (m == 0 && edge < 0) edge = t;
                else if (m >= block / 4 && m <= block * 3 / 4 && inside < 0) inside = t;
            }
            return (inside, edge);
        }

        /// <summary>CU3 (clips.md §5.6, the double-play rule): a note in the MIDI column sounds once, whether it was written to the column or came from a song's editor data on open.</summary>
        [AvaloniaTest]
        public void ANoteInTheMidiColumnSoundsOnceWhetherWrittenOrMigrated()
        {
            foreach (var who in new[] { Who.Engine, Who.Migrated })
                foreach (var block in new[] { 64, 512 })
                {
                    var landing = Play(What.Midi, who, 125, 4, 9, block, 15);
                    Assert.That(landing.Ons, Is.EqualTo(1), $"the machine took the note-on once ({who}, {block} frames; peak {landing.Peak}, first sound at {landing.Sample})");
                    Assert.That(landing.Sample, Is.GreaterThanOrEqualTo(0), $"and it sounded ({who})");
                }
            foreach (var what in new[] { What.Level, What.Note })
            {
                var migrated = Play(what, Who.Migrated, 125, 4, 9, 256, 15);
                Assert.That(migrated.Sample, Is.GreaterThanOrEqualTo(0), $"a migrated {what} lane is played by the engine");
            }
        }

        [AvaloniaTest]
        public void TheEngineLandsARowAlignedValueOfEveryLaneAtItsTick()
        {
            Assert.That(Core.MachineDLLs.ContainsKey("Clip Probe"), Is.True, "the probe's machine is in the tests' gear (TestMachines/ClipProbe.csproj)");
            var rows = new List<Row>();
            foreach (var (bpm, tpb) in new[] { (120, 4), (125, 4) })
            {
                double average = 60.0 * ReBuzzCore.masterInfo.SamplesPerSec / (bpm * tpb);
                var starts = TickStarts(average, 700);
                foreach (int block in new[] { 64, 256, 1024 })
                {
                    var (inside, edge) = Pick(starts, block, 690);
                    foreach (bool atEdge in new[] { false, true })
                    {
                        int tick = atEdge ? edge : inside;
                        if (tick < 0) continue;
                        foreach (var what in new[] { What.Level, What.Note, What.Midi })
                        {
                            int ticks = tick + 6;
                            var engine = Play(what, Who.Engine, bpm, tpb, tick, block, ticks);
                            rows.Add(new Row
                            {
                                What = what, Bpm = bpm, Tpb = tpb, Block = block, Tick = tick, Edge = atEdge, TickSamples = average,
                                Expected = engine.TickStart[tick], Engine = engine.Sample,
                            });
                        }
                    }
                }
            }

            var text = new StringBuilder();
            text.AppendLine("Timing probe (docs/specs/clips.md §5.6): a row-aligned value, the engine's column against the editor's data");
            text.AppendLine($"sample rate {ReBuzzCore.masterInfo.SamplesPerSec} Hz, sub-tick timing {Core.EngineSettings.SubTickTiming}");
            text.AppendLine("value | bpm/tpb | call | tick (start in call) | tick start | engine lands | engine - tick start (samples, ticks)");
            foreach (var r in rows)
            {
                string lag = r.Engine < 0 ? "never" : $"{r.Engine - r.Expected} ({(r.Engine - r.Expected) / r.TickSamples:0.00})";
                text.AppendLine($"{r.What,-5} | {r.Bpm}/{r.Tpb} | {r.Block,4} | {r.Tick,3} ({(r.Edge ? "edge" : "inside")}) | {r.Expected,7} | {r.Engine,7} | {lag}");
            }
            TestContext.Out.WriteLine(text.ToString());
            string path = Environment.GetEnvironmentVariable("CLIP_PROBE_OUT");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, text.ToString());

            foreach (var r in rows)
            {
                string at = $"{r.What} at {r.Bpm} BPM, {r.Block} frames, tick {r.Tick}";
                Assert.That(r.Engine, Is.GreaterThanOrEqualTo(r.Expected), $"the engine's column lands at or after the tick's first sample ({at})");
                Assert.That(r.Engine, Is.LessThan(r.Expected + (int)r.TickSamples), $"and within the tick ({at}): CU3 plays every lane in the engine, so there is one landing to measure");
            }
        }
    }
}
