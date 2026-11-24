#nullable disable
// Neo Tune (docs/specs/neo-tune.md): pitch correction on the shared pitch tracker (docs/specs/pitch.md). NT2 splits the work in three:
//  - Control stage (TuneControl.cs): per tracker estimate, the pitch is split into the note CENTRE and the expression around it. The
//    centre is a moving average over one measured vibrato period (zero crossings of the deviation), so a 4-8 Hz vibrato does not leak
//    into it; the target note is chosen on the centre (cents past the midpoint and a minimum time, the Stickiness control), and the
//    shift applied to the INPUT is goal - centre, weighted by the correction zone and by how fast the centre moves, smoothed by the
//    retune speed (slower on a sustained note: Humanize), plus (1 - Keep) of the expression to flatten it. The singer's vibrato,
//    drift and scoops pass through untouched at high Natural; at Natural 0 everything is flattened and the target snaps.
//  - Marks (here): one analysis mark per period of a voiced stretch, placed by waveform correlation; shared by both engines.
//  - Engines (TuneSynth.cs): SPLICE resamples the input at the pitch ratio from a delay line and keeps the reader in step by repeating or
//    dropping whole periods at the marks (the method of US 5,973,252: no grain overlap, no comb filtering, formants move with the ratio, which is
//    inaudible for corrections under about 1 semitone); PSOLA (grains of two periods at the true mark intervals times the ratio) is for
//    formant control and large transposes. Method Auto picks by the settings. Both read the input through a 16-tap sinc (TuneSinc.cs).
//  - Voicing continuity: a gap in the tracker's voicing of less than 70 ms inside a note keeps the last pitch and ratio; at real unvoiced
//    sounds and silence the ratio ramps to 1 in 20 ms. The output is never the dry voice inside a voiced note.
// Allocation-free after SetSampleRate; the portable maths, fixed order; the same bits for any block size.

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal sealed partial class TuneEngine
    {
        public const int MaxBlock = 256;

        // The three ranges: longest grain (seconds) and latency (seconds). The latency is the tracker's: its span and a period.
        static readonly double[] CapSeconds = { 0.008, 0.024, 0.028 };
        static readonly double[] LatencySeconds = { 0.010, 0.028, 0.036 };

        const int ContourBits = 12, ContourSize = 1 << ContourBits, ContourMask = ContourSize - 1;
        const int MarkSize = 1024, MarkMask = MarkSize - 1;
        const int DisplaySize = 512;
        const int HannSize = 1024;

        readonly ParamValues values;
        readonly FxStage fx;
        double sr = 48000;
        PitchTracker[] trackers;
        OnsetDetector onsets;
        PitchEstimate[] frames;
        OnsetEvent[] events;
        int range = 1;
        int latency;
        double capSamples;

        // ---- rings (absolute sample time t at index t & ringMask)
        float[] ring, dryL, dryR;
        int ringMask;
        long inCount;
        long outPos;
        readonly float[] mono = new float[MaxBlock];

        // ---- contour (the tracker's f0 and the shift in semitones, stamped at the middle of the audio an estimate heard)
        readonly long[] cT = new long[ContourSize];
        readonly float[] cF = new float[ContourSize], cShift = new float[ContourSize];
        long cCount;
        long gPtr, sPtr, mPtr, mScan, rPtr;

        // ---- analysis marks
        readonly double[] mT = new double[MarkSize];
        readonly int[] mRun = new int[MarkSize];
        long mCount;
        bool markRun;
        int runId;
        readonly float[] refw = new float[512];
        double[] score = new double[1];

        // ---- gate (the Transients dry hold)
        double gate;
        double gateStep;
        long holdFrom, holdUntil = long.MinValue;

        // ---- settings (Update)
        double retuneSeconds, humanize, amount, vibKeep, vibDepth, vibRate, vibDelay, fshift, zoneCents, glide, stick;
        int transpose, scale, rootKey, customMask, source, target, midiOctave, inputSel, method;
        bool preserve, transients, useSplice;
        double mix;

        // ---- MIDI target
        readonly object midiLock = new object();
        readonly int[] midiQueue = new int[256];
        int midiRead, midiWrite;
        readonly bool[] held = new bool[128];
        int heldCount;

        // ---- published
        double pubInHz, pubNote, pubCorrection, pubCentre, pubZone;
        int pubVoiced, pubMask;
        readonly float[] dispIn = new float[DisplaySize], dispOut = new float[DisplaySize], dispCentre = new float[DisplaySize];
        long dispCount;
        double dispClock;
        double dispIn0, dispOut0, dispCentre0;
        double dispStep;

        /// <summary>What Amount scales (docs/specs/machines.md, "Mix and Amount").</summary>
        public static readonly AmountRule[] AmountRules =
        {
            // The strength of the correction (its own Amount control, 0-100 %) scales, capped at full; the retune time shortens towards instant as it grows.
            AmountRule.Fun(P.Amount, (raw, a) => raw.Value(P.Amount) * a, 0, 1),
            AmountRule.Lin(P.Retune, 400),
        };

        public TuneEngine(ParamValues values)
        {
            fx = new FxStage(values, P.FxAmount, -1, AmountRules);
            this.values = fx.Effective;
            hann = new float[HannSize + 2];
            for (int i = 0; i <= HannSize + 1; i++)
                hann[i] = i > HannSize ? 0f : (float)(0.5 * (1.0 + PortableMath.Cos(Math.PI * i / HannSize)));
        }

        // ------------------------------------------------------------------ public surface

        /// <summary>The audio is delayed by this many samples (the lookahead of the range in use), reported to the app.</summary>
        public int Latency => latency;

        public double InputHz => Volatile.Read(ref pubInHz);
        public double TargetNote => Volatile.Read(ref pubNote);
        public double CorrectionCents => Volatile.Read(ref pubCorrection);
        public bool Voiced => Volatile.Read(ref pubVoiced) != 0;
        /// <summary>The note centre now as a MIDI number (0 when not voiced): the pitch with the vibrato taken out.</summary>
        public double CentreMidi => Volatile.Read(ref pubCentre);
        /// <summary>The correction zone around the target, cents (100 and over: none).</summary>
        public double ZoneCents => Volatile.Read(ref pubZone);
        /// <summary>True while the splice engine is the one in use (Method Auto picks it for small corrections without formant or transpose).</summary>
        public bool UsingSplice => useSplice;

        /// <summary>The notes the voice may land on now (a mask of pitch classes, C = bit 0), for the editor's keyboard and roll.</summary>
        public int NoteMask => Volatile.Read(ref pubMask);

        /// <summary>The key's root now (the Key parameter, or what Listen has heard).</summary>
        public int Root => listenRoot >= 0 && values.Choice(P.Source) == 1 ? listenRoot : values.Choice(P.Key);

        /// <summary>The newest history entry's index (entries are written at 60 per second, newest last), for the editor.</summary>
        public long HistoryCount => Volatile.Read(ref dispCount);
        public const int HistorySize = DisplaySize;
        public double HistoryRate => 60;

        /// <summary>Input pitch (MIDI, 0 when unvoiced) and corrected pitch of the history entry <paramref name="index"/> (absolute, newest = HistoryCount - 1).</summary>
        public void History(long index, out float input, out float corrected)
        {
            int i = (int)(index & (DisplaySize - 1));
            input = dispIn[i];
            corrected = dispOut[i];
        }

        /// <summary>The note centre (MIDI, 0 when unvoiced) of a history entry.</summary>
        public float HistoryCentre(long index) => dispCentre[(int)(index & (DisplaySize - 1))];

        public IReadOnlyDictionary<string, double> Readings() => new Dictionary<string, double>
        {
            ["input_hz"] = Math.Round(InputHz, 2),
            ["target_note"] = Math.Round(TargetNote, 2),
            ["correction_cents"] = Math.Round(CorrectionCents, 1),
            ["voiced"] = Voiced ? 1 : 0,
            ["latency_samples"] = latency,
            ["centre_note"] = Math.Round(CentreMidi, 2),
            ["engine"] = useSplice ? 0 : 1,
        };

        /// <summary>A MIDI note for the MIDI target (velocity 0 releases).</summary>
        public void MidiNote(int note, int velocity)
        {
            if (note < 0 || note > 127) return;
            lock (midiLock)
            {
                int next = (midiWrite + 1) & 255;
                if (next == midiRead) return;
                midiQueue[midiWrite] = velocity > 0 ? note + 1 : -(note + 1);
                midiWrite = next;
            }
        }

        public void AllNotesOff()
        {
            lock (midiLock)
            {
                int next = (midiWrite + 1) & 255;
                if (next == midiRead) return;
                midiQueue[midiWrite] = 0;
                midiWrite = next;
            }
        }

        public void SetSampleRate(float rate)
        {
            fx.SetSampleRate(rate);
            sr = rate;
            trackers = new[]
            {
                new PitchTracker(sr, PitchRange.Voice, PitchQuality.LowLatency),
                new PitchTracker(sr, PitchRange.Voice, PitchQuality.Quality),
                new PitchTracker(sr, PitchRange.Full, PitchQuality.Quality),
            };
            int frameMax = 0;
            for (int i = 0; i < trackers.Length; i++) frameMax = Math.Max(frameMax, trackers[i].MaxFrames(MaxBlock));
            frames = new PitchEstimate[frameMax];
            onsets = new OnsetDetector(sr) { MinIntervalSeconds = 0.1 };
            events = new OnsetEvent[onsets.MaxEvents(MaxBlock)];
            int rs = 1 << 15;
            while (rs < (int)(0.4 * sr)) rs <<= 1;
            ring = new float[rs]; dryL = new float[rs]; dryR = new float[rs];
            ringMask = rs - 1;
            hgMax = 0.040 * sr;
            score = new double[(int)(0.4 * sr / 20) + 8];
            scoreJ = new double[(int)(0.3 * sr / 20) + 16];
            int os = 1 << 12;
            while (os < (int)(2 * hgMax + 64)) os <<= 1;
            num = new float[os]; den = new float[os];
            olaMask = os - 1;
            gateStep = 1.0 / (0.0025 * sr);
            dispStep = 1.0 / 60.0;
            Update(true);
            Reset();
        }

        public void Reset()
        {
            fx.Reset();
            Array.Clear(ring, 0, ring.Length);
            Array.Clear(dryL, 0, dryL.Length);
            Array.Clear(dryR, 0, dryR.Length);
            Array.Clear(num, 0, num.Length);
            Array.Clear(den, 0, den.Length);
            for (int i = 0; i < trackers.Length; i++) trackers[i].Reset();
            onsets.Reset();
            inCount = 0;
            cCount = 0; gPtr = sPtr = mPtr = mScan = rPtr = 0;
            mCount = 0; markRun = false; runId = 0; synthActive = false; synthRun = 0;
            gate = 0; holdUntil = long.MinValue; holdFrom = 0;
            ResetControl();
            outPos = -latency;
            ResetSplice();
            pubInHz = pubNote = pubCorrection = pubCentre = 0; pubVoiced = 0;
            dispCount = 0; dispClock = 0; dispIn0 = dispOut0 = dispCentre0 = 0;
            Array.Clear(dispIn, 0, DisplaySize);
            Array.Clear(dispOut, 0, DisplaySize);
            Array.Clear(dispCentre, 0, DisplaySize);
            lock (midiLock) { midiRead = midiWrite = 0; }
            Array.Clear(held, 0, 128);
            heldCount = 0;
        }

        // ------------------------------------------------------------------ parameters

        bool naturalDrives;
        int naturalWrite;

        /// <summary>Whether Natural alone changed since the last call (then the six fine controls follow it); true once per change. For the machine, to show the six new values.</summary>
        public bool TakeNaturalWrite(out double natural)
        {
            natural = values.Value(P.Natural);
            return Interlocked.Exchange(ref naturalWrite, 0) == 1;
        }

        /// <summary>True while the six fine controls follow Natural (it changed alone and none of them has been set since).</summary>
        public bool NaturalDrives => naturalDrives;

        /// <summary>The note stickiness in force (0 to 1).</summary>
        internal double StickValue => stick;

        /// <summary>The correction zone in force (cents).</summary>
        internal double ZoneValue => zoneCents;

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            int r = values.Choice(P.Range);
            // Natural as a parameter (docs/specs/neo-tune.md 11.1): in a tick where Natural changed and none of its six fine controls did (automation, a pattern, a
            // knob card, a MIDI binding), the six follow Natural's map until one of them is set again. If any fine control changed in the same tick (a song load, a
            // preset, the editor's one-step write) the fine values win, so what was saved is what plays.
            bool fineDirty = false;
            foreach (int c in TuneNatural.Controls) if (values.IsDirty(c)) fineDirty = true;
            if (fineDirty) naturalDrives = false;
            else if (values.IsDirty(P.Natural)) { naturalDrives = true; Volatile.Write(ref naturalWrite, 1); }
            double nat = values.Value(P.Natural);
            double Fine(int i) => naturalDrives ? TuneNatural.Value(i, nat) : values.Value(TuneNatural.Controls[i]);
            retuneSeconds = Fine(0) * 0.001;
            humanize = Fine(3);
            amount = values.Value(P.Amount);
            transpose = (int)values.Value(P.Transpose);
            preserve = values.On(P.Preserve);
            int fst = (int)values.Value(P.Formant);
            fshift = PortableMath.Exp2(fst / 12.0);
            vibKeep = Fine(2);
            vibDepth = values.Value(P.VibDepth);
            vibRate = values.Value(P.VibRate);
            vibDelay = values.Value(P.VibDelay) * 0.001;
            zoneCents = Fine(1);
            glide = Fine(4);
            stick = Fine(5);
            method = values.Choice(P.Method);
            scale = values.Choice(P.Scale);
            rootKey = values.Choice(P.Key);
            customMask = (int)values.Value(P.Notes);
            source = values.Choice(P.Source);
            target = values.Choice(P.Target);
            midiOctave = values.Choice(P.MidiOctave);
            inputSel = values.Choice(P.Input);
            transients = values.On(P.Transients);
            mix = values.Value(P.Mix);
            bool splice = method == 1 || (method == 0 && fst == 0 && Math.Abs(transpose) <= 2 && values.Choice(P.Target) == 0);
            bool change = false;
            if (r != range || force)
            {
                change = r != range;
                range = Math.Clamp(r, 0, 2);
                latency = (int)Math.Round(LatencySeconds[range] * sr);
                capSamples = CapSeconds[range] * sr;
            }
            if (splice != useSplice) { useSplice = splice; change = !force; if (evOn) Log(outPos, TuneEventKind.MethodSwitch, splice ? 1 : 0); }
            if (change && ring != null) Reset();
            values.Clean();
        }

        // ------------------------------------------------------------------ processing

        /// <summary>Corrects a stereo block in place: the wet is the voice, mono, on both channels, blended with the delayed input.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            fx.Begin(left, right, n);
            ProcessCore(left, right, n);
            fx.End(left, right, n, Latency);
        }

        void ProcessCore(float[] left, float[] right, int n)
        {
            Update(false);
            int done = 0;
            while (done < n)
            {
                int c = Math.Min(MaxBlock, n - done);
                Chunk(left, right, done, c);
                done += c;
            }
        }

        void Chunk(float[] left, float[] right, int offset, int c)
        {
            DrainMidi();
            // 1. the rings
            bool sum = inputSel == 0;
            for (int i = 0; i < c; i++)
            {
                int idx = (int)((inCount + i) & ringMask);
                float l = left[offset + i], r = right[offset + i];
                dryL[idx] = l;
                dryR[idx] = r;
                float m = sum ? 0.5f * (l + r) : l;
                ring[idx] = m;
                mono[i] = m;
            }
            inCount += c;

            // 2. tracking and the control stage
            var tracker = trackers[range];
            int nf = tracker.Process(mono, 0, c, frames, 0);
            for (int f = 0; f < nf; f++) Control(in frames[f], tracker);
            int ne = onsets.Process(mono, 0, c, events, 0);
            for (int e = 0; e < ne; e++)
            {
                long at = events[e].Sample;
                // Dry from a little before the attack (as far before as the lookahead lets the detector's report come in time) to 8 ms after it.
                double lead = Math.Clamp(latency / sr - 0.0090, 0.001, 0.004);
                long from = at - (long)(lead * sr);
                if (from > holdUntil) holdFrom = from;
                holdUntil = Math.Max(holdUntil, at + (long)(0.008 * sr));
            }

            // 3. marks, then the engine and the output
            GenerateMarks();
            long tEnd = inCount - latency;
            if (!useSplice) PlaceGrains(tEnd);
            Emit(left, right, offset, c, tEnd);
        }

        // ------------------------------------------------------------------ MIDI

        void DrainMidi()
        {
            if (midiRead == midiWrite) return;
            lock (midiLock)
            {
                while (midiRead != midiWrite)
                {
                    int v = midiQueue[midiRead];
                    midiRead = (midiRead + 1) & 255;
                    if (v == 0) { Array.Clear(held, 0, 128); heldCount = 0; }
                    else if (v > 0) { if (!held[v - 1]) { held[v - 1] = true; heldCount++; } }
                    else if (held[-v - 1]) { held[-v - 1] = false; heldCount--; }
                }
            }
        }

        int HeldMask()
        {
            int m = 0;
            for (int n = 0; n < 128; n++) if (held[n]) m |= 1 << (n % 12);
            return m;
        }

        int EffectiveMask()
        {
            if (target == 1) return HeldMask();
            int root = rootKey;
            if (source == 1 && scale != TuneScales.Custom && scale != 0 && listenRoot >= 0) root = listenRoot;
            return TuneScales.Mask(scale, root, customMask);
        }

        // ------------------------------------------------------------------ the contour

        /// <summary>The tracker's f0 and the shift at time t (the entry before it and the next, blended when both are voiced).</summary>
        bool ContourAt(double t, ref long ptr, out double f, out double shift)
        {
            f = 0; shift = 0;
            if (cCount == 0) return false;
            long floor = Math.Max(0, cCount - ContourSize + 8);
            if (ptr < floor) ptr = floor;
            while (ptr + 1 < cCount && cT[(ptr + 1) & ContourMask] <= t) ptr++;
            while (ptr > floor && cT[ptr & ContourMask] > t) ptr--;
            int s0 = (int)(ptr & ContourMask);
            double t0 = cT[s0];
            if (ptr + 1 < cCount)
            {
                int s1 = (int)((ptr + 1) & ContourMask);
                double t1 = cT[s1];
                bool v0 = cF[s0] > 0, v1 = cF[s1] > 0;
                if (v0 && v1)
                {
                    double u = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 0;
                    f = cF[s0] + u * (cF[s1] - cF[s0]);
                    shift = cShift[s0] + u * (cShift[s1] - cShift[s0]);
                    return true;
                }
                bool nearest1 = t1 - t < t - t0;
                int s = nearest1 ? s1 : s0;
                if (cF[s] > 0) { f = cF[s]; shift = cShift[s]; return true; }
                return false;
            }
            if (cF[s0] > 0) { f = cF[s0]; shift = cShift[s0]; return true; }
            return false;
        }

        /// <summary>The shift at time t whether or not the voice is voiced there (the unvoiced entries carry the ramp to 0), and whether it is.</summary>
        bool ShiftAt(double t, ref long ptr, out double shift)
        {
            shift = 0;
            if (cCount == 0) return false;
            long floor = Math.Max(0, cCount - ContourSize + 8);
            if (ptr < floor) ptr = floor;
            while (ptr + 1 < cCount && cT[(ptr + 1) & ContourMask] <= t) ptr++;
            while (ptr > floor && cT[ptr & ContourMask] > t) ptr--;
            int s0 = (int)(ptr & ContourMask);
            if (ptr + 1 < cCount)
            {
                int s1 = (int)((ptr + 1) & ContourMask);
                double t0 = cT[s0], t1 = cT[s1];
                double u = t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 0;
                shift = cShift[s0] + u * (cShift[s1] - cShift[s0]);
                return (u < 0.5 ? cF[s0] : cF[s1]) > 0;
            }
            shift = cShift[s0];
            return cF[s0] > 0;
        }

        // ------------------------------------------------------------------ analysis marks

        float X(long t) => (t < 0 || t >= inCount || t < inCount - ring.Length + 4) ? 0f : ring[(int)(t & ringMask)];

        void AddMark(double t)
        {
            int i = (int)(mCount & MarkMask);
            if (evOn && mCount > 1 && mRun[(int)((mCount - 1) & MarkMask)] == runId && mRun[(int)((mCount - 2) & MarkMask)] == runId)
            {
                double s1 = t - mT[(int)((mCount - 1) & MarkMask)], s0 = mT[(int)((mCount - 1) & MarkMask)] - mT[(int)((mCount - 2) & MarkMask)];
                if (s1 > 1.12 * s0 || s1 < 0.88 * s0) Log((long)t, TuneEventKind.MarkOutlier, s1, s0);
            }
            mT[i] = t;
            mRun[i] = runId;
            mCount++;
        }

        void GenerateMarks()
        {
            for (int guard = 0; guard < 64; guard++)
            {
                if (!markRun)
                {
                    // Look for the start of a voiced stretch in the contour entries not yet scanned.
                    long floor = Math.Max(0, cCount - ContourSize + 8);
                    if (mScan < floor) mScan = floor;
                    while (mScan < cCount && cF[(int)(mScan & ContourMask)] <= 0) mScan++;
                    if (mScan >= cCount) return;
                    int s = (int)(mScan & ContourMask);
                    double pin = sr / cF[s];
                    long start = cT[s];
                    double lastEndMark = mCount > 0 ? mT[(int)((mCount - 1) & MarkMask)] : double.MinValue;
                    if (start < lastEndMark + 0.5 * pin) start = (long)Math.Ceiling(lastEndMark + 0.5 * pin);
                    if (start + (long)(1.2 * pin) > inCount - 1) return;
                    // The seed: the highest point of the first period.
                    long best = start;
                    float bv = float.MinValue;
                    long end = start + (long)pin;
                    for (long q = start; q < end; q++)
                    {
                        float v = X(q);
                        if (v > bv) { bv = v; best = q; }
                    }
                    runId++;
                    if (evOn) Log(best, TuneEventKind.MarkRunStart, runId);
                    AddMark(best);
                    markRun = true;
                    mPtr = mScan;
                    continue;
                }
                double last = mT[(int)((mCount - 1) & MarkMask)];
                if (!ContourAt(last + 1, ref mPtr, out double f, out _))
                {
                    // The voice stopped (as far as the contour has got).
                    if (mPtr + 1 < cCount) { markRun = false; mScan = mPtr + 1; if (evOn) Log((long)last, TuneEventKind.MarkRunEnd, runId); continue; }
                    return;
                }
                double p = sr / f;
                // Marks smoothed across transitions: the period of a voice does not change by more than a quarter from one cycle to the next, and a tracker that blips
                // at a note change (an octave error for a few frames, a late estimate) must not lay marks at half or twice the true spacing (the splice would then
                // jump half a period: a click). The spacing of the marks just laid is the reference.
                double prevSp = 0;
                if (mCount >= 2)
                {
                    int i1 = (int)((mCount - 1) & MarkMask), i2 = (int)((mCount - 2) & MarkMask);
                    if (mRun[i1] == mRun[i2]) prevSp = mT[i1] - mT[i2];
                }
                if (prevSp > 8)
                {
                    double ratio = p / prevSp;
                    if (ratio < 0.58 && ratio > 0.42) p *= 2;
                    else if (ratio > 1.7 && ratio < 2.4) p *= 0.5;
                }
                double pred = last + p;
                int hw = Math.Max(4, (int)(0.3 * p)), rg = Math.Max(2, (int)(0.2 * p));
                if (pred + rg + hw + 2 > inCount - 1) return;
                // Is the voice still voiced at the predicted mark?
                long probe = mPtr;
                if (!ContourAt(pred, ref probe, out _, out _) && probe + 1 < cCount) { markRun = false; mScan = probe + 1; if (evOn) Log((long)last, TuneEventKind.MarkRunEnd, runId); continue; }
                double k = Refine(pred, last, hw, rg);
                double minK = last + 0.5 * p;
                if (k < minK) k = minK;
                AddMark(k);
            }
        }

        /// <summary>
        /// The position near <paramref name="pred"/> where the waveform best repeats the one around the previous mark (a normalised
        /// correlation over 0.6 period, a parabola through the best three positions: marks fall between samples, so that a period of
        /// 53.76 samples is not rounded to 54 over and over, which would shift the grains' phase by a quarter sample per period).
        /// </summary>
        double Refine(double pred, double last, int hw, int rg)
        {
            int step = Math.Max(1, hw / 48);
            int terms = 0;
            double eref = 0;
            for (int j = -hw; j <= hw; j += step)
            {
                float v = Interp(last + j);
                refw[terms++] = v;
                eref += (double)v * v;
            }
            if (eref < 1e-12) return pred;
            long centre = (long)Math.Round(pred);
            int n = 2 * rg + 1;
            double best = double.MinValue;
            int bestI = rg;
            for (int di = 0; di < n; di++)
            {
                long k = centre + di - rg;
                double c = 0, e = 0;
                int q = 0;
                for (int j = -hw; j <= hw; j += step)
                {
                    float a = X(k + j);
                    c += (double)a * refw[q++];
                    e += (double)a * a;
                }
                double sc = c / Math.Sqrt(e * eref + 1e-18);
                score[di] = sc;
                double biased = sc - 0.15 * Math.Abs(k - pred) / rg;
                if (biased > best) { best = biased; bestI = di; }
            }
            double frac = 0;
            if (bestI > 0 && bestI < n - 1)
            {
                double a = score[bestI - 1], b = score[bestI], c2 = score[bestI + 1];
                double d = a - 2 * b + c2;
                if (d < -1e-12) frac = Math.Clamp(0.5 * (a - c2) / d, -0.5, 0.5);
            }
            return centre + bestI - rg + frac;
        }

        /// <summary>The sample at a fractional position, by the 16-tap sinc (zeros outside what the ring holds).</summary>
        float Interp(double pos)
        {
            long p = (long)Math.Floor(pos);
            double f = pos - p;
            long lo = Math.Max(0, inCount - ring.Length + 8);
            double ph = f * TuneSinc.Phases;
            int i = (int)ph;
            float u = (float)(ph - i);
            var tab = TuneSinc.Table;
            int b0 = i * TuneSinc.Taps, b1 = b0 + TuneSinc.Taps;
            float s0 = 0f, s1 = 0f;
            if (p - 7 >= lo && p + 8 < inCount)
            {
                int idx = (int)((p - 7) & ringMask);
                for (int k = 0; k < TuneSinc.Taps; k++)
                {
                    float x = ring[(idx + k) & ringMask];
                    s0 += tab[b0 + k] * x;
                    s1 += tab[b1 + k] * x;
                }
            }
            else
            {
                for (int k = 0; k < TuneSinc.Taps; k++)
                {
                    long q = p - 7 + k;
                    if (q < lo || q >= inCount) continue;
                    float x = ring[(int)(q & ringMask)];
                    s0 += tab[b0 + k] * x;
                    s1 += tab[b1 + k] * x;
                }
            }
            return s0 + u * (s1 - s0);
        }
    }
}
