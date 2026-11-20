#nullable disable
// Neo Audio to MIDI (docs/specs/audio-to-midi.md): the engine. Mono audio in, MIDI events out; no ReBuzz types.
//  - Melody: PitchTracker (Guitar, Bass or Full range) and OnsetDetector -> MelodyNotes (notes at the onset with the attack's velocity, re-plucks,
//    legato, hammer-ons and slides, pitch bend, key lock, Fast or Stable response, note off on a level or pitch fall with hysteresis).
//  - Drums: OnsetDetector -> DrumHits (kick, snare, hat by rule, or by the trained sounds when all three are calibrated) -> a note per class with its
//    velocity and minimum interval.
// Shared with Neo Voice to MIDI (Machines/NeoBuzz.Dsp/Midi): the event buffer, the key lock, velocity, the bend map, the note-off queue, the training pass.
// Allocation-free per block after construction (Train, the training drain and the readings are off the audio thread).

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.AudioToMidi
{
    internal sealed class AudioMidiEngine
    {
        public const int MaxBlock = 256, Sounds = DrumHits.Classes;
        public readonly ParamValues Values;
        readonly VoiceClassifier classifier = new VoiceClassifier(Sounds);
        public VoiceClassifier Classifier => classifier;
        readonly MidiBlock block = new MidiBlock();
        readonly KeyLock keyLock = new KeyLock();
        readonly NoteOffQueue offs = new NoteOffQueue(32);
        readonly OnsetTrainer trainer = new OnsetTrainer(VoiceFeatures.Dimension);
        readonly HitQueue pending = new HitQueue(16);
        readonly MelodyNotes melody;

        int rate = 48000;
        PitchTracker[] trackers;
        PitchTracker tracker;
        PitchEstimate[] frames;
        OnsetDetector onsets;
        OnsetEvent[] onsetBuf;
        VoiceFeatures feats;
        DrumHits drums;
        float[] ring;
        int ringMask;
        long total;
        readonly float[] feat = new float[VoiceFeatures.Dimension];

        // Settings read once per block.
        bool drumMode, drumLevelGate;
        int mode = -1, range = -1;
        float thresholdLin, thresholdDb, velSoftDb, velLoudDb;
        int velMin, trigLenSamples, drumCh;
        readonly long[] lastHit = new long[Sounds];

        public int ActiveMask { get; private set; } = Scales.All;
        public int ActiveRoot => keyLock.ActiveRoot;
        public int ActiveScale => keyLock.ActiveScale;
        public KeyFinder Finder => keyLock.Finder;

        // For the editor and the readings (any thread).
        public const int TrailLength = 1024;
        public readonly float[] Trail = new float[TrailLength];
        public volatile int TrailHead;
        int trailAcc;
        public volatile float LevelDb = -120, SungMidi;
        public volatile bool Voiced;
        float lvl;
        public readonly bool[] Lit = new bool[Sounds];
        public readonly long[] LitAt = new long[Sounds];
        public int LastHit { get; private set; }
        public int HitSerial { get; private set; }
        public long EventsSent => block.EventsSent;
        public long Total => Volatile.Read(ref total);
        public int Rate => rate;
        public MelodyNotes Melody => melody;
        public PitchTracker CurrentTracker => tracker;
        public bool DrumMode => drumMode;

        public int Learning => trainer.Learning;
        public int LearnTarget { get => trainer.Target; set => trainer.Target = value; }
        public bool LearnDone => trainer.Done;
        public int LearnedThisPass => trainer.Learned;
        /// <summary>The hits a calibration pass asks for per drum.</summary>
        public const int CalibrationHits = 5;

        public AudioMidiEngine(ParamValues values)
        {
            Values = values;
            melody = new MelodyNotes(block);
            trainer.Target = CalibrationHits;
            SetSampleRate(48000);
        }

        public void SetSongKey(string text) => keyLock.SetSongKey(text);

        public void SetSampleRate(int sampleRate)
        {
            rate = Math.Clamp(sampleRate, 8000, 192000);
            trackers = new[]
            {
                new PitchTracker(rate, PitchRange.Guitar), new PitchTracker(rate, PitchRange.Bass), new PitchTracker(rate, PitchRange.Full),
            };
            tracker = trackers[0];
            int maxFrames = 0;
            foreach (var t in trackers) maxFrames = Math.Max(maxFrames, t.MaxFrames(MaxBlock));
            frames = new PitchEstimate[maxFrames + 4];
            onsets = new OnsetDetector(rate);
            onsetBuf = new OnsetEvent[onsets.MaxEvents(MaxBlock) + 4];
            feats = new VoiceFeatures(rate, VoiceFeatures.FastSeconds);
            drums = new DrumHits(rate);
            int size = 1;
            while (size < rate / 4) size <<= 1;
            ring = new float[Math.Max(16384, size)];
            ringMask = ring.Length - 1;
            total = 0;
            melody.Rate = rate;
            melody.Ring = ring; melody.RingMask = ringMask;
            mode = -1; range = -1;
            Reset();
        }

        public void Reset()
        {
            foreach (var t in trackers) t.Reset();
            onsets.Reset(); drums.Reset(); melody.Reset();
            Array.Clear(ring, 0, ring.Length);
            total = 0; pending.Clear(); offs.Clear(); lvl = 0;
            for (int i = 0; i < Sounds; i++) lastHit[i] = long.MinValue / 2;
        }

        /// <summary>Seconds from the start of a note of <paramref name="hz"/> to its note on at best: the pitch window plus the wait (Fast, at most) or Settle (Stable).</summary>
        public double NoteLatencySeconds(double hz = 196) => (tracker ?? trackers[0]).DetectionSeconds(hz) + (melody.Fast ? MelodyNotes.FastWaitMs * 0.001 : melody.SettleSamples / (double)rate);

        /// <summary>The delay of a drum note after its hit: the hit's slice (the detector's lead and the features' length) and the detection.</summary>
        public double HitLatencySeconds => (feats.Length - feats.Lead) / (double)rate;

        // ---------------------------------------------------------------------------------------------------------- settings

        float V(int i) => (float)Values.Table[i].Value(Values[i]);
        int I(int i) => (int)Math.Round(Values.Table[i].Value(Values[i]));
        bool B(int i) => Values.Table[i].Value(Values[i]) > 0;

        void ReadSettings()
        {
            int m = I(P.Mode), r = I(P.Range);
            if (m != mode)
            {
                bool wasMelody = mode == 0;
                mode = m; drumMode = m == 1;
                if (wasMelody) melody.End(total);
                offs.Flush(block, MidiStream.Trigger, total);
                pending.Clear();
                tracker.Reset(); onsets.Reset();
                lvl = 0;
            }
            if (r != range)
            {
                range = r;
                var want = trackers[Math.Clamp(r, 0, 2)];
                if (!ReferenceEquals(want, tracker)) { tracker = want; want.Reset(); melody.End(total); }
            }
            thresholdDb = V(P.Level);
            thresholdLin = (float)PortableMath.Pow(10.0, thresholdDb / 20.0);
            velMin = Math.Min(I(P.VelMin), I(P.Fixed));
            velSoftDb = V(P.Soft);
            velLoudDb = Math.Max(V(P.Loud), velSoftDb + 1f);
            trigLenSamples = (int)(V(P.Length) * 0.001 * rate);
            drumCh = I(P.DrumCh) - 1;
            if (drumMode)
            {
                onsets.Threshold = DrumTuning.ThrBase - DrumTuning.ThrSlope * V(P.Sensitivity);
                onsets.RobustSpread = DrumTuning.Robust != 0;
                onsets.MinRatio = DrumTuning.MinRatio;
                double minGap = Math.Min(V(P.KickGap), Math.Min(V(P.SnareGap), V(P.HatGap)));
                onsets.MinIntervalSeconds = Math.Max(0.015, minGap * 0.001);
            }
            else
            {
                onsets.Threshold = 8f - 6f * V(P.Sensitivity);
                onsets.RobustSpread = false; onsets.MinRatio = 2.2f;
                onsets.MinIntervalSeconds = 0.045;
            }

            var ml = melody;
            ml.Fast = I(P.Response) == 0;
            ml.Retrigger = B(P.Retrigger);
            ml.Legato = B(P.Legato);
            ml.Octave = I(P.Octave);
            ml.Channel = I(P.NoteCh) - 1;
            ml.OnLevel = thresholdLin;
            ml.ReleaseDb = V(P.Release);
            ml.StickSemis = V(P.Stick) / 100f;
            ml.SettleSamples = (int)(V(P.Settle) * 0.001 * rate);
            ml.MinNoteSamples = (int)(V(P.MinNote) * 0.001 * rate);
            ml.FixedVel = I(P.VelMode) == 1;
            ml.FixedVelocity = I(P.Fixed);
            ml.VelMin = velMin; ml.SoftDb = velSoftDb; ml.LoudDb = velLoudDb;
            ml.Window = tracker.Window; ml.Hop = tracker.Hop; ml.MinHz = tracker.MinHz; ml.MaxHz = tracker.MaxHz;
            ml.Bend.Range = I(P.Bend);
            ml.Bend.Anchored = I(P.BendMode) == 1;
            ml.Bend.AnchorSemis = V(P.Anchor) / 100f;
            ml.ActiveMask = ActiveMask = keyLock.Compute(I(P.Lock), I(P.Root), I(P.Scale), Values[P.Custom], 0);
        }

        // ---------------------------------------------------------------------------------------------------------- the block

        /// <summary>Processes <paramref name="n"/> mono samples (at most <see cref="MaxBlock"/>) and returns the events (sorted by Offset) in <see cref="Events"/>.</summary>
        public int Process(float[] x, int n)
        {
            if (n > MaxBlock) n = MaxBlock;
            block.Begin(total, n);
            ReadSettings();
            for (int i = 0; i < n; i++) ring[(int)((total + i) & ringMask)] = x[i];
            long end = total + n;

            double ss = 0;
            for (int i = 0; i < n; i++) ss += (double)x[i] * x[i];
            float rms = (float)Math.Sqrt(ss / Math.Max(1, n));
            float ka = (float)(1.0 - PortableMath.Exp(-n / (0.005 * rate))), kr = (float)(1.0 - PortableMath.Exp(-n / (0.06 * rate)));
            lvl += (rms > lvl ? ka : kr) * (rms - lvl);
            LevelDb = lvl > 1e-6f ? 20f * PortableMathF.Log10(lvl) : -120f;

            int found = onsets.Process(x, 0, n, onsetBuf, 0);
            if (!drumMode)
            {
                for (int i = 0; i < found; i++) melody.Onset(in onsetBuf[i]);
                int got = tracker.Process(x, 0, n, frames, 0);
                for (int i = 0; i < got; i++)
                {
                    ref var e = ref frames[i];
                    Voiced = e.Voiced; SungMidi = e.Voiced ? (float)e.Midi : 0f;
                    lastVoiced = e.Voiced; lastLevel = e.Rms;
                    EstimateProbe?.Invoke(in e);
                    melody.Estimate(in e);
                    if (e.Voiced && e.Rms >= thresholdLin) keyLock.Finder.Add((int)Math.Round(e.Midi), tracker.Hop / (double)rate);
                }
                melody.CheckOff(end);
                keyClock += n / (double)rate;
                if (keyClock >= 1.0) { keyClock = 0; keyLock.Finder.Update(); }
            }
            else
            {
                drums.Push(x, n);
                for (int i = 0; i < found; i++) if (!pending.Add(in onsetBuf[i])) break;
                ServiceHits(end);
                offs.Service(block, MidiStream.Trigger, end);
            }

            int step = Math.Max(1, rate / 200);
            trailAcc += n;
            if (trailAcc >= step)
            {
                float v = !drumMode && lastVoiced && lastLevel >= thresholdLin ? SungMidi : 0f;
                int head = TrailHead;
                while (trailAcc >= step) { Trail[head % TrailLength] = v; head++; trailAcc -= step; }
                TrailHead = head % (TrailLength * 1024);
            }
            Volatile.Write(ref total, end);
            block.SortByOffset();
            return block.Count;
        }

        bool lastVoiced;
        float lastLevel;
        double keyClock;

        public MidiEvent[] Events => block.Events;

        /// <summary>Something is still sounding or waiting (a note, a drum note's off, a hit being named): the machine keeps running.</summary>
        public bool Busy => melody.Busy || offs.Count > 0 || pending.Count > 0;

        // ---------------------------------------------------------------------------------------------------------- drums

        /// <summary>For the evaluation tool only: sees every hit with its bands before it is named.</summary>
        public static ProbeHandler Probe;
        public delegate void EstimateHandler(in PitchEstimate e);
        /// <summary>For the evaluation tool only: sees every pitch estimate.</summary>
        public static EstimateHandler EstimateProbe;
        public static float CalConfidence = 0.95f;
        public delegate void ProbeHandler(in OnsetEvent o, HitBands bands);

        void ServiceHits(long now)
        {
            while (pending.TakeReady(now, feats.Lead, feats.Length, out var o, out long ready))
            {
                if (Learning >= 0)
                {
                    int learn = Learning;
                    feats.Extract(ring, ringMask, o.Sample, feat);
                    LitAt[learn] = total; Lit[learn] = true;
                    trainer.Offer(learn, feat);
                    continue;
                }
                if (o.LevelDb < thresholdDb) continue;
                if (Probe != null) Probe(in o, drums.Analyse(o.Sample));
                float sK = V(P.KickSens), sS = V(P.SnareSens), sH = V(P.HatSens);
                int mask;
                if (classifier.ClassesTrained >= Sounds)
                {
                    feats.Extract(ring, ringMask, o.Sample, feat);
                    var r = classifier.Classify(feat);
                    float sens = r.Class == 0 ? sK : r.Class == 1 ? sS : sH;
                    if (r.Class >= 0 && r.Confidence >= CalConfidence - 0.5f * sens) mask = 1 << r.Class;
                    else
                    {
                        var hb = drums.Analyse(o.Sample);
                        mask = drums.Rules(in hb, sK, sS, sH);
                    }
                }
                else
                {
                    var hb = drums.Analyse(o.Sample);
                    mask = drums.Rules(in hb, sK, sS, sH);
                }
                long at = Math.Max(ready, o.ReportedAt);
                for (int c = 0; c < Sounds; c++)
                    if ((mask & (1 << c)) != 0) Hit(c, in o, at);
            }
        }

        void Hit(int cls, in OnsetEvent o, long at)
        {
            int first = P.KickNote + 4 * cls;
            long gap = (long)(V(first + 3) * 0.001 * rate);
            if (o.Sample - lastHit[cls] < gap) return;
            lastHit[cls] = o.Sample;
            int note = I(first), fixedV = I(first + 1);
            int vel = fixedV > 0 ? fixedV : VelocityMap.FromHit(o.LevelDb, velMin, velSoftDb, velLoudDb);
            offs.Play(block, MidiStream.Trigger, drumCh, note, vel, at, trigLenSamples, Math.Min(at, o.Sample));
            LastHit = cls + 1; HitSerial++;
            Lit[cls] = true; LitAt[cls] = at;
        }

        // ---------------------------------------------------------------------------------------------------------- calibration

        /// <summary>Consumer (the machine's pump): the next calibration example, or false.</summary>
        public bool TakeLearned(out int cls, float[] features) => trainer.Take(out cls, features);
        /// <summary>Starts a pass for one drum (0 kick, 1 snare, 2 hat): the next five hits are filed as its examples.</summary>
        public void StartLearning(int sound) => trainer.Start(sound);
        public void StopLearning() => trainer.Stop();

        // ---------------------------------------------------------------------------------------------------------- flush

        /// <summary>Ends everything sounding (transport stop, the machine going quiet, a new target): the events are in <see cref="Events"/>.</summary>
        public int Flush()
        {
            block.Begin(total, 1);
            melody.End(total);
            offs.Flush(block, MidiStream.Trigger, total);
            return block.Count;
        }

        // ---------------------------------------------------------------------------------------------------------- readings

        public IReadOnlyDictionary<string, double> Readings()
        {
            int trained = 0;
            for (int c = 0; c < Sounds; c++) if (classifier.ExamplesOf(c) > 0) trained++;
            return new Dictionary<string, double>
            {
                ["note"] = melody.Sounding ? melody.CurrentNote + 12 * melody.Octave : 0,
                ["sung_midi"] = SungMidi,
                ["voiced"] = Voiced ? 1 : 0,
                ["level_db"] = LevelDb,
                ["last_hit"] = LastHit,
                ["calibrated_drums"] = trained,
                ["note_latency_ms"] = NoteLatencySeconds() * 1000.0,
            };
        }
    }
}
