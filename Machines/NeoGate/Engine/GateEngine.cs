#nullable disable
// Neo Gate (docs/specs/mastering-tools.md §5.13): the successor of Pedal Gate, and a downward expander.
//  - The key (the input, or input 2) goes through KeyDetector, the detector Neo De-esser shares: a high-pass and a
//    low-pass (off at their ends), then a linked Peak follower (attack 1 ms, release 10 ms: Pedal Gate's) or a 10 ms RMS
//    average read on the stereo power sum (Pedal Gate's), in dB.
//  - Pedal Gate's state machine, kept: Closed → Attack when the level reaches the threshold; Open until it falls under
//    the close threshold (threshold − hysteresis), then Hold for the hold time, then Release; a level back over the
//    threshold during Release or Attack re-opens. The gain moves in linear ramps (1 / attack, 1 / release per sample),
//    so the same settings open and close the same way.
//  - Range is the floor of the closed gate. Ratio below infinity expands: under the close threshold the floor rises
//    to the gain (level − close threshold)·(ratio − 1) dB down (never under Range), so the gate eases shut as a
//    downward expander; at infinity the floor is Range alone, a hard gate.
//  - Lookahead delays the audio (the key is read undelayed): the latency is the lookahead, reported.
//  - Listen outputs the filtered key (delayed by the lookahead). Delta: the output minus the input.
// Allocation-free after construction; the portable maths, fixed order.

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using NeoBuzz.Analysis;
using NeoBuzz.Dsp;

namespace NeoBuzz.Gate
{
    internal sealed class GateEngine
    {
        public const int MaxBlock = 256;
        public const double MaxLookahead = 0.025;
        const double RmsSeconds = 0.010;

        enum State { Closed, Attack, Open, Hold, Release }

        readonly ParamValues values;
        float sampleRate = 48000;
        readonly KeyDetector key = new KeyDetector();
        DelayLineF delayL, delayR;
        State state = State.Closed;
        double gain;
        int holdLeft;
        SmoothRamp rangeRamp;
        readonly float[] dryL = new float[MaxBlock], dryR = new float[MaxBlock];
        SmoothRamp output;
        public readonly DeltaListen Delta;
        public readonly GainMatch Match;
        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        double threshold, closeThreshold, ratio, attackRate, releaseRate;
        int holdSamples, lookahead;
        bool listen;
        double pubGain, pubMatch = double.NaN;
        int pubOpen, pubLatency;

        public GateEngine(ParamValues values)
        {
            this.values = values;
            Delta = new DeltaListen((int)Math.Ceiling(MaxLookahead * 192000) + 4, 48000);
            Match = new GainMatch(48000);
        }

        /// <summary>Samples the audio is delayed by: the lookahead.</summary>
        public int Latency => lookahead;

        public double GainDb => Volatile.Read(ref pubGain);
        public bool IsOpen => Volatile.Read(ref pubOpen) != 0;
        public double GainMatchDb => Volatile.Read(ref pubMatch);

        public void SetSampleRate(float sr)
        {
            sampleRate = sr;
            int max = (int)Math.Ceiling(MaxLookahead * sr) + 2;
            delayL = new DelayLineF(max);
            delayR = new DelayLineF(max);
            rangeRamp.Setup(0.02, sr);
            output.Setup(0.02, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Delta.SetSampleRate(sr);
            Match.SetRate((int)Math.Round(sr));
            Update(true);
            Reset();
        }

        public void Reset()
        {
            key.Reset();
            delayL.Reset();
            delayR.Reset();
            state = State.Closed;
            gain = 0;
            holdLeft = 0;
            Delta.Reset();
            Match.Reset();
        }

        static void Target(ref SmoothRamp r, float v, bool force)
        {
            if (force) r.Reset(v);
            else r.SetTarget(v);
        }

        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            threshold = values.Value(P.Threshold);
            closeThreshold = threshold - values.Value(P.Hysteresis);
            ratio = values.Value(P.Ratio);
            double attack = values.Value(P.Attack), release = values.Value(P.Release);
            attackRate = 1 / (attack * sampleRate);
            releaseRate = 1 / (release * sampleRate);
            holdSamples = Math.Max(0, (int)(values.Value(P.Hold) * sampleRate));
            lookahead = Math.Clamp((int)Math.Round(values.Value(P.Lookahead) * 0.001 * sampleRate), 0, (int)Math.Ceiling(MaxLookahead * sampleRate));
            bool rms = values.Choice(P.Detection) == 1;
            if (rms) key.SetDetector(true, RmsSeconds, 0, sampleRate);
            else key.SetDetector(false, 0.001, 0.010, sampleRate);
            double low = values.Value(P.KeyLow), high = values.Value(P.KeyHigh);
            key.SetBand(low > 10.5 ? low : 0, high < 21000 ? high : 0, sampleRate);
            listen = values.On(P.Listen);
            double rangeDb = values.Value(P.Range);
            Target(ref rangeRamp, rangeDb <= -89.999 ? (float)DspMath.DbToGain(-90.0) : (float)DspMath.DbToGain(rangeDb), force);
            Delta.Latency = lookahead;
            pubLatency = lookahead;
            Target(ref output, (float)DspMath.DbToGain(values.Value(P.Output)), force);
            if (force) Delta.Set(values.On(P.Delta));
            else Delta.On = values.On(P.Delta);
            values.Clean();
        }

        /// <summary>Gates a stereo block in place; <paramref name="keyL"/> and <paramref name="keyR"/> are the external key (null: the input is the key).</summary>
        public void Process(float[] left, float[] right, float[] keyL, float[] keyR, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            Update(false);
            InMeter.Feed(left, right, n);
            Array.Copy(left, dryL, n);
            Array.Copy(right, dryR, n);
            bool external = keyL != null && values.Choice(P.Sidechain) == 1;
            Run(left, right, external ? keyL : left, external ? keyR : right, external, n);
            Delta.Process(dryL, dryR, left, right, n);
            for (int i = 0; i < n; i++)
            {
                float o = output.Next();
                left[i] *= o;
                right[i] *= o;
            }
            if (!Delta.Active) Match.Process(dryL, dryR, left, right, 0, n);
            OutMeter.Feed(left, right, n);
            Volatile.Write(ref pubGain, gain > 1e-6 ? 20 * PortableMath.Log10(gain) : -120);
            Volatile.Write(ref pubOpen, state == State.Open || state == State.Hold || state == State.Attack ? 1 : 0);
            Volatile.Write(ref pubMatch, Match.Db);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        void Run(float[] left, float[] right, float[] kl, float[] kr, bool external, int n)
        {
            int la = lookahead;
            bool hard = double.IsPositiveInfinity(ratio);
            double expand = hard ? 0 : ratio - 1;
            for (int i = 0; i < n; i++)
            {
                // The key is read before the input's output is written (the key may be the input's own buffer).
                float a = kl[i], b = kr[i];
                float l = left[i], r = right[i];
                float rangeLin = rangeRamp.Next();
                key.Filter(a, b, out float fa, out float fb);
                double level = key.Level(fa, fb);

                switch (state)
                {
                    case State.Closed:
                        if (level >= threshold) state = State.Attack;
                        break;
                    case State.Attack:
                        gain += attackRate;
                        if (gain >= 1)
                        {
                            gain = 1;
                            state = State.Open;
                            if (level < closeThreshold) { holdLeft = holdSamples; state = State.Hold; }
                        }
                        else if (level < closeThreshold) state = State.Release;
                        break;
                    case State.Open:
                        if (level < closeThreshold) { holdLeft = holdSamples; state = State.Hold; }
                        break;
                    case State.Hold:
                        if (level >= threshold) state = State.Open;
                        else if (--holdLeft <= 0) state = State.Release;
                        break;
                    case State.Release:
                    {
                        double floor = Floor(rangeLin, level, hard, expand);
                        gain -= releaseRate;
                        if (gain <= floor) { gain = floor; state = State.Closed; }
                        if (level >= threshold) state = State.Attack;
                        break;
                    }
                }
                if (state == State.Closed && !hard)
                {
                    // An expander: the closed gate follows the level-dependent floor, down by the release ramp, up by the attack's.
                    double floor = Floor(rangeLin, level, hard, expand);
                    if (gain > floor) gain = Math.Max(floor, gain - releaseRate);
                    else gain = Math.Min(floor, gain + attackRate);
                }
                double g = Math.Clamp(gain, rangeLin, 1.0);

                delayL.Write(l); delayR.Write(r);
                if (listen)
                {
                    left[i] = fa; right[i] = fb;
                }
                else
                {
                    left[i] = (float)(delayL.Read(la) * g);
                    right[i] = (float)(delayR.Read(la) * g);
                }
            }
        }

        /// <summary>The closed gate's floor at a level: Range, or higher while an expander's ratio would cut less.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        double Floor(double rangeLin, double level, bool hard, double expand)
        {
            if (hard || level >= closeThreshold) return rangeLin;
            double f = DspMath.DbToGain(-(closeThreshold - level) * expand);
            return f > rangeLin ? f : rangeLin;
        }

        public IReadOnlyDictionary<string, double> Readings()
        {
            var d = new Dictionary<string, double>
            {
                ["gain_db"] = Math.Round(GainDb, 2),
                ["open"] = IsOpen ? 1 : 0,
                ["latency_samples"] = Volatile.Read(ref pubLatency),
            };
            double m = GainMatchDb;
            if (double.IsFinite(m)) d["gain_match_db"] = Math.Round(m, 2);
            return d;
        }
    }
}
