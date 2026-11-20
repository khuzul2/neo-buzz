// Neo Voice to MIDI (docs/specs/voice-to-midi.md §10): the engine measured on synthetic voices and a synthetic beatbox kit, whose truth is known.
// Each test prints its figures (the spec quotes them).

using System.Globalization;
using NeoBuzz.Dsp;
using NeoBuzz.VoiceToMidi;

namespace NeoBuzz.MachineTests
{
    [TestFixture]
    public class VoiceToMidiTests
    {
        const int Rate = 48000;
        static void Say(string s) => TestContext.Out.WriteLine(s);
        static string F(double v, string fmt = "F1") => v.ToString(fmt, CultureInfo.InvariantCulture);

        sealed record Ev(long At, MidiKind Kind, MidiStream Stream, int Channel, int Data1, int Data2, int Lag = 0);

        static (VoiceEngine E, ParamValues V) Make(Action<ParamValues> setup = null)
        {
            var v = new ParamValues(Params.Table);
            v.Set(P.Monitor, 0);
            setup?.Invoke(v);
            var e = new VoiceEngine(v);
            e.SetSampleRate(Rate);
            return (e, v);
        }

        static List<Ev> Play(VoiceEngine e, float[] x, int block = 256)
        {
            var list = new List<Ev>();
            var buf = new float[VoiceEngine.MaxBlock];
            for (int pos = 0; pos < x.Length; pos += block)
            {
                int n = Math.Min(block, x.Length - pos);
                Array.Copy(x, pos, buf, 0, n);
                int count = e.Process(buf, n);
                for (int i = 0; i < count; i++)
                {
                    var m = e.Events[i];
                    list.Add(new Ev(pos + m.Offset, m.Kind, m.Stream, m.Channel, m.Data1, m.Data2, m.Lag));
                }
            }
            int c = e.Flush();
            for (int i = 0; i < c; i++) { var m = e.Events[i]; list.Add(new Ev(x.Length + m.Offset, m.Kind, m.Stream, m.Channel, m.Data1, m.Data2, m.Lag)); }
            return list;
        }

        static double Hz(int midi) => 440 * Math.Pow(2, (midi - 69) / 12.0);

        /// <summary>The notes in order, each held 0.6 s with 0.2 s of silence between.</summary>
        static (float[] X, List<(double Start, int Note)> Truth) Melody(int[] notes, ulong seed = 3, double cents = 0, double hold = 0.6)
        {
            var parts = new List<float>();
            var truth = new List<(double, int)>();
            parts.AddRange(new float[(int)(0.3 * Rate)]);
            foreach (int n in notes)
            {
                truth.Add((parts.Count / (double)Rate, n));
                var v = PitchSignals.Voice(Rate, Hz(n) * Math.Pow(2, cents / 1200), hold, new PitchSignals.VoiceOptions { Seed = seed++ });
                parts.AddRange(v.X);
                parts.AddRange(new float[(int)(0.2 * Rate)]);
            }
            parts.AddRange(new float[(int)(0.3 * Rate)]);
            return (parts.ToArray(), truth);
        }

        static List<Ev> NoteOns(List<Ev> evs, MidiStream stream = MidiStream.Pitch) => evs.Where(e => e.Stream == stream && e.Kind == MidiKind.NoteOn).ToList();

        // ---------------------------------------------------------------- pitch

        [TestCase(0, 0, "Low latency, Fast")]
        [TestCase(1, 1, "Quality, Stable")]
        public void RecordedVoiceNotesLandAtTheStartOfTheSound(int range, int response, string name)
        {
            var (e, v) = Make(v => { v.Set(P.Range, range); v.Set(P.Response, response); v.Set(P.Triggers, 0); v.Set(P.Lock, 0); });
            Assert.That(v[P.Attack], Is.EqualTo(1), "Record at attack is on by default");
            var (x, truth) = Melody(new[] { 57, 60, 64, 67, 62, 69, 72, 55 });
            var evs = Play(e, x);
            var ons = NoteOns(evs);
            var live = new List<double>(); var rec = new List<double>();
            foreach (var (start, note) in truth)
            {
                var hit = ons.FirstOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.5);
                if (hit == null) continue;
                live.Add(hit.At / (double)Rate - start); rec.Add((hit.At - hit.Lag) / (double)Rate - start);
            }
            live.Sort(); rec.Sort();
            double mLive = live[live.Count / 2], mRec = rec.OrderBy(Math.Abs).ElementAt(rec.Count / 2);
            Say($"VM1 recorded timing ({name}): note-on delay median {F(mLive * 1000)} ms live, {F(mRec * 1000)} ms recorded (worst recorded {F(rec.Max(Math.Abs) * 1000)} ms over {rec.Count} notes)");
            Assert.That(rec.Count, Is.GreaterThanOrEqualTo(truth.Count - 1));
            Assert.That(Math.Abs(mRec), Is.LessThan(Math.Abs(mLive)), "recorded closer to the start than live");
            Assert.That(rec.Max(Math.Abs), Is.LessThan(0.035), "within 35 ms of the start of the sound");
            // A note off is never recorded before its note on.
            double on = double.NaN;
            foreach (var m in evs.Where(m => m.Stream == MidiStream.Pitch && m.Kind != MidiKind.PitchBend))
            {
                double t = (m.At - m.Lag) / (double)Rate;
                if (m.Kind == MidiKind.NoteOn) on = t; else Assert.That(t, Is.GreaterThanOrEqualTo(on - 1e-9));
            }
        }

        [TestCase(0, 0, "Low latency, Fast")]
        [TestCase(1, 0, "Quality, Fast")]
        [TestCase(0, 1, "Low latency, Stable")]
        [TestCase(1, 1, "Quality, Stable")]
        public void SungNotesPlayTheRightNotesAndTheLatencyIsMeasured(int range, int response, string name)
        {
            var (e, v) = Make(v => { v.Set(P.Range, range); v.Set(P.Response, response); v.Set(P.Triggers, 0); v.Set(P.Lock, 0); });
            var notes = new[] { 57, 60, 64, 67, 62, 69, 72, 55 };
            var (x, truth) = Melody(notes);
            var evs = Play(e, x);
            var ons = NoteOns(evs);
            int right = 0, finalRight = 0; var delays = new List<double>();
            foreach (var (start, note) in truth)
            {
                var hit = ons.FirstOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.5);
                if (hit == null) continue;
                // Pitch notes carry the Octave shift (0 by default).
                if (hit.Data1 == note) right++;
                delays.Add((hit.At / (double)Rate - start) * 1000);
                // What sounds 120 ms into the note, after any correction.
                var last = ons.LastOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.12);
                if (last != null && last.Data1 == note) finalRight++;
            }
            delays.Sort();
            double median = delays.Count == 0 ? 0 : delays[delays.Count / 2], worst = delays.Count == 0 ? 0 : delays[^1];
            Say($"VM1 pitch ({name}): {finalRight}/{truth.Count} notes right at 120 ms ({right} on the first note-on, {truth.Count - right} wrong first), note-on delay median {F(median)} ms, worst {F(worst)} ms, {ons.Count} note-ons, {e.Corrections} corrections");
            Assert.That(finalRight, Is.GreaterThanOrEqualTo(truth.Count - 1));
            if (response == 0)
            {
                Assert.That(median, Is.LessThanOrEqualTo(range == 0 ? 20 : 30));
                Assert.That(worst, Is.LessThanOrEqualTo(range == 0 ? 35 : 45));
                Assert.That(ons.Count, Is.LessThanOrEqualTo(truth.Count + e.Corrections + 1), "no extra notes besides the corrections");
            }
            else
            {
                Assert.That(ons.Count, Is.LessThanOrEqualTo(truth.Count + 1), "no extra notes");
                Assert.That(median, Is.LessThan(range == 0 ? 80 : 120));
            }
            Assert.That(e.NoteLatencySeconds(Hz(60)) * 1000, Is.LessThan(range == 0 ? 60 : 90));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NoteOnDelayWithTriggersOnAndTrainedSounds(int range)
        {
            var (e, _) = Make(v => { v.Set(P.Range, range); v.Set(P.Lock, 0); });
            for (int c = 0; c < 3; c++) Train(e, c, 15, (ulong)(70 + c));
            e.Classifier.Train();
            e.Reset();
            var notes = new[] { 57, 60, 64, 67, 62, 69, 72, 55 };
            var (x, truth) = Melody(notes);
            var evs = Play(e, x);
            var ons = NoteOns(evs);
            var delays = new List<double>(); int right = 0;
            foreach (var (start, note) in truth)
            {
                var hit = ons.FirstOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.5);
                if (hit == null) continue;
                delays.Add((hit.At / (double)Rate - start) * 1000);
                var last = ons.LastOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.12);
                if (last != null && last.Data1 == note) right++;
            }
            delays.Sort();
            int trig = NoteOns(evs, MidiStream.Trigger).Count;
            Say($"VM1 pitch with Triggers on, 3 sounds trained ({(range == 0 ? "Low latency" : "Quality")}, Fast): {right}/{truth.Count} right at 120 ms, note-on delay median {F(delays.Count == 0 ? 0 : delays[delays.Count / 2])} ms, worst {F(delays.Count == 0 ? 0 : delays[^1])} ms, {trig} trigger notes from the voice");
            Assert.That(right, Is.GreaterThanOrEqualTo(truth.Count - 1));
            Assert.That(trig, Is.EqualTo(0), "sung notes are not hits");
            Assert.That(delays[delays.Count / 2], Is.LessThanOrEqualTo(range == 0 ? 20 : 30));
            Assert.That(delays[^1], Is.LessThanOrEqualTo(range == 0 ? 35 : 45));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void NoteOnDelayBrokenDownByStage(int range)
        {
            // Where the delay goes: the tracker's first voiced estimate (the window), then what the engine adds, then the host's block.
            var notes = new[] { 57, 60, 64, 67, 62, 69, 72, 55 };
            var (x, truth) = Melody(notes);
            var tr = new PitchTracker(Rate, PitchRange.Voice, range == 0 ? PitchQuality.LowLatency : PitchQuality.Quality);
            var frames = new PitchEstimate[tr.MaxFrames(256) + 4];
            var firstVoiced = new List<double>();
            long pos = 0; int next = 0;
            for (int p0 = 0; p0 < x.Length; p0 += 256)
            {
                int n = Math.Min(256, x.Length - p0);
                int got = tr.Process(x, p0, n, frames, 0);
                for (int i = 0; i < got; i++)
                    if (next < truth.Count && frames[i].Voiced && frames[i].EndSample / (double)Rate >= truth[next].Start)
                    {
                        firstVoiced.Add((frames[i].EndSample / (double)Rate - truth[next].Start) * 1000); next++;
                    }
                pos += n;
            }
            var (e, _) = Make(v => { v.Set(P.Range, range); v.Set(P.Response, 0); v.Set(P.Triggers, 0); v.Set(P.Lock, 0); });
            var ons = NoteOns(Play(e, x));
            var engine = new List<double>(); var wrongFirst = 0;
            foreach (var (start, note) in truth)
            {
                var hit = ons.FirstOrDefault(o => o.At / (double)Rate >= start - 0.01 && o.At / (double)Rate < start + 0.5);
                if (hit == null) continue;
                engine.Add((hit.At / (double)Rate - start) * 1000);
                if (hit.Data1 != note) wrongFirst++;
            }
            firstVoiced.Sort(); engine.Sort();
            double Med(List<double> l) => l.Count == 0 ? 0 : l[l.Count / 2];
            Say($"VM1 delay stages ({(range == 0 ? "Low latency" : "Quality")}, Fast): tracker first voiced estimate median {F(Med(firstVoiced))} ms (worst {F(firstVoiced.Count == 0 ? 0 : firstVoiced[^1])}), " +
                $"note-on median {F(Med(engine))} ms (worst {F(engine.Count == 0 ? 0 : engine[^1])}); voicing hysteresis, stickiness and min note length add 0 to the first note; " +
                $"the host adds up to one block (256 samples = {F(256 * 1000.0 / Rate)} ms); first notes wrong: {wrongFirst} of {engine.Count}");
            Assert.That(Med(engine) - Med(firstVoiced), Is.LessThanOrEqualTo(VoiceEngine.FastWaitMs), "the engine adds at most its wait for a clean estimate");
        }

        [Test]
        public void EveryNoteOnHasItsNoteOffAndNothingHangs()
        {
            var (e, _) = Make(v => v.Set(P.Triggers, 0));
            var (x, _) = Melody(new[] { 60, 62, 64, 65, 67 });
            var evs = Play(e, x);
            int on = 0, off = 0;
            foreach (var m in evs.Where(m => m.Stream == MidiStream.Pitch)) { if (m.Kind == MidiKind.NoteOn) on++; else if (m.Kind == MidiKind.NoteOff) off++; }
            Assert.That(on, Is.GreaterThan(0));
            Assert.That(off, Is.EqualTo(on));
            Assert.That(e.Busy, Is.False);
        }

        [Test]
        public void TheKeyLockMovesAnOutOfKeyNoteToTheNearestNoteInTheKey()
        {
            // C major, a voice on C# 4 (61) goes to C (60) or D (62), never 61; E-flat (63) goes to D or E.
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Lock, 1); v.Set(P.Root, 0); v.Set(P.Scale, 0); });
            var (x, _) = Melody(new[] { 61, 63, 66, 68, 70 });
            var ons = NoteOns(Play(e, x));
            int[] inKey = { 0, 2, 4, 5, 7, 9, 11 };
            Say("VM1 key lock: " + string.Join(" ", ons.Select(o => o.Data1)));
            Assert.That(ons.Count, Is.GreaterThanOrEqualTo(4));
            foreach (var o in ons) Assert.That(inKey, Does.Contain(o.Data1 % 12), $"note {o.Data1} is in C major");
        }

        [Test]
        public void AMutedNoteIsNeverPlayedAndTheOctaveShiftMovesTheNotes()
        {
            // C muted: a voice on C goes to the nearest note that is on (B or D).
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Lock, 1); v.Set(P.Scale, 0); v.Set(P.Muted, 1); });
            var (x, _) = Melody(new[] { 60, 72 });
            var ons = NoteOns(Play(e, x));
            Assert.That(ons.Count, Is.GreaterThanOrEqualTo(2));
            foreach (var o in ons) Assert.That(o.Data1 % 12, Is.Not.EqualTo(0));
            var (e2, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Octave, 4); });   // +1 octave (raw 3 = 0)
            var (x2, _) = Melody(new[] { 60, 64 });
            var ons2 = NoteOns(Play(e2, x2));
            Say("VM1 octave +1: " + string.Join(" ", ons2.Select(o => o.Data1)));
            Assert.That(ons2.Select(o => o.Data1), Is.EqualTo(new[] { 72, 76 }));
        }

        [Test]
        public void AFlatVoiceStaysOnItsNoteWithStickinessAndAnchoredBendOnlyMovesPastTheThreshold()
        {
            // 25 cents flat holds the note; free bend follows, anchored stays put until the voice leaves the anchor.
            var (xa, _) = Melody(new[] { 64 }, cents: -20, hold: 1.0);
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Bend, 2); v.Set(P.BendMode, 0); });
            var free = Play(e, xa).Where(m => m.Kind == MidiKind.PitchBend).ToList();
            var (e2, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Bend, 2); v.Set(P.BendMode, 1); v.Set(P.Anchor, 40); });
            var anchored = Play(e2, xa).Where(m => m.Kind == MidiKind.PitchBend && Math.Abs(m.Data2 - 8192) > 64).ToList();
            var (e3, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Bend, 0); });
            var off = Play(e3, xa).Where(m => m.Kind == MidiKind.PitchBend).ToList();
            Say($"VM1 bend: free {free.Count} messages (last {free.LastOrDefault()?.Data2}), anchored beyond the dead zone {anchored.Count}, off {off.Count}");
            Assert.That(free.Count, Is.GreaterThan(0));
            Assert.That(off.Count, Is.EqualTo(0));
            Assert.That(anchored.Count, Is.EqualTo(0), "20 cents is inside the 40 cent anchor");
            Assert.That(free.Any(m => Math.Abs(m.Data2 - 8192) > 600), Is.True, "20 cents of 200 cents range is about 819 steps");
        }

        [Test]
        public void VelocityFollowsTheLevelInDynamicModeAndIsFixedInFixedMode()
        {
            var parts = new List<float>(new float[Rate / 4]);
            foreach (double level in new[] { 0.04, 0.5 })
            {
                parts.AddRange(PitchSignals.Voice(Rate, 220, 0.6, new PitchSignals.VoiceOptions { Level = level }).X);
                parts.AddRange(new float[Rate / 4]);
            }
            var x = parts.ToArray();
            var (e, _) = Make(v => v.Set(P.Triggers, 0));
            var dyn = NoteOns(Play(e, x));
            var (e2, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.VelMode, 1); v.Set(P.Fixed, 90); });
            var fix = NoteOns(Play(e2, x));
            Say($"VM1 velocity: dynamic {string.Join(",", dyn.Select(o => o.Data2))}, fixed {string.Join(",", fix.Select(o => o.Data2))}");
            Assert.That(dyn.Count, Is.EqualTo(2));
            Assert.That(dyn[1].Data2, Is.GreaterThan(dyn[0].Data2 + 20));
            Assert.That(fix.Select(o => o.Data2), Is.All.EqualTo(90));
        }

        [Test]
        public void ASuggestedKeyFollowsTheSungNotes()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Lock, 3); });
            var notes = new[] { 57, 59, 60, 62, 64, 65, 67, 69, 67, 65, 64, 62, 60, 59, 57, 60, 64, 57 };   // A minor
            var (x, _) = Melody(notes, hold: 0.35);
            Play(e, x);
            Say($"VM1 suggested key: root {e.ActiveRoot} scale {e.ActiveScale}, notes mask {Convert.ToString(e.ActiveMask, 2)}");
            Assert.That((e.ActiveMask & (1 << 9)) != 0 && (e.ActiveMask & (1 << 0)) != 0 && (e.ActiveMask & (1 << 4)) != 0, Is.True, "A, C and E are in the suggested key");
            Assert.That((e.ActiveMask & (1 << 1)) == 0, Is.True, "C# is not");
        }

        [Test]
        public void ASongKeyIsFollowed()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Lock, 2); });
            e.SetSongKey("D minor");
            var (x, _) = Melody(new[] { 61, 64 });
            var ons = NoteOns(Play(e, x));
            Assert.That(ons.Select(o => o.Data1 % 12), Is.All.Not.EqualTo(1).And.Not.EqualTo(11), "D minor has neither C# nor B natural");
        }

        // ---------------------------------------------------------------- chords

        [Test]
        public void ChordsFollowTheSungRootDiatonicallyOnTheirOwnChannel()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Pitch, 0); v.Set(P.Chords, 1); v.Set(P.Lock, 1); v.Set(P.Scale, 0); v.Set(P.ChordCh, 2); });
            var (x, _) = Melody(new[] { 60, 62, 64, 65, 67, 69, 71 });
            var evs = Play(e, x);
            var chordOns = evs.Where(m => m.Stream == MidiStream.Chord && m.Kind == MidiKind.NoteOn).ToList();
            Assert.That(chordOns.Count, Is.GreaterThanOrEqualTo(18));
            Assert.That(chordOns.Select(c => c.Channel), Is.All.EqualTo(2));
            // Group by the time each chord started.
            var groups = chordOns.GroupBy(c => c.At / 480).Select(g => g.Select(c => c.Data1 % 12).OrderBy(n => n).ToArray()).ToList();
            int[][] expected = { new[] { 0, 4, 7 }, new[] { 2, 5, 9 }, new[] { 4, 7, 11 }, new[] { 0, 5, 9 }, new[] { 2, 7, 11 }, new[] { 0, 4, 9 }, new[] { 2, 5, 11 } };
            Say("VM1 chords: " + string.Join(" | ", groups.Select(g => string.Join(",", g))));
            for (int i = 0; i < expected.Length && i < groups.Count; i++)
                Assert.That(groups[i].OrderBy(n => n).ToArray(), Is.EqualTo(expected[i]), $"chord {i + 1}");
            int[] inKey = { 0, 2, 4, 5, 7, 9, 11 };
            Assert.That(chordOns.All(c => inKey.Contains(c.Data1 % 12)), Is.True);
            int on = chordOns.Count, off = evs.Count(m => m.Stream == MidiStream.Chord && m.Kind == MidiKind.NoteOff);
            Assert.That(off, Is.EqualTo(on), "every chord note ends");
        }

        [Test]
        public void SeventhsAddAFourthNoteAndTheSuggestionsFitTheMelody()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Pitch, 0); v.Set(P.Chords, 1); v.Set(P.ChordType, 1); v.Set(P.Lock, 1); });
            var (x, _) = Melody(new[] { 60, 64, 67, 64, 60, 67, 64, 60 }, hold: 0.4);
            var evs = Play(e, x);
            var first = evs.Where(m => m.Stream == MidiStream.Chord && m.Kind == MidiKind.NoteOn).Take(4).Select(m => m.Data1 % 12).OrderBy(n => n).ToArray();
            Assert.That(first, Is.EqualTo(new[] { 0, 4, 7, 11 }), "Cmaj7");
            var s = e.Suggester;
            Say($"VM1 chord suggestions for a C-E-G melody: {string.Join(" ", Enumerable.Range(0, s.Count).Select(i => ChordBuilder.ChordName(s.RootPc[i], s.Quality[i])))}");
            Assert.That(s.Count, Is.GreaterThan(0));
            Assert.That(s.RootPc[0], Is.EqualTo(0));
        }

        // ---------------------------------------------------------------- expression

        [Test]
        public void TheLevelMapsToAControllerWithinItsRangeAndChannel()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Pitch, 0); v.Set(P.LvlOn, 1); v.Set(P.LvlCC, 10); v.Set(P.LvlLo, 20); v.Set(P.LvlHi, 100); v.Set(P.LvlCh, 3); });
            var parts = new List<float>(new float[Rate / 4]);
            foreach (double level in new[] { 0.01, 0.1, 0.6 }) { parts.AddRange(PitchSignals.Voice(Rate, 200, 0.6, new PitchSignals.VoiceOptions { Level = level }).X); }
            var evs = Play(e, parts.ToArray());
            var cc = evs.Where(m => m.Kind == MidiKind.Controller && m.Data1 == 11).ToList();
            Say($"VM1 level CC range {cc.Min(c => c.Data2)}..{cc.Max(c => c.Data2)}, {cc.Count} messages");
            Assert.That(cc.Count, Is.GreaterThan(3));
            Assert.That(cc.Select(c => c.Channel), Is.All.EqualTo(3), "channel 4, counted from 0");
            Assert.That(cc.Min(c => c.Data2), Is.GreaterThanOrEqualTo(20));
            Assert.That(cc.Max(c => c.Data2), Is.LessThanOrEqualTo(100));
            Assert.That(cc.Last(m => m.At < 1.7 * Rate).Data2, Is.GreaterThan(cc.First().Data2), "louder gives a higher value");
            Assert.That(evs.Count(m => m.Kind == MidiKind.Controller && m.Data1 != 11), Is.EqualTo(0));
        }

        [Test]
        public void TheVowelsMoveTheirOwnControllers()
        {
            var (e, _) = Make(v => { v.Set(P.Triggers, 0); v.Set(P.Pitch, 0); v.Set(P.AaOn, 1); v.Set(P.OoOn, 1); v.Set(P.EeOn, 1); v.Set(P.AaCC, 20); v.Set(P.OoCC, 21); v.Set(P.EeCC, 22); });
            double[][] formants = { PitchSignals.VowelsLow[0], PitchSignals.VowelsLow[1], PitchSignals.VowelsLow[2] };   // aa, oo, ee
            var winners = new List<string>();
            for (int k = 0; k < formants.Length; k++)
            {
                var x = PitchSignals.Voice(Rate, 140, 1.2, new PitchSignals.VoiceOptions { Formants = formants[k], Seed = (ulong)(20 + k) }).X;
                var evs = Play(e, x);
                var last = new Dictionary<int, int>();
                foreach (var m in evs.Where(m => m.Kind == MidiKind.Controller)) last[m.Data1] = m.Data2;
                Say($"   vowel {k}: " + string.Join(" ", last.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value)));
                int best = last.Count == 0 ? -1 : last.OrderByDescending(p => p.Value).First().Key;
                winners.Add(best.ToString());
                e.Reset();
            }
            Say("VM1 vowels, strongest controller for a, o, i: " + string.Join(" ", winners) + " (21 aa, 22 oo, 23 ee)");
            Assert.That(winners.Distinct().Count(), Is.GreaterThanOrEqualTo(2), "different vowels favour different controllers");
        }

        // ---------------------------------------------------------------- triggers

        static void Train(VoiceEngine e, int cls, int hits, ulong seed)
        {
            var (x, _) = PitchSignals.Kit(Rate, Enumerable.Repeat(cls, hits).ToArray(), 0.22, 0.35, seed);
            e.StartLearning(cls);
            e.LearnTarget = hits;
            Play(e, x);
            var f = new float[VoiceFeatures.Dimension];
            while (e.TakeLearned(out int c, f)) e.Classifier.Add(c, f);
            e.StopLearning();
        }

        static (int Right, int Total, int Extra) TriggerAccuracy(int classes, int examples, VoiceEngine e = null)
        {
            if (e == null) (e, _) = Make(v => { v.Set(P.Pitch, 0); v.Set(P.Gap, 50); });
            for (int c = 0; c < classes; c++) Train(e, c, examples, (ulong)(100 + c));
            e.Classifier.Train();
            var rng = new SynthRng(777);
            var order = Enumerable.Range(0, 60).Select(i => i % classes).OrderBy(_ => rng.Next()).ToArray();
            var (x, truth) = PitchSignals.Kit(Rate, order, 0.2, 0.35, 999);
            e.Reset();
            var ons = Play(e, x).Where(m => m.Stream == MidiStream.Trigger && m.Kind == MidiKind.NoteOn).ToList();
            int right = 0;
            var used = new HashSet<int>();
            foreach (var (sample, cls) in truth)
            {
                int note = (int)e.Values.Value(P.T1Note + 3 * cls);
                for (int i = 0; i < ons.Count; i++)
                {
                    if (used.Contains(i)) continue;
                    if (ons[i].At >= sample - Rate / 100 && ons[i].At < sample + Rate / 10) { used.Add(i); if (ons[i].Data1 == note) right++;; break; }
                }
            }
            return (right, truth.Count, ons.Count - used.Count);
        }

        [TestCase(3, 15)]
        [TestCase(4, 15)]
        [TestCase(8, 20)]
        public void TrainedSoundsPlayTheirNotes(int classes, int examples)
        {
            var (right, total, extra) = TriggerAccuracy(classes, examples);
            Say($"VM1 triggers on the synthetic kit, {classes} sounds trained with {examples} examples: {right}/{total} right ({F(100.0 * right / total)} %), {extra} extra notes");
            Assert.That(100.0 * right / total, Is.GreaterThanOrEqualTo(classes <= 4 ? 85 : 80));
            Assert.That(extra, Is.LessThanOrEqualTo(4));
        }

        [Test]
        public void TrainingStopsByItselfAtTheTargetAndOnlyTheTrainedSoundsPlay()
        {
            var (e, _) = Make(v => v.Set(P.Pitch, 0));
            var (x, _) = PitchSignals.Kit(Rate, Enumerable.Repeat(0, 30).ToArray(), 0.22, 0.3, 5);
            e.StartLearning(0);
            Play(e, x);
            Assert.That(e.LearnDone, Is.True);
            Assert.That(e.Learning, Is.EqualTo(-1));
            Assert.That(e.LearnedThisPass, Is.EqualTo(e.LearnTarget));
            // Nothing trained: nothing plays.
            var (e2, _) = Make(v => v.Set(P.Pitch, 0));
            var (y, _) = PitchSignals.Kit(Rate, new[] { 0, 1, 2, 0, 1, 2 }, 0.3, 0.4, 6);
            Assert.That(Play(e2, y).Count(m => m.Stream == MidiStream.Trigger), Is.EqualTo(0));
        }

        [Test]
        public void ATrainedSetSurvivesASerializeAndLoad()
        {
            var (e, _) = Make(v => v.Set(P.Pitch, 0));
            for (int c = 0; c < 4; c++) Train(e, c, 12, (ulong)(30 + c));
            e.Classifier.Train();
            string text = e.Classifier.Serialize();
            var (e2, _) = Make(v => v.Set(P.Pitch, 0));
            Assert.That(e2.Classifier.Deserialize(text), Is.True);
            Assert.That(e2.Classifier.Trained, Is.True);
            Assert.That(e2.Classifier.Count, Is.EqualTo(e.Classifier.Count));
            Assert.That(e2.Classifier.Serialize(), Is.EqualTo(text));
        }

        [TestCase(0)]
        [TestCase(1)]
        public void VoicedSoundsGoToThePitchAndPercussiveOnesToTheTriggersAtOnce(int response)
        {
            var (e, _) = Make(v => { v.Set(P.Overlap, 0); v.Set(P.Gap, 50); v.Set(P.Response, response); });
            for (int c = 0; c < 3; c++) Train(e, c, 15, (ulong)(60 + c));
            e.Classifier.Train();
            e.Reset();
            // A sung note with kick hits (class 0) in the middle of a pause: both streams play.
            var voice = PitchSignals.Voice(Rate, Hz(57), 0.8, new PitchSignals.VoiceOptions { Seed = 9 }).X;
            var (kit, _) = PitchSignals.Kit(Rate, new[] { 0, 1, 0 }, 0.3, 0.35, 12);
            var x = new List<float>(new float[Rate / 4]);
            x.AddRange(voice); x.AddRange(new float[Rate / 4]); x.AddRange(kit);
            var evs = Play(e, x.ToArray());
            int pitch = NoteOns(evs).Count, trig = NoteOns(evs, MidiStream.Trigger).Count;
            Say($"VM1 both at once: {pitch} pitch notes, {trig} trigger notes");
            Assert.That(pitch, Is.GreaterThanOrEqualTo(1));
            Assert.That(trig, Is.GreaterThanOrEqualTo(2));
            long voiceEnd = Rate / 4 + voice.Length + Rate / 8;
            if (response == 1) Assert.That(NoteOns(evs).All(o => o.At < voiceEnd), Is.True, "Stable: no pitch note from the beatbox");
            else
            {
                // Fast plays at once and a hit named a trigger cuts its pitch note: at most a blip of 80 ms (the hit is named about 60 ms after it starts).
                foreach (var on in NoteOns(evs).Where(o => o.At >= voiceEnd))
                {
                    var off = evs.FirstOrDefault(m => m.Stream == MidiStream.Pitch && m.Kind == MidiKind.NoteOff && m.Data1 == on.Data1 && m.At >= on.At);
                    Assert.That(off, Is.Not.Null);
                    Assert.That((off.At - on.At) * 1000.0 / Rate, Is.LessThanOrEqualTo(80), "a pitch note from a hit is cut once the hit is named");
                }
                Say($"VM1 Fast, pitch notes from the beatbox: {NoteOns(evs).Count(o => o.At >= voiceEnd)} blips");
            }
        }

        // ---------------------------------------------------------------- real time

        [Test]
        public void ProcessingAllocatesNothing()
        {
            var (e, v) = Make(s => { s.Set(P.Chords, 1); s.Set(P.AaOn, 1); s.Set(P.LvlOn, 1); s.Set(P.Bend, 2); });
            for (int c = 0; c < 4; c++) Train(e, c, 10, (ulong)(40 + c));
            e.Classifier.Train();
            var (mel, _) = Melody(new[] { 57, 60, 64, 67, 62, 69, 72, 55, 60, 64 }, hold: 0.4);
            var (kit, _) = PitchSignals.Kit(Rate, new[] { 0, 1, 2, 3, 0, 1, 2, 3 }, 0.25, 0.3, 3);
            var x = mel.Concat(kit).ToArray();
            var buf = new float[256];
            Play(e, x);
            var (allocated, why) = AllocationCheck.Run(x.Length / 256, b =>
            {
                Array.Copy(x, b * 256, buf, 0, 256);
                if (b == 100) { v.Set(P.Lock, 1); v.Set(P.Scale, 1); v.Set(P.Range, 0); v.Set(P.ChordType, 1); }
                if (b == 300) { v.Set(P.Window, 0); v.Set(P.Octave, 4); }
                e.Process(buf, 256);
            });
            Measure.Report($"Neo Voice to MIDI: allocation over {x.Length / Rate} s with parameter changes: {allocated} bytes");
            Assert.That(allocated, Is.EqualTo(0), why);
        }

        [Test]
        public void TheEngineCostsUnderFourPercentOfRealTime()
        {
            var (mel, _) = Melody(new[] { 57, 60, 64, 67, 62, 69, 72, 55, 60, 64, 62, 59 }, hold: 0.5);
            var (kit, _) = PitchSignals.Kit(Rate, new[] { 0, 1, 2, 3, 0, 1, 2, 3, 0, 1, 2, 3 }, 0.25, 0.3, 3);
            var x = mel.Concat(kit).ToArray();
            Throughput.Guard("Neo Voice to MIDI: pitch, chords, four trained sounds and the expression lanes at 48 kHz", Throughput.Floor(x64: 25, arm64: 18), 2, Rate, 256, () =>
            {
                var (e, _) = Make(s => { s.Set(P.Chords, 1); s.Set(P.AaOn, 1); s.Set(P.LvlOn, 1); });
                for (int c = 0; c < 4; c++) Train(e, c, 10, (ulong)(40 + c));
                e.Classifier.Train();
                var buf = new float[256];
                void Block(int pos) { Array.Copy(x, pos % (x.Length - 256), buf, 0, 256); e.Process(buf, 256); }
                for (int pos = 0; pos < Rate; pos += 256) Block(pos);
                return Throughput.InBlocks(256, (pos, n) => Block(Rate + pos));
            }, Measure.Report);
        }
    }
}
