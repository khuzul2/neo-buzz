#nullable disable
// Neo Follow (docs/specs/neo-follow.md): the engine of an audio follower. It listens to the audio passing through (or to a sidechain) and
// makes one control signal, 0 … 1, that the bank (NeoBuzz.Dsp/ModTargets.cs) writes to up to 8 targets. The audio itself is not touched here.
//  - Envelope: the level of the signal (Peak: the highest sample in a short window, RMS: the mean square) through an attack and a release
//    (one-pole time constants), then dB, Floor and Ceiling onto 0 … 100 % and a Curve.
//  - Pitch: the shared YIN tracker (NeoBuzz.Dsp/PitchTracker.cs); a pitch whose confidence is under the threshold is not used, so the output
//    holds; the note is smoothed by the pitch glide and mapped from the Low to the High note.
//  - Onset: the shared onset detector (NeoBuzz.Dsp/OnsetDetector.cs); each hit starts a pulse (full, or scaled by the hit's level with
//    Dynamic), which holds for Hold ms and then falls with the Decay time constant.
// Then the shared shape: Smooth, Invert, Range, Depth and Bias, as Neo Random has them. Allocation-free after SetSampleRate; the portable maths
// only, and every sum in a fixed order, so the same audio gives the same bits on every platform whatever the host's block size allows.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.FollowMachine
{
    internal enum FollowMode { Envelope, Pitch, Onset }

    internal sealed class FollowEngine
    {
        public const int MaxBlock = 256;
        const double PeakWindowFraction = 0.05;     // of the release: the window of the Peak detector
        const double PeakWindowMax = 0.005;         // s
        const double RmsDecisionFraction = 0.25;    // of the shorter of attack and release: the smoothing of the copy that decides between them
        const double RmsWindowFraction = 0.05;      // of the shorter of attack and release: the time constant of the RMS detector's own averaging
        const double LevelFloorDb = -120.0;
        // The onset detector, tuned for hits against sustained sound: the threshold is ThrBase - ThrSlope x Sensitivity deviations, and the energy rises that
        // fire on their own are 15 dB (whole band) and 20 dB (under 150 Hz): the beating of two detuned oscillators in a pad swells by less than that in 10 ms.
        const double ThrBase = 10.0, ThrSlope = 8.0, ThrMinRatio = 2.2, RiseDb = 15.0, LowRiseDb = 20.0;

        readonly ParamValues v;
        double sr = 44100;

        // ---- what it says
        /// <summary>The control signal, 0 … 1, after Smooth, Invert, Range, Depth and Bias: what the targets get.</summary>
        public float Output { get; private set; }
        /// <summary>The mode's own value, 0 … 1, before the shape.</summary>
        public float Raw { get; private set; }
        /// <summary>Envelope: the detected level, dB (full scale 0).</summary>
        public double LevelDb { get; private set; } = LevelFloorDb;
        /// <summary>Envelope: the level the ballistics act on (the peak, or the mean square), linear.</summary>
        public double Level => lvl;
        /// <summary>Pitch: the smoothed note, MIDI (69 = A4 = 440 Hz).</summary>
        public double Note { get; private set; }
        /// <summary>Onset: the pulse now, 0 … 1.</summary>
        public double Pulse => pulse;
        /// <summary>Onsets heard since the engine was made (for tests and the editor).</summary>
        public long OnsetCount { get; private set; }
        /// <summary>The stream position (samples since the start) of the latest onset the detector found, refined to the sample.</summary>
        public long LastOnsetSample { get; private set; }
        /// <summary>The samples between that onset and the moment the detector reported it (for tests).</summary>
        public long LastOnsetDelay { get; private set; }
        /// <summary>The Write glide, in seconds.</summary>
        public double Glide => v.Value(P.Glide);

        // ---- the analysers
        PitchTracker[] trackers;
        PitchTracker tracker;
        OnsetDetector onsets;
        PitchEstimate[] frames;
        OnsetEvent[] events;
        readonly float[] mono = new float[MaxBlock];

        // ---- state
        int lastMode = -1, lastDetector = -1, lastPitchRange = -1;
        // Envelope
        double lvl;
        double coefA, coefR, coefS, coefD;
        int coefKeyA = -1, coefKeyR = -1;
        double lvlS, lvlD;
        long[] dIdx;
        float[] dVal;
        int dMask;
        long dHead, dTail, dCount;
        // Pitch
        double pitchTarget, pitchSm;
        bool pitchPrimed;
        // Onset
        double pulse;
        int holdLeft;
        // Shape
        double sm;
        bool smPrimed;

        public FollowEngine(ParamValues values)
        {
            v = values;
        }

        public void SetSampleRate(double rate)
        {
            if (rate < 8000) rate = 8000;
            sr = rate;
            int cap = 1;
            int maxW = (int)Math.Ceiling(PeakWindowMax * rate) + 2;
            while (cap < maxW + 2) cap <<= 1;
            dIdx = new long[cap];
            dVal = new float[cap];
            dMask = cap - 1;
            trackers = new[]
            {
                new PitchTracker(rate, PitchRange.Voice), new PitchTracker(rate, PitchRange.Guitar),
                new PitchTracker(rate, PitchRange.Bass), new PitchTracker(rate, PitchRange.Full),
            };
            tracker = trackers[3];
            frames = new PitchEstimate[tracker.MaxFrames(MaxBlock) + 1];
            for (int i = 0; i < trackers.Length; i++) if (trackers[i].MaxFrames(MaxBlock) + 1 > frames.Length) frames = new PitchEstimate[trackers[i].MaxFrames(MaxBlock) + 1];
            onsets = new OnsetDetector(rate);
            events = new OnsetEvent[onsets.MaxEvents(MaxBlock) + 1];
            coefKeyA = coefKeyR = -1;
            Reset();
        }

        /// <summary>Back to the start: no level, no pitch, no pulse.</summary>
        public void Reset()
        {
            lvl = 0; lvlS = 0; lvlD = 0; dHead = dTail = dCount = 0;
            pitchTarget = pitchSm = 0; pitchPrimed = false;
            pulse = 0; holdLeft = 0;
            sm = 0; smPrimed = false;
            Raw = 0; Output = 0; LevelDb = LevelFloorDb; Note = 0;
            lastMode = lastDetector = lastPitchRange = -1;
            if (tracker != null) { foreach (var t in trackers) t.Reset(); onsets.Reset(); }
        }

        /// <summary>
        /// The time between a change in the audio and the control signal showing it, in samples, on average (the Hop and the host's block come on top, up to one
        /// each). Envelope has none of its own: the attack and the release are the control's. Pitch: the tracker compares the newest window with the same
        /// window one period back, so the estimate describes the audio (window + period) / 2 samples ago: <paramref name="hz"/> is the pitch (0: the range's lowest,
        /// where it is longest). Onset: the detector confirms an onset one hop after the frame that holds it, and refines where in the frame it was; about half a frame after the hit (the pulse then starts at the end of the block the report falls in).
        /// </summary>
        public int ControlDelay(FollowMode mode, double hz = 0)
        {
            switch (mode)
            {
                case FollowMode.Pitch:
                {
                    var t = Trackers(v.Choice(P.PitchRange));
                    if (hz <= 0) hz = t.MinHz;
                    return (int)Math.Round((t.Window + sr / hz) / 2.0);
                }
                case FollowMode.Onset: return onsets != null ? onsets.FrameSize / 2 : 0;
                default: return 0;
            }
        }

        PitchTracker Trackers(int range) => trackers[Math.Clamp(range, 0, 3)];

        // ---------------------------------------------------------------- one block

        /// <summary>
        /// Listens to <paramref name="n"/> samples (at most <see cref="MaxBlock"/>): the input (<paramref name="l"/>, <paramref name="r"/>) or the sidechain
        /// (<paramref name="kl"/>, <paramref name="kr"/>; null: nothing wired, so silence). Updates <see cref="Output"/>.
        /// </summary>
        public void Process(float[] l, float[] r, float[] kl, float[] kr, int n)
        {
            if (n <= 0) return;
            if (n > MaxBlock) n = MaxBlock;
            bool side = v.Choice(P.Listen) == 1;
            float[] a = side ? kl : l, b = side ? kr : r;
            if (b == null) b = a;
            int mode = Math.Clamp(v.Choice(P.Mode), 0, 2);
            if (mode != lastMode)
            {
                // The analysers of the mode taken up start clean (a pitch or a pulse of the mode before is not this one's).
                lastMode = mode;
                lvl = 0; lvlS = 0; lvlD = 0; dHead = dTail = 0;
                pitchPrimed = false; pitchTarget = pitchSm = 0;
                pulse = 0; holdLeft = 0;
                if (mode == (int)FollowMode.Pitch) Trackers(v.Choice(P.PitchRange)).Reset();
                if (mode == (int)FollowMode.Onset) onsets.Reset();
                lastPitchRange = -1;
            }
            double q;
            switch ((FollowMode)mode)
            {
                case FollowMode.Pitch: q = PitchBlock(a, b, n); break;
                case FollowMode.Onset: q = OnsetBlock(a, b, n); break;
                default: q = EnvelopeBlock(a, b, n); break;
            }
            Raw = (float)q;
            Shape(q, n / sr);
        }

        // ---------------------------------------------------------------- Envelope

        double EnvelopeBlock(float[] a, float[] b, int n)
        {
            int det = v.Choice(P.Detector);
            if (det != lastDetector) { lastDetector = det; lvl = 0; lvlS = 0; lvlD = 0; dHead = dTail = 0; }
            int rawA = v[P.Attack] + 4096 * det, rawR = v[P.Release];
            double tauR = v.Value(P.Release) * 0.001;
            if (rawA != coefKeyA || rawR != coefKeyR)
            {
                coefKeyA = rawA; coefKeyR = rawR;
                double tauA = v.Value(P.Attack) * 0.001;
                double ts = RmsWindowFraction * Math.Min(tauA, tauR);
                coefS = Coef(ts);
                coefD = Coef(RmsDecisionFraction * Math.Min(tauA, tauR));
                // The RMS detector's averaging and the ballistics are two poles in a row: the pair covers 63 % of a step in t1 (1 - ln(1 - ts / t1)),
                // so the ballistics' own time constant is made the one that puts the pair at the time the person set. (The Peak detector's two
                // time constants are the same, and its window is a delay: no correction.)
                coefA = Coef(det == 0 ? tauA : Corrected(tauA, ts));
                coefR = Coef(det == 0 ? tauR : Corrected(tauR, ts));
            }
            double ca = coefA, cr = coefR, level = lvl;
            if (det == 0)
            {
                int w = (int)Math.Round(PeakWindowFraction * tauR * sr);
                int wMax = (int)Math.Ceiling(PeakWindowMax * sr);
                w = w < 1 ? 1 : w > wMax ? wMax : w;
                for (int i = 0; i < n; i++)
                {
                    float m = 0f;
                    if (a != null)
                    {
                        float x = Math.Abs(a[i]), y = Math.Abs(b[i]);
                        m = x > y ? x : y;
                    }
                    while (dTail > dHead && dVal[(int)((dTail - 1) & dMask)] <= m) dTail--;
                    dIdx[(int)(dTail & dMask)] = dCount; dVal[(int)(dTail & dMask)] = m; dTail++;
                    while (dIdx[(int)(dHead & dMask)] <= dCount - w) dHead++;
                    double pk = dVal[(int)(dHead & dMask)];
                    dCount++;
                    level += (pk - level) * (pk > level ? ca : cr);
                    if (level < 1e-15) level = 0;
                }
            }
            else
            {
                // The mean square is averaged a little first (a fraction of the shorter time) so that the ripple of a tone's x² does not ride
                // on the attack and release, which would read a tone high; then the attack and the release act on it.
                // Whether the attack or the release acts is decided on a copy smoothed more (a tone's ripple would make it switch all the time and
                // read the tone high), while the update itself follows the lightly averaged one.
                double cs = coefS, cd = coefD, avg = lvlS, dec = lvlD;
                for (int i = 0; i < n; i++)
                {
                    double p = 0;
                    if (a != null) { double x = a[i], y = b[i]; p = 0.5 * (x * x + y * y); }
                    avg += (p - avg) * cs;
                    if (avg < 1e-30) avg = 0;
                    dec += (avg - dec) * cd;
                    if (dec < 1e-30) dec = 0;
                    level += (avg - level) * (dec > level ? ca : cr);
                    if (level < 1e-30) level = 0;
                }
                lvlS = avg; lvlD = dec;
            }
            lvl = level;
            double amp = det == 0 ? level : Math.Sqrt(level);
            double db = amp > 1e-6 ? DspMath.DbPerNeper * PortableMath.Log(amp) : LevelFloorDb;
            LevelDb = db;
            double floor = v.Value(P.Floor), ceil = v.Value(P.Ceiling);
            if (ceil < floor + 1.0) ceil = floor + 1.0;
            double t = DspMath.Clamp((db - floor) / (ceil - floor), 0.0, 1.0);
            return ModBank.Bend(t, (int)Math.Round(v.Value(P.Curve) * 100.0));
        }

        /// <summary>The time constant that, in a row after a pole of <paramref name="ts"/>, reaches 63 % of a step in <paramref name="tau"/>.</summary>
        static double Corrected(double tau, double ts)
        {
            if (ts <= 0) return tau;
            double t1 = tau;
            for (int i = 0; i < 3; i++)
            {
                double r = Math.Min(ts / t1, 0.5);
                t1 = tau / (1.0 - PortableMath.Log(1.0 - r));
            }
            return t1;
        }

        double Coef(double tau) => tau <= 0 ? 1.0 : 1.0 - PortableMath.Exp(-1.0 / (tau * sr));

        // ---------------------------------------------------------------- Pitch

        double PitchBlock(float[] a, float[] b, int n)
        {
            int range = Math.Clamp(v.Choice(P.PitchRange), 0, 3);
            if (range != lastPitchRange)
            {
                lastPitchRange = range;
                tracker = trackers[range];
                tracker.Reset();
                pitchPrimed = false;
            }
            for (int i = 0; i < n; i++) mono[i] = a == null ? 0f : 0.5f * (a[i] + b[i]);
            int count = tracker.Process(mono, 0, n, frames, 0);
            double threshold = v.Value(P.Confidence);
            for (int k = 0; k < count; k++)
            {
                ref PitchEstimate e = ref frames[k];
                if (e.RawF0 > 0 && e.Confidence >= threshold && e.Rms >= tracker.Gate)
                {
                    pitchTarget = DspMath.HzToNote(e.RawF0);
                    if (!pitchPrimed) { pitchPrimed = true; pitchSm = pitchTarget; }
                }
            }
            if (!pitchPrimed) return Raw;
            double tau = v.Value(P.PitchGlide) * 0.001;
            double dt = n / sr;
            if (tau <= 0) pitchSm = pitchTarget;
            else
            {
                pitchSm += (pitchTarget - pitchSm) * (1.0 - PortableMath.Exp(-dt / tau));
                if (Math.Abs(pitchTarget - pitchSm) < 1e-9) pitchSm = pitchTarget;
            }
            Note = pitchSm;
            double lo = v.Value(P.Low), hi = v.Value(P.High);
            if (hi < lo + 1.0) hi = lo + 1.0;
            return DspMath.Clamp((pitchSm - lo) / (hi - lo), 0.0, 1.0);
        }

        // ---------------------------------------------------------------- Onset

        double OnsetBlock(float[] a, float[] b, int n)
        {
            double sens = v.Value(P.Sensitivity);
            onsets.Threshold = (float)(ThrBase - ThrSlope * sens);
            onsets.RobustSpread = true;
            onsets.RobustMask = 8;
            onsets.MinRatio = (float)ThrMinRatio;
            onsets.MinIntervalSeconds = 0.03;
            onsets.RiseDb = (float)RiseDb;
            onsets.LowRiseDb = (float)LowRiseDb;
            for (int i = 0; i < n; i++) mono[i] = a == null ? 0f : 0.5f * (a[i] + b[i]);
            int count = onsets.Process(mono, 0, n, events, 0);

            // The pulse falls over this block (after its hold), then the hits found in it start theirs: a pulse takes the block's end as its start.
            if (pulse > 0)
            {
                int left = n;
                if (holdLeft > 0)
                {
                    int used = holdLeft < left ? holdLeft : left;
                    holdLeft -= used;
                    left -= used;
                }
                if (left > 0)
                {
                    double tau = v.Value(P.Decay) * 0.001;
                    pulse *= PortableMath.Exp(-left / (tau * sr));
                    if (pulse < 1e-6) pulse = 0;
                }
            }
            if (count > 0)
            {
                bool dynamic = v.On(P.Dynamic);
                int hold = (int)Math.Round(v.Value(P.Hold) * 0.001 * sr);
                for (int k = 0; k < count; k++)
                {
                    OnsetCount++;
                    LastOnsetSample = events[k].Sample;
                    LastOnsetDelay = events[k].ReportedAt - events[k].Sample;
                    double h = dynamic ? DspMath.Clamp(1.0 + (events[k].LevelDb + 6.0) / 30.0, 0.1, 1.0) : 1.0;
                    pulse = h;
                    holdLeft = hold;
                }
            }
            return pulse;
        }

        // ---------------------------------------------------------------- the shape

        void Shape(double q, double dt)
        {
            double tau = v.Value(P.Smooth) * 0.001;
            if (!smPrimed) { smPrimed = true; sm = q; }
            if (tau <= 0) sm = q;
            else
            {
                sm += (q - sm) * (1.0 - PortableMath.Exp(-dt / tau));
                if (Math.Abs(q - sm) < 1e-9) sm = q;
            }
            double y = v.On(P.Invert) ? 1.0 - sm : sm;
            double depth = v.Value(P.Depth), bias = v.Value(P.Bias);
            bool bipolar = v.Choice(P.Range) != 0;
            double o = bipolar ? 0.5 + depth * (y - 0.5) + 0.5 * bias : depth * y + bias;
            Output = (float)DspMath.Clamp(o, 0.0, 1.0);
        }
    }
}
