#nullable disable
// Neo Tune (docs/specs/neo-tune.md): pitch correction by pitch-synchronous overlap-add (TD-PSOLA) on the shared pitch tracker
// (docs/specs/pitch.md).
//  - The input (the sum or the left channel) goes through the tracker, one estimate per hop. Each estimate runs the control
//    stage: the note the voice is nearest to (in the key's scale, the notes a MIDI keyboard holds, or the key the voice
//    itself suggests), the glide to it (retune speed, humanize, the singer's vibrato kept or an added one), the amount, the
//    transpose: the result is a shift in semitones, stored in a contour with the tracker's f0, stamped at the middle of the
//    audio the estimate heard.
//  - Analysis marks: one per period of the voiced stretch, each placed where the waveform around it best matches the waveform
//    around the previous mark (a normalised correlation over 0.6 period in a window of ±0.2 period around the predicted
//    position), so every grain starts at the same phase of the cycle.
//  - Synthesis: grain centres are laid at the corrected period, f_out = f_in · 2^(shift/12) (read at the grain's own time, so
//    jitter of the tracker does not reach the output: a hard-tuned note has marks exactly one period of the note apart,
//    placed with fractional-sample accuracy). Each grain is the input around the nearest analysis mark, a Hann window of two
//    periods (at most Pcap: the Low latency and Quality grain lengths), read with a cubic interpolation, and added into an
//    accumulator together with the window's weight; an output sample is the sum over the weights', so the level does not
//    depend on how the grains overlap.
//  - Formants: the grain is read at a speed φ (the window and its content are scaled by 1/φ): φ = 2^(Formant shift/12) with
//    Formant preserve on, and the pitch ratio times that with it off (the spectral envelope then rides the pitch: a sped-up
//    or slowed voice). The envelope is the grain's own, no spectral analysis.
//  - Unvoiced sounds and silence pass through as the delayed input; a gate crossfades (2.5 ms) between that and the grains
//    where the contour says voiced, and an onset detector holds it dry for a few milliseconds at a hard attack (Transients).
//  - Latency (reported): the lookahead the grains and the tracker need: 10 ms for Low latency (grains up to 8 ms), 28 ms
//    for Quality (24 ms) and 36 ms for Full.
// Allocation-free after SetSampleRate; the portable maths, fixed order; the same bits for any block size.

using System;
using System.Collections.Generic;
using System.Threading;
using NeoBuzz.Dsp;

namespace NeoBuzz.Tune
{
    internal sealed class TuneEngine
    {
        public const int MaxBlock = 256;

        // The three ranges: tracker, longest grain (seconds, two of the shortest period it holds whole), latency (seconds).
        static readonly double[] CapSeconds = { 0.008, 0.024, 0.028 };
        static readonly double[] LatencySeconds = { 0.010, 0.028, 0.036 };

        const int ContourBits = 12, ContourSize = 1 << ContourBits, ContourMask = ContourSize - 1;
        const int MarkSize = 1024, MarkMask = MarkSize - 1;
        const int DisplaySize = 512;
        const int HannSize = 1024;

        readonly ParamValues values;
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

        // ---- contour
        readonly long[] cT = new long[ContourSize];
        readonly float[] cF = new float[ContourSize], cShift = new float[ContourSize];
        long cCount;
        long gPtr, sPtr, mPtr, mScan;

        // ---- analysis marks
        readonly double[] mT = new double[MarkSize];
        readonly int[] mRun = new int[MarkSize];
        long mCount;
        bool markRun;
        int runId;
        readonly float[] refw = new float[512];
        double[] score = new double[1];

        // ---- synthesis
        float[] num, den;
        int olaMask;
        bool synthActive;
        int synthRun;
        double sNext;
        readonly float[] hann = new float[HannSize + 2];
        double hgMax;

        // ---- gate
        double gate;
        double gateStep;
        long holdFrom, holdUntil = long.MinValue;

        // ---- control stage
        readonly KeyListener listener = new KeyListener();
        bool wasVoiced;
        double unvoicedFor;
        int curNote = -1;
        double noteTime;
        double slow, slow2, mOut;
        bool smoothInit;
        double lfoPhase;
        double lastEnd;
        int listenRoot = -1;
        double lastMidiIn;

        // ---- MIDI target
        readonly object midiLock = new object();
        readonly int[] midiQueue = new int[256];
        int midiRead, midiWrite;
        readonly bool[] held = new bool[128];
        int heldCount;

        // ---- settings (Update)
        double retuneSeconds, humanize, amount, vibKeep, vibDepth, vibRate, vibDelay, fshift;
        int transpose, scale, rootKey, customMask, source, target, midiOctave, inputSel;
        bool preserve, transients;
        double mix;

        // ---- published
        double pubInHz, pubNote, pubCorrection;
        int pubVoiced, pubMask;
        readonly float[] dispIn = new float[DisplaySize], dispOut = new float[DisplaySize];
        long dispCount;
        double dispClock;
        double dispIn0, dispOut0;
        double dispStep;

        public TuneEngine(ParamValues values)
        {
            this.values = values;
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


        /// <summary>The notes the voice may land on now (a mask of pitch classes, C = bit 0), for the editor's keyboard and roll.</summary>
        public int NoteMask => Volatile.Read(ref pubMask);

        /// <summary>The key's root now (the Key parameter, or what Listen has heard).</summary>
        public int Root => listenRoot >= 0 && values.Choice(P.Source) == 1 ? listenRoot : values.Choice(P.Key);

        /// <summary>The newest history entry's index (entries are written at 60 per second, newest last), for the editor.</summary>
        public long HistoryCount => Volatile.Read(ref dispCount);
        public const int HistorySize = DisplaySize;
        public double HistoryRate => 60;

        /// <summary>Input pitch (MIDI, 0 when unvoiced) and corrected pitch of the history entry <paramref name="index"/> (absolute, newest = HistoryCount − 1).</summary>
        public void History(long index, out float input, out float corrected)
        {
            int i = (int)(index & (DisplaySize - 1));
            input = dispIn[i];
            corrected = dispOut[i];
        }

        public IReadOnlyDictionary<string, double> Readings() => new Dictionary<string, double>
        {
            ["input_hz"] = Math.Round(InputHz, 2),
            ["target_note"] = Math.Round(TargetNote, 2),
            ["correction_cents"] = Math.Round(CorrectionCents, 1),
            ["voiced"] = Voiced ? 1 : 0,
            ["latency_samples"] = latency,
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
            Array.Clear(ring, 0, ring.Length);
            Array.Clear(dryL, 0, dryL.Length);
            Array.Clear(dryR, 0, dryR.Length);
            Array.Clear(num, 0, num.Length);
            Array.Clear(den, 0, den.Length);
            for (int i = 0; i < trackers.Length; i++) trackers[i].Reset();
            onsets.Reset();
            inCount = 0;
            cCount = 0; gPtr = sPtr = mPtr = mScan = 0;
            mCount = 0; markRun = false; runId = 0; synthActive = false; synthRun = 0;
            gate = 0; holdUntil = long.MinValue; holdFrom = 0;
            wasVoiced = false; unvoicedFor = 1; curNote = -1; smoothInit = false; lfoPhase = 0; lastEnd = 0;
            listener.Reset(); listenRoot = -1;
            outPos = -latency;
            pubInHz = pubNote = pubCorrection = 0; pubVoiced = 0;
            dispCount = 0; dispClock = 0; dispIn0 = dispOut0 = 0;
            Array.Clear(dispIn, 0, DisplaySize);
            Array.Clear(dispOut, 0, DisplaySize);
            lock (midiLock) { midiRead = midiWrite = 0; }
            Array.Clear(held, 0, 128);
            heldCount = 0;
        }

        // ------------------------------------------------------------------ parameters

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            int r = values.Choice(P.Range);
            retuneSeconds = values.Value(P.Retune) * 0.001;
            humanize = values.Value(P.Humanize);
            amount = values.Value(P.Amount);
            transpose = (int)values.Value(P.Transpose);
            preserve = values.On(P.Preserve);
            fshift = PortableMath.Exp2(values.Value(P.Formant) / 12.0);
            vibKeep = values.Value(P.VibKeep);
            vibDepth = values.Value(P.VibDepth);
            vibRate = values.Value(P.VibRate);
            vibDelay = values.Value(P.VibDelay) * 0.001;
            scale = values.Choice(P.Scale);
            rootKey = values.Choice(P.Key);
            customMask = (int)values.Value(P.Notes);
            source = values.Choice(P.Source);
            target = values.Choice(P.Target);
            midiOctave = values.Choice(P.MidiOctave);
            inputSel = values.Choice(P.Input);
            transients = values.On(P.Transients);
            mix = values.Value(P.Mix);
            if (r != range || force)
            {
                bool change = r != range;
                range = Math.Clamp(r, 0, 2);
                latency = (int)Math.Round(LatencySeconds[range] * sr);
                capSamples = CapSeconds[range] * sr;
                if (change && ring != null) { Reset(); }
            }
            values.Clean();
        }

        // ------------------------------------------------------------------ processing

        /// <summary>Corrects a stereo block in place: the wet is the voice, mono, on both channels, blended with the delayed input.</summary>
        public void Process(float[] left, float[] right, int n)
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

            // 3. marks, grains, output
            GenerateMarks();
            long tEnd = inCount - latency;
            PlaceGrains(tEnd);
            Emit(left, right, offset, c, tEnd);
        }

        // ------------------------------------------------------------------ the control stage

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

        int PickNote(double m, int mask)
        {
            if (target == 1)
            {
                if (heldCount == 0) return -1;
                if (midiOctave == 0) return TuneScales.Nearest(m, mask);
                int bestN = -1;
                double bestD = 1e9;
                for (int n = 0; n < 128; n++)
                {
                    if (!held[n]) continue;
                    double d = Math.Abs(m - n);
                    if (d < bestD) { bestD = d; bestN = n; }
                }
                return bestN;
            }
            int nearest = TuneScales.Nearest(m, mask);
            if (nearest < 0) return -1;
            // Stay on the current note until another is clearly closer (15 cents past the halfway point).
            if (curNote >= 0 && curNote != nearest && curNote < 128 && ((mask >> (curNote % 12)) & 1) != 0
                && Math.Abs(m - curNote) <= Math.Abs(m - nearest) + 0.15) return curNote;
            return nearest;
        }

        void Control(in PitchEstimate e, PitchTracker tracker)
        {
            double dt = lastEnd == 0 ? tracker.Hop / sr : (e.EndSample - lastEnd) / sr;
            lastEnd = e.EndSample;
            if (dt <= 0 || dt > 0.1) dt = tracker.Hop / sr;
            double tau = e.Voiced && e.F0 > 0 ? sr / e.F0 : sr / Math.Max(e.RawF0, 100.0);
            long centre = e.EndSample - (long)((tracker.Window + tau) * 0.5);

            long slot = cCount & ContourMask;
            cT[slot] = centre;
            if (!e.Voiced)
            {
                unvoicedFor += dt;
                wasVoiced = false;
                cF[slot] = 0; cShift[slot] = 0;
                cCount++;
                Volatile.Write(ref pubVoiced, 0);
                Display(dt, 0, 0);
                return;
            }

            double m = e.Midi;
            if (!wasVoiced && unvoicedFor > 0.12)
            {
                curNote = -1; smoothInit = false; noteTime = 0; lfoPhase = 0;
            }
            unvoicedFor = 0;
            wasVoiced = true;
            lastMidiIn = m;
            listener.Add(m, e.Confidence, dt);
            if (source == 1) listenRoot = listener.Estimate(scale);

            int mask = EffectiveMask();
            Volatile.Write(ref pubMask, mask);
            int note = PickNote(m, mask);
            if (note != curNote)
            {
                curNote = note;
                noteTime = 0;
                if (note >= 0) { double dv = Math.Clamp((m - note) * 100, -200, 200); slow = slow2 = dv; }
            }
            noteTime += dt;

            double outMidi;
            if (curNote < 0)
            {
                // No note to go to (an empty scale, no key held): the voice as sung, glided to the way the note would.
                outMidi = m;
                mOut = m;
                smoothInit = true;
            }
            else
            {
                double dev = Math.Clamp((m - curNote) * 100, -200, 200);
                slow += (dev - slow) * (1 - PortableMath.Exp(-dt / 0.12));
                slow2 += (slow - slow2) * (1 - PortableMath.Exp(-dt / 0.40));
                double drift = slow - slow2;
                double vib = dev - slow;
                double goal = curNote + humanize * drift / 100.0;
                if (!smoothInit || retuneSeconds <= 1e-6) { mOut = goal; smoothInit = true; }
                else mOut += (goal - mOut) * (1 - PortableMath.Exp(-dt / retuneSeconds));
                outMidi = mOut + vibKeep * Math.Clamp(vib, -150, 150) / 100.0;
                if (vibDepth > 0)
                {
                    lfoPhase += vibRate * dt;
                    lfoPhase -= Math.Floor(lfoPhase);
                    double ramp = Math.Clamp((noteTime - vibDelay) / 0.3, 0, 1);
                    outMidi += ramp * vibDepth / 100.0 * PortableMath.Sin(2 * Math.PI * lfoPhase);
                }
            }
            double shift = amount * (outMidi - m) + transpose;
            shift = Math.Clamp(shift, -48, 48);
            cF[slot] = (float)e.F0;
            cShift[slot] = (float)shift;
            cCount++;

            Volatile.Write(ref pubInHz, e.F0);
            Volatile.Write(ref pubNote, curNote < 0 ? 0 : curNote);
            Volatile.Write(ref pubCorrection, (outMidi - m) * amount * 100);
            Volatile.Write(ref pubVoiced, 1);
            Display(dt, m, m + shift);
        }

        void Display(double dt, double input, double corrected)
        {
            dispClock += dt;
            dispIn0 = input; dispOut0 = corrected;
            while (dispClock >= dispStep)
            {
                dispClock -= dispStep;
                int i = (int)(dispCount & (DisplaySize - 1));
                dispIn[i] = (float)dispIn0;
                dispOut[i] = (float)dispOut0;
                Volatile.Write(ref dispCount, dispCount + 1);
            }
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

        // ------------------------------------------------------------------ analysis marks

        float X(long t) => (t < 0 || t >= inCount || t < inCount - ring.Length + 4) ? 0f : ring[(int)(t & ringMask)];

        void AddMark(double t)
        {
            int i = (int)(mCount & MarkMask);
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
                    AddMark(best);
                    markRun = true;
                    mPtr = mScan;
                    continue;
                }
                double last = mT[(int)((mCount - 1) & MarkMask)];
                if (!ContourAt(last + 1, ref mPtr, out double f, out _))
                {
                    // The voice stopped (as far as the contour has got).
                    if (mPtr + 1 < cCount) { markRun = false; mScan = mPtr + 1; continue; }
                    return;
                }
                double p = sr / f;
                double pred = last + p;
                int hw = Math.Max(4, (int)(0.3 * p)), rg = Math.Max(2, (int)(0.2 * p));
                if (pred + rg + hw + 2 > inCount - 1) return;
                // Is the voice still voiced at the predicted mark?
                long probe = mPtr;
                if (!ContourAt(pred, ref probe, out _, out _) && probe + 1 < cCount) { markRun = false; mScan = probe + 1; continue; }
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
                float v = Src(last + j);
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

        // ------------------------------------------------------------------ grains

        float Src(double pos)
        {
            long p = (long)Math.Floor(pos);
            if (p < 1 || p + 2 >= inCount || p < inCount - ring.Length + 8) return 0f;
            double f = pos - p;
            float y0 = ring[(int)((p - 1) & ringMask)], y1 = ring[(int)(p & ringMask)], y2 = ring[(int)((p + 1) & ringMask)], y3 = ring[(int)((p + 2) & ringMask)];
            double c0 = y1, c1 = 0.5 * (y2 - y0), c2 = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3, c3 = 0.5 * (y3 - y0) + 1.5 * (y1 - y2);
            return (float)(((c3 * f + c2) * f + c1) * f + c0);
        }

        float Hann(double u)
        {
            double x = u * HannSize;
            int i = (int)x;
            if (i >= HannSize) return 0f;
            double f = x - i;
            return (float)(hann[i] + f * (hann[i + 1] - hann[i]));
        }

        /// <summary>The newest mark of run <paramref name="run"/> nearest in time to <paramref name="s"/>, or −1.</summary>
        double NearestMark(double s, int run)
        {
            double best = -1;
            double bd = double.MaxValue;
            long floor = Math.Max(0, mCount - MarkSize + 4);
            for (long i = mCount - 1; i >= floor; i--)
            {
                int ri = mRun[(int)(i & MarkMask)];
                if (ri > run) continue;
                if (ri < run) break;
                double t = mT[(int)(i & MarkMask)];
                double d = Math.Abs(t - s);
                if (d < bd) { bd = d; best = t; }
                else if (t < s) break;
            }
            return best;
        }

        void PlaceGrains(long tEnd)
        {
            for (int guard = 0; guard < 4096; guard++)
            {
                if (!synthActive)
                {
                    // The first mark of the next run.
                    long floor = Math.Max(0, mCount - MarkSize + 4);
                    long found = -1;
                    for (long i = mCount - 1; i >= floor; i--)
                    {
                        int ri = mRun[(int)(i & MarkMask)];
                        if (ri <= synthRun) break;
                        found = i;
                    }
                    if (found < 0) return;
                    synthRun = mRun[(int)(found & MarkMask)];
                    sNext = mT[(int)(found & MarkMask)];
                    sPtr = 0;
                    synthActive = true;
                }
                if (!ContourAt(sNext, ref sPtr, out double f, out double shiftSemis))
                {
                    // Unvoiced where the next grain would go: if the contour is that far, the run is over.
                    if (sPtr + 1 < cCount || sNext < inCount - latency - 2 * hgMax) { synthActive = false; continue; }
                    return;
                }
                double ratio = PortableMath.Exp2(shiftSemis / 12.0);
                double fOut = Math.Clamp(f * ratio, 20.0, sr * 0.25);
                ratio = fOut / f;
                double pin = sr / f, pout = sr / fOut;
                double phi = preserve ? fshift : ratio * fshift;
                phi = Math.Clamp(phi, 0.25, 4.0);
                double hs = Math.Min(pin, capSamples);
                double hg = Math.Min(Math.Max(hs / phi, 0.55 * pout), hgMax);
                if (sNext - hg >= tEnd) return;
                double a = NearestMark(sNext, synthRun);
                if (a < 0) return;
                long kLo = (long)Math.Ceiling(sNext - hg), kHi = (long)Math.Floor(sNext + hg);
                if (kLo < outPos) kLo = outPos;
                double invHg = 1.0 / hg;
                for (long k = kLo; k <= kHi; k++)
                {
                    double u = k - sNext;
                    float w = Hann(Math.Abs(u) * invHg);
                    if (w <= 0f) continue;
                    float v = Src(a + phi * u);
                    int oi = (int)(k & olaMask);
                    num[oi] += w * v;
                    den[oi] += w;
                }
                sNext += pout;
            }
        }

        // ------------------------------------------------------------------ output

        bool VoicedAt(long t)
        {
            if (cCount == 0) return false;
            long floor = Math.Max(0, cCount - ContourSize + 8);
            if (gPtr < floor) gPtr = floor;
            while (gPtr + 1 < cCount && cT[(int)((gPtr + 1) & ContourMask)] <= t) gPtr++;
            int s0 = (int)(gPtr & ContourMask);
            if (gPtr + 1 < cCount)
            {
                int s1 = (int)((gPtr + 1) & ContourMask);
                return (cT[s1] - t < t - cT[s0] ? cF[s1] : cF[s0]) > 0;
            }
            return cF[s0] > 0;
        }

        void Emit(float[] left, float[] right, int offset, int c, long tEnd)
        {
            float wet = (float)mix;
            for (int i = 0; i < c; i++)
            {
                long t = outPos + i;
                int oi = (int)(t & olaMask);
                float nu = num[oi], de = den[oi];
                num[oi] = 0f; den[oi] = 0f;
                bool voiced = VoicedAt(t) && de > 0.02f;
                bool dryHold = transients && t >= holdFrom && t < holdUntil;
                double target = voiced && !dryHold ? 1.0 : 0.0;
                if (gate < target) gate = Math.Min(target, gate + gateStep);
                else if (gate > target) gate = Math.Max(target, gate - gateStep);
                int di = (int)(t & ringMask);
                float dl = t >= 0 ? dryL[di] : 0f, dr = t >= 0 ? dryR[di] : 0f;
                float w = 0f;
                if (gate > 0)
                {
                    float shaped = (float)(gate * Math.Min(1.0, de * 6.0));
                    w = (float)(wet * shaped);
                    float v = nu / Math.Max(de, 1e-3f);
                    left[offset + i] = dl + w * (v - dl);
                    right[offset + i] = dr + w * (v - dr);
                }
                else
                {
                    left[offset + i] = dl;
                    right[offset + i] = dr;
                }
            }
            outPos += c;
        }
    }
}
