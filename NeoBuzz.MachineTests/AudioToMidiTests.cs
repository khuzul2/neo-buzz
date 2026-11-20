// Neo Audio to MIDI (docs/specs/audio-to-midi.md §7): the engine measured on synthetic instruments (Karplus-Strong guitar and bass with plucks, re-plucks,
// hammer-ons and slides; a saw and a sine lead with vibrato) and drum loops with ghost notes, whose truth is known. Each test prints its figures (the
// spec and the report quote them). Notes score by onset within 50 ms and the exact note; latency is the note on's time less the truth's.

using System.Globalization;
using NeoBuzz.AudioToMidi;
using NeoBuzz.Dsp;
using static NeoBuzz.MachineTests.AudioToMidiSignals;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class AudioToMidiTests
    {
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

        sealed record Out(double T, MidiKind Kind, MidiStream Stream, int Channel, int Data1, int Data2, int Lag = 0);

        static (AudioMidiEngine E, ParamValues V) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Monitor, 0);
            setup?.Invoke(v);
            var e = new AudioMidiEngine(v);
            e.SetSampleRate((int)Rate);
            return (e, v);
        }

        static List<Out> Play(AudioMidiEngine e, float[] x, int block = 256)
        {
            var list = new List<Out>();
            var buf = new float[AudioMidiEngine.MaxBlock];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                Array.Copy(x, pos, buf, 0, n);
                int count = e.Process(buf, n);
                for (int i = 0; i < count; i++) { var m = e.Events[i]; list.Add(new Out((pos + m.Offset) / Rate, m.Kind, m.Stream, m.Channel, m.Data1, m.Data2, m.Lag)); }
            }
            int c = e.Flush();
            for (int i = 0; i < c; i++) { var m = e.Events[i]; list.Add(new Out((x.Length + m.Offset) / Rate, m.Kind, m.Stream, m.Channel, m.Data1, m.Data2, m.Lag)); }
            return list;
        }

        static List<(double T, int Note, int Vel)> NoteOns(List<Out> evs, MidiStream stream = MidiStream.Pitch) =>
            evs.Where(m => m.Stream == stream && m.Kind == MidiKind.NoteOn).Select(m => (m.T, m.Data1, m.Data2)).ToList();

        /// <summary>The note ons as a recording track places them (Record at attack): the time less the event's lag.</summary>
        static List<(double T, int Note, int Vel)> RecordedNoteOns(List<Out> evs, MidiStream stream = MidiStream.Pitch) =>
            evs.Where(m => m.Stream == stream && m.Kind == MidiKind.NoteOn).Select(m => (m.T - m.Lag / Rate, m.Data1, m.Data2)).ToList();

        /// <summary>Scores the notes: onset within <paramref name="tol"/> s and the same note. Matching is one to one, nearest in time first.</summary>
        sealed class Score
        {
            public int Truth, Output, Hit, OctaveErrors;
            public List<double> Latency = new();
            public double P => Output == 0 ? 0 : Hit / (double)Output;
            public double R => Truth == 0 ? 0 : Hit / (double)Truth;
            public double FMeasure => P + R == 0 ? 0 : 2 * P * R / (P + R);
            public double Minutes;
            public double ChatterPerMinute => Minutes <= 0 ? 0 : (Output - Hit - OctaveErrors) / Minutes;
            public double Median => Latency.Count == 0 ? double.NaN : Latency.OrderBy(v => v).ElementAt(Latency.Count / 2);
            public double Worst => Latency.Count == 0 ? double.NaN : Latency.Max();
            public double FWide;
            public override string ToString() => $"F {F(FMeasure * 100)} % at 50 ms, {F(FWide * 100)} % at 100 ms (P {F(P * 100)}, R {F(R * 100)}), latency median {F(Median * 1000)} ms worst {F(Worst * 1000)} ms, octave errors {OctaveErrors}, chatter {F(ChatterPerMinute)} /min";
        }

        static Score Match(List<(double T, int Note)> truth, List<(double T, int Note, int Vel)> outs, double seconds, double tol = 0.05)
        {
            var s = new Score { Truth = truth.Count, Output = outs.Count, Minutes = seconds / 60 };
            var used = new bool[outs.Count];
            foreach (var (t, note) in truth)
            {
                int best = -1; double bd = double.MaxValue;
                for (int i = 0; i < outs.Count; i++)
                {
                    if (used[i] || outs[i].Note != note) continue;
                    double d = Math.Abs(outs[i].T - t);
                    if (d <= tol && d < bd) { bd = d; best = i; }
                }
                if (best >= 0) { used[best] = true; s.Hit++; s.Latency.Add(outs[best].T - t); }
            }
            // An output that is the truth's note an octave out (or two).
            foreach (var (t, note) in truth)
                for (int i = 0; i < outs.Count; i++)
                    if (!used[i] && Math.Abs(outs[i].T - t) <= tol && (outs[i].Note - note) % 12 == 0 && outs[i].Note != note) { used[i] = true; s.OctaveErrors++; break; }
            return s;
        }

        static Score Merge(IEnumerable<Score> parts)
        {
            var m = new Score();
            foreach (var p in parts) { m.Truth += p.Truth; m.Output += p.Output; m.Hit += p.Hit; m.OctaveErrors += p.OctaveErrors; m.Latency.AddRange(p.Latency); m.Minutes += p.Minutes; }
            return m;
        }

        static Score RunLine(Func<ulong, (float[] X, List<(double T, int Note)> Truth)> make, ulong[] seeds, Action<ParamValues> setup, string label)
        {
            var parts = new List<Score>();
            foreach (ulong seed in seeds)
            {
                var (x, truth) = make(seed);
                var (e, _) = Make(setup);
                var evs = Play(e, x);
                parts.Add(Match(truth, NoteOns(evs), x.Length / Rate));
            }
            var total = Merge(parts);
            var wide = Merge(seeds.Select(seed => { var (x, truth) = make(seed); var (e, _) = Make(setup); return Match(truth, NoteOns(Play(e, x)), x.Length / Rate, 0.1); }));
            total.FWide = wide.FMeasure;
            Say($"{label}: {total}");
            return total;
        }

        static readonly ulong[] Seeds = { 1, 2, 3 };

        // ---------------------------------------------------------------- melody: the lines

        [TestCase(0, "Guitar, Fast", 0.80, 0.020)]
        [TestCase(1, "Guitar, Stable", 0.80, 0.070)]
        public void GuitarLinesWithSlidesHammerOnsAndReplucks(int response, string label, double minF, double maxMedian)
        {
            var s = RunLine(seed => GuitarLine(seed), Seeds, v => { v.Set(P.Range, 0); v.Set(P.Response, response); }, "AM1 guitar line " + label);
            // Stable plays a note only when it has lasted 40 ms (a minimum note length), so its notes come a median 40 to 47 ms after the pluck: the gate is at 100 ms.
            Assert.That(response == 0 ? s.FMeasure : s.FWide, Is.GreaterThan(minF), "note F-measure");
            Assert.That(s.Median, Is.LessThan(maxMedian), "median latency");
        }

        [TestCase(1, 0, "Bass range, Fast")]
        [TestCase(1, 1, "Bass range, Stable")]
        [TestCase(2, 0, "Full range, Fast")]
        [TestCase(2, 1, "Full range, Stable")]
        public void BassLineE1ToG3(int range, int response, string label)
        {
            var s = RunLine(seed => BassLine(seed), Seeds, v => { v.Set(P.Range, range); v.Set(P.Response, response); }, "AM1 bass line E1-G3 " + label);
            // Bass needs a long window (its latency is about 48 ms), so Stable's notes arrive near the edge of 50 ms: the gate there is at 100 ms.
            Assert.That(response == 0 ? s.FMeasure : s.FWide, Is.GreaterThan(0.8), response == 0 ? "note F-measure at 50 ms" : "note F-measure at 100 ms");
        }

        [TestCase(true, 0)]
        [TestCase(false, 0)]
        [TestCase(true, 1)]
        public void LeadWithVibrato(bool saw, int response)
        {
            var s = RunLine(seed => Lead(seed, saw: saw), Seeds, v => { v.Set(P.Range, 0); v.Set(P.Response, response); v.Set(P.Retrigger, 1); }, $"AM1 {(saw ? "saw" : "sine")} lead with vibrato, {(response == 0 ? "Fast" : "Stable")}");
            Assert.That(response == 0 ? s.FMeasure : s.FWide, Is.GreaterThan(0.85), "note F-measure");
            if (response == 0) Assert.That(s.ChatterPerMinute, Is.LessThan(6), "extra notes per minute");
        }

        // ---------------------------------------------------------------- melody: the settings

        static List<(double T, int Note, int Vel)> Notes(float[] x, Action<ParamValues> setup, out List<Out> all)
        {
            var (e, _) = Make(setup);
            all = Play(e, x);
            return NoteOns(all);
        }

        [Test]
        public void OctaveShiftAndKeyLockMoveTheNotes()
        {
            var (x, truth) = GuitarLine(11, 24, slides: false, hammers: false);
            var plain = Notes(x, v => { v.Set(P.Range, 0); }, out _);
            var up = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Octave, 4); }, out _);
            Assert.That(up.Count, Is.EqualTo(plain.Count));
            Assert.That(up.Zip(plain, (a, b) => a.Note - b.Note).All(d => d == 12), "every note an octave up");
            // C major, Set key: every note lands in the scale.
            var locked = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Lock, 1); v.Set(P.Root, 0); v.Set(P.Scale, 0); }, out _);
            int[] major = { 0, 2, 4, 5, 7, 9, 11 };
            Assert.That(locked.All(n => major.Contains(n.Note % 12)), "all notes in C major");
            Assert.That(locked.Count, Is.GreaterThan(plain.Count / 2));
        }

        [Test]
        public void RetriggerOffHoldsTheNoteAndLegatoOverlapsThem()
        {
            var (x, truth) = GuitarLine(12, 40, slides: false, hammers: false);
            var on = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Retrigger, 1); }, out _);
            var off = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Retrigger, 0); }, out _);
            Say($"AM1 retrigger on {on.Count} notes, off {off.Count} (truth {truth.Count}, of which re-plucks of the same note: {truth.Zip(truth.Skip(1), (a, b) => a.Note == b.Note).Count(b => b)})");
            Assert.That(off.Count, Is.LessThan(on.Count), "Retrigger off drops the re-plucks");
            // Legato: a note on comes before the previous note's off.
            var evs = Play(Make(v => { v.Set(P.Range, 0); v.Set(P.Legato, 1); }).E, x).Where(m => m.Stream == MidiStream.Pitch && m.Kind != MidiKind.PitchBend).ToList();
            int overlaps = 0, sounding = 0;
            foreach (var m in evs)
            {
                if (m.Kind == MidiKind.NoteOn) { if (sounding > 0) overlaps++; sounding++; }
                else if (m.Kind == MidiKind.NoteOff) sounding--;
            }
            Say($"AM1 legato: {overlaps} of {evs.Count(m => m.Kind == MidiKind.NoteOn)} notes start before the last one ends");
            Assert.That(overlaps, Is.GreaterThan(0));
        }

        [Test]
        public void PitchBendFollowsASlideWithinItsRange()
        {
            var (x, _) = GuitarLine(13, 30, slides: true, hammers: false);
            var plain = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Bend, 0); }, out var a);
            var bent = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.Bend, 5); }, out var b);
            int bends = b.Count(m => m.Kind == MidiKind.PitchBend), none = a.Count(m => m.Kind == MidiKind.PitchBend);
            Say($"AM1 pitch bend: {bends} bend messages with ±5 st, {none} with it off; notes {bent.Count} against {plain.Count}");
            Assert.That(none, Is.EqualTo(0));
            Assert.That(bends, Is.GreaterThan(5));
            Assert.That(bent.Count, Is.LessThanOrEqualTo(plain.Count));
        }

        [Test]
        public void FixedVelocityAndTheLatencyReported()
        {
            var (x, _) = GuitarLine(14, 20);
            var fixedV = Notes(x, v => { v.Set(P.Range, 0); v.Set(P.VelMode, 1); v.Set(P.Fixed, 90); }, out _);
            Assert.That(fixedV.All(n => n.Vel == 90));
            var dyn = Notes(x, v => { v.Set(P.Range, 0); }, out _);
            Assert.That(dyn.Select(n => n.Vel).Distinct().Count(), Is.GreaterThan(3), "dynamic velocity varies");
            double[] lat = new double[3];
            for (int r = 0; r < 3; r++) { var (e, _) = Make(v => v.Set(P.Range, r)); lat[r] = e.NoteLatencySeconds(r == 1 ? 55 : 196) * 1000; }
            Say($"AM1 latency reported: Guitar {F(lat[0])} ms, Bass {F(lat[1])} ms, Full {F(lat[2])} ms");
            Assert.That(lat[0], Is.LessThan(lat[1]));
        }

        // ---------------------------------------------------------------- drums

        static readonly int[] DrumNotes = { 36, 38, 42 };

        sealed class DrumScore
        {
            public int[] Truth = new int[3], Output = new int[3], Hit = new int[3];
            public int GhostTruth, GhostHit;
            public List<double> Offset = new();
            public double F(int c) { double p = Output[c] == 0 ? 0 : Hit[c] / (double)Output[c], r = Truth[c] == 0 ? 0 : Hit[c] / (double)Truth[c]; return p + r == 0 ? 0 : 2 * p * r / (p + r); }
            public override string ToString() => $"kick F {F(0) * 100:F1} % ({Hit[0]}/{Truth[0]}, {Output[0]} out), snare F {F(1) * 100:F1} % ({Hit[1]}/{Truth[1]}, {Output[1]} out; ghosts {GhostHit}/{GhostTruth}), hat F {F(2) * 100:F1} % ({Hit[2]}/{Truth[2]}, {Output[2]} out), timing median {(Offset.Count == 0 ? double.NaN : Offset.OrderBy(v => v).ElementAt(Offset.Count / 2) * 1000):F1} ms worst {(Offset.Count == 0 ? double.NaN : Offset.Max() * 1000):F1} ms";
        }

        static DrumScore ScoreDrums(float[] x, List<(long Sample, int Class, bool Ghost)> truth, AudioMidiEngine e, bool ghostsInTruth = false)
        {
            var hits = NoteOns(Play(e, x), MidiStream.Trigger);
            var s = new DrumScore();
            var used = new bool[hits.Count];
            foreach (var (at, cls, ghost) in truth)
            {
                if (ghost && !ghostsInTruth) { s.GhostTruth++; }
                else s.Truth[cls]++;
                int best = -1; double bd = 0.05;
                for (int i = 0; i < hits.Count; i++)
                {
                    if (used[i] || hits[i].Note != DrumNotes[cls]) continue;
                    double d = Math.Abs(hits[i].T - at / Rate);
                    if (d <= bd) { bd = d; best = i; }
                }
                if (best >= 0)
                {
                    used[best] = true;
                    if (ghost && !ghostsInTruth) { s.GhostHit++; continue; }
                    s.Hit[cls]++; s.Offset.Add(hits[best].T - at / Rate);
                }
            }
            // Output counts: the non-ghost matches and every unmatched output; ghost matches are neither right nor wrong.
            for (int c = 0; c < 3; c++) s.Output[c] = hits.Count(h => h.Note == DrumNotes[c]);
            for (int c = 0; c < 3; c++) s.Output[c] -= 0;
            s.Output[1] -= s.GhostHit;
            return s;
        }

        [TestCase(90)]
        [TestCase(120)]
        [TestCase(140)]
        [TestCase(174)]
        public void DrumLoopsWithGhostNotes(int bpm)
        {
            var total = new DrumScore();
            foreach (ulong seed in Seeds)
            {
                var (x, truth) = DrumLoop(bpm, 8, seed);
                var (e, _) = Make(v => v.Set(P.Mode, 1));
                var sc = ScoreDrums(x, truth, e);
                for (int c = 0; c < 3; c++) { total.Truth[c] += sc.Truth[c]; total.Output[c] += sc.Output[c]; total.Hit[c] += sc.Hit[c]; }
                total.GhostTruth += sc.GhostTruth; total.GhostHit += sc.GhostHit; total.Offset.AddRange(sc.Offset);
            }
            Say($"AM1 drum loop {bpm} bpm, built-in rules: {total}");
            Assert.That(total.F(0), Is.GreaterThan(0.65), "kick");
            Assert.That(total.F(1), Is.GreaterThan(0.80), "snare");
            Assert.That(total.F(2), Is.GreaterThan(0.70), "hat");
            Assert.That(total.Offset.Max(), Is.LessThan(0.05), "every right hit within 50 ms");
        }

        [Test]
        public void CalibratedFromFiveHitsOfEachDrum()
        {
            var (e, _) = Make(v => v.Set(P.Mode, 1));
            for (int c = 0; c < 3; c++)
            {
                var kit = new float[(int)(Rate * 3)];
                var rng = new SynthRng((ulong)(60 + c));
                for (int h = 0; h < 5; h++) PitchSignals.Hit(kit, (int)((0.4 + 0.5 * h) * Rate), Rate, c, rng, rng.Range(0.4, 0.7));
                e.StartLearning(c); e.LearnTarget = AudioMidiEngine.CalibrationHits;
                Play(e, kit);
                var f = new float[VoiceFeatures.Dimension];
                while (e.TakeLearned(out int cls, f)) e.Classifier.Add(cls, f);
                e.StopLearning();
            }
            e.Classifier.Train();
            Assert.That(e.Classifier.ClassesTrained, Is.EqualTo(3));
            var total = new DrumScore();
            foreach (ulong seed in Seeds)
            {
                var (x, truth) = DrumLoop(120, 8, seed);
                e.Reset();
                var sc = ScoreDrums(x, truth, e);
                for (int c = 0; c < 3; c++) { total.Truth[c] += sc.Truth[c]; total.Output[c] += sc.Output[c]; total.Hit[c] += sc.Hit[c]; }
                total.GhostTruth += sc.GhostTruth; total.GhostHit += sc.GhostHit; total.Offset.AddRange(sc.Offset);
            }
            Say($"AM1 drum loop 120 bpm, calibrated with 5 hits each: {total}");
            Assert.That(total.F(0), Is.GreaterThan(0.65), "kick");
            Assert.That(total.F(1), Is.GreaterThan(0.80), "snare");
            Assert.That(total.F(2), Is.GreaterThan(0.70), "hat");
        }

        [Test]
        public void PerDrumNoteVelocityAndMinimumInterval()
        {
            var (x, truth) = DrumLoop(174, 8, 5);
            var free = NoteOns(Play(Make(v => v.Set(P.Mode, 1)).E, x), MidiStream.Trigger);
            var slow = NoteOns(Play(Make(v => { v.Set(P.Mode, 1); v.Set(P.HatGap, 120); v.Set(P.KickNote, 35); v.Set(P.SnareVel, 77); }).E, x), MidiStream.Trigger);
            int hatsFree = free.Count(n => n.Note == 42), hatsSlow = slow.Count(n => n.Note == 42);
            // The hats that fall on the same sixteenth as a kick (the case a 14 ms before/after window loses): found within 50 ms, over every seed.
            int onKick = 0, onKickFound = 0;
            foreach (ulong seed in Seeds)
            {
                var (xs, ts) = DrumLoop(174, 8, seed);
                var got = NoteOns(Play(Make(v => v.Set(P.Mode, 1)).E, xs), MidiStream.Trigger).Where(n => n.Note == 42).ToList();
                foreach (var h in ts.Where(t => t.Class == 2 && ts.Any(k => k.Class == 0 && Math.Abs(k.Sample - t.Sample) < 0.001 * Rate)))
                { onKick++; if (got.Any(g => Math.Abs(g.T - h.Sample / Rate) <= 0.05)) onKickFound++; }
            }
            Say($"AM1 hats on a kick's sixteenth at 174 bpm: {onKickFound} of {onKick} found ({(onKick == 0 ? 0 : 100.0 * onKickFound / onKick):F1} %)");
            Say($"AM1 hat interval: {hatsFree} hats at 15 ms, {hatsSlow} at 120 ms (loop at 174 bpm has {truth.Count(t => t.Class == 2)})");
            Assert.That(hatsSlow, Is.LessThan(hatsFree));
            Assert.That(slow.Any(n => n.Note == 35) && !slow.Any(n => n.Note == 36), "kick note moved");
            Assert.That(slow.Where(n => n.Note == 38).All(n => n.Vel == 77), "snare velocity fixed");
        }


        // ---------------------------------------------------------------- recorded timing (Record at attack)

        [TestCase(0, "Fast")]
        [TestCase(1, "Stable")]
        public void RecordedNotesLandAtTheAttackAndLiveOutputIsUnchanged(int response, string label)
        {
            var live = new List<Score>(); var recorded = new List<Score>();
            var liveOffsets = new List<double>();
            foreach (ulong seed in Seeds)
            {
                var (x, truth) = GuitarLine(seed);
                var evs = Play(Make(v => { v.Set(P.Range, 0); v.Set(P.Response, response); }).E, x);
                live.Add(Match(truth, NoteOns(evs), x.Length / Rate));
                recorded.Add(Match(truth, RecordedNoteOns(evs), x.Length / Rate));
                // The lag is only a label: it never moves an event (the same run without reading it is the live output), and a note's off never lands before its on.
                var ons = evs.Where(m => m.Stream == MidiStream.Pitch && m.Kind == MidiKind.NoteOn).ToList();
                Assert.That(evs.All(m => m.Lag >= 0 && m.Lag < 0.2 * Rate), "lags are small and never negative");
                Assert.That(ons.Count(m => m.Lag > 0), Is.GreaterThan(ons.Count / 2), "most notes carry a lag");
                var open = new Dictionary<int, double>();
                foreach (var m in evs.Where(m => m.Stream == MidiStream.Pitch && m.Kind != MidiKind.PitchBend))
                {
                    double rec = m.T - m.Lag / Rate;
                    if (m.Kind == MidiKind.NoteOn) open[m.Data1] = rec;
                    else if (open.TryGetValue(m.Data1, out var on)) { Assert.That(rec, Is.GreaterThanOrEqualTo(on - 1e-9), "a note off is never recorded before its note on"); open.Remove(m.Data1); }
                }
            }
            var l = Merge(live); var r = Merge(recorded);
            l.FWide = 0; r.FWide = 0;
            Say($"AM1 recorded timing, guitar line {label}: live F {F(l.FMeasure * 100)} % (latency median {F(l.Median * 1000)} ms), recorded F {F(r.FMeasure * 100)} % (offset median {F(r.Median * 1000)} ms, worst {F(r.Worst * 1000)} ms)");
            Assert.That(r.FMeasure, Is.GreaterThanOrEqualTo(l.FMeasure - 1e-9), "recording at the attack never costs notes");
            Assert.That(Math.Abs(r.Median), Is.LessThan(0.015), "recorded notes land within 15 ms of the pluck (median)");
            if (response == 1) Assert.That(r.Median, Is.LessThan(l.Median), "Stable's lateness is taken out");
        }

        [Test]
        public void RecordedDrumHitsLandOnTheHitAndTheNoteKeepsItsLength()
        {
            var live = new List<double>(); var rec = new List<double>();
            foreach (ulong seed in Seeds)
            {
                var (x, truth) = DrumLoop(120, 8, seed);
                var evs = Play(Make(v => v.Set(P.Mode, 1)).E, x);
                foreach (var (at, cls, ghost) in truth)
                {
                    var on = evs.Where(m => m.Stream == MidiStream.Trigger && m.Kind == MidiKind.NoteOn && m.Data1 == DrumNotes[cls] && Math.Abs(m.T - at / Rate) < 0.05).OrderBy(m => Math.Abs(m.T - at / Rate)).FirstOrDefault();
                    if (on == null) continue;
                    live.Add(on.T - at / Rate); rec.Add(on.T - on.Lag / Rate - at / Rate);
                    var off = evs.FirstOrDefault(m => m.Stream == MidiStream.Trigger && m.Kind == MidiKind.NoteOff && m.Data1 == on.Data1 && m.T > on.T);
                    if (off != null) Assert.That((off.T - off.Lag / Rate) - (on.T - on.Lag / Rate), Is.EqualTo(0.06).Within(0.003), "recorded length is the hit length (60 ms by default)");
                }
            }
            double mLive = live.OrderBy(v => v).ElementAt(live.Count / 2), mRec = Math.Abs(rec.OrderBy(v => v).ElementAt(rec.Count / 2));
            Say($"AM1 recorded drum hits: median offset from the hit {F(mLive * 1000)} ms live, {F(mRec * 1000)} ms recorded (worst recorded {F(rec.Max(Math.Abs) * 1000)} ms, {rec.Count} hits)");
            Assert.That(mLive, Is.GreaterThan(0.010), "the note is sent after the hit");
            Assert.That(mRec, Is.LessThan(0.004), "and recorded on it");
        }

        [Test]
        public void ANoteOffQueueNoteCarriesItsLagToItsOff()
        {
            var b = new MidiBlock(); var q = new NoteOffQueue(8);
            b.Begin(0, 4800);
            q.Play(b, MidiStream.Trigger, 9, 38, 100, at: 1000, lengthSamples: 2400, attack: 600);
            q.Service(b, MidiStream.Trigger, 4000);
            Assert.That(b.Count, Is.EqualTo(2));
            Assert.That(b.Events[0].Kind, Is.EqualTo(MidiKind.NoteOn));
            Assert.That(b.Events[0].Lag, Is.EqualTo(400));
            Assert.That(b.Events[1].Kind, Is.EqualTo(MidiKind.NoteOff));
            Assert.That(b.Events[1].Offset, Is.EqualTo(3400), "the off is sent when it is due");
            Assert.That(b.Events[1].Lag, Is.EqualTo(400), "and recorded the same length after the on");
            // An attack later than the event is never a negative lag.
            b.Begin(0, 4800);
            b.Emit(MidiKind.NoteOn, MidiStream.Pitch, 0, 60, 90, 100, 900);
            Assert.That(b.Events[0].Lag, Is.EqualTo(0));
            // The bus gets the lag only while Record at attack is on.
            Assert.That(new MidiEvent { Lag = 123 }.RecordedLag(true), Is.EqualTo(123));
            Assert.That(new MidiEvent { Lag = 123 }.RecordedLag(false), Is.EqualTo(0));
        }

        [Test]
        public void RecordAtAttackIsAParameterOnByDefaultAndTheEngineReadsNothingFromIt()
        {
            var (e, v) = Make();
            Assert.That(v[P.Attack], Is.EqualTo(1), "on by default");
            var (x, _) = GuitarLine(31, 16);
            var a = Play(e, x);
            var (e2, v2) = Make(s => s.Set(P.Attack, 0));
            var b = Play(e2, x);
            Assert.That(b.Select(m => (m.T, m.Kind, m.Data1, m.Data2)), Is.EqualTo(a.Select(m => (m.T, m.Kind, m.Data1, m.Data2))), "the live events are the same either way: the machine only decides what the bus is told");
        }

        // ---------------------------------------------------------------- real time

        [Test]
        public void ProcessingAllocatesNothing()
        {
            var (g, _) = GuitarLine(21, 20);
            var (d, _) = DrumLoop(120, 4, 4);
            var x = g.Concat(d).ToArray();
            var (e, v) = Make(s => { s.Set(P.Bend, 2); });
            var buf = new float[256];
            Play(e, x);
            var (allocated, why) = AllocationCheck.Run(x.Length / 256, b =>
            {
                Array.Copy(x, b * 256, buf, 0, 256);
                if (b == 100) { v.Set(P.Lock, 1); v.Set(P.Scale, 1); v.Set(P.Range, 1); v.Set(P.Response, 1); }
                if (b == 300) { v.Set(P.Octave, 4); v.Set(P.Legato, 1); }
                if (b == (g.Length / 256) + 4) v.Set(P.Mode, 1);
                if (b == (g.Length / 256) + 200) v.Set(P.Mode, 0);
                e.Process(buf, 256);
            });
            Measure.Report($"Neo Audio to MIDI: allocation over {x.Length / Rate} s with parameter and mode changes: {allocated} bytes");
            Assert.That(allocated, Is.EqualTo(0), why);
        }

        [TestCase(0, "Melody, Guitar range, Fast")]
        [TestCase(1, "Drums")]
        public void TheEngineCostsUnderFourPercentOfRealTime(int mode, string label)
        {
            var (g, _) = GuitarLine(22, 24);
            var (d, _) = DrumLoop(140, 6, 6);
            var x = mode == 0 ? g : d;
            Throughput.Guard("Neo Audio to MIDI: " + label + " at 48 kHz", Throughput.Floor(x64: 25, arm64: 18), 2, (int)Rate, 256, () =>
            {
                var (e, _) = Make(s => s.Set(P.Mode, mode));
                var buf = new float[256];
                void Block(int pos) { Array.Copy(x, pos % (x.Length - 256), buf, 0, 256); e.Process(buf, 256); }
                for (int pos = 0; pos < Rate; pos += 256) Block(pos);
                return Throughput.InBlocks(256, (pos, n) => Block((int)Rate + pos));
            }, Measure.Report);
        }
    }
}
