#nullable disable
// Tutorial Tremolo (docs/sdk/tutorial.md): a low-frequency oscillator moves the level. Pure DSP: no Buzz, no editor.
// It is the fixture of the SDK's tutorial, compiled and tested with the test project (never shipped as a machine).

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.TutorialTremolo
{
    internal sealed class TremoloEngine
    {
        // snippet EngineFields
        public const int MaxBlock = 256;
        const double SmoothSeconds = 0.02;

        readonly ParamValues values;     // what the engine reads: the machine's values with Amount applied
        readonly FxStage fx;
        SmoothRamp depth, offset, triangle;
        double sampleRate = 48000;
        double phase;                    // 0 … 1, the left channel's position in the cycle
        double increment;                // phase per sample
        // end snippet EngineFields

        // snippet AmountRules
        /// <summary>What Amount scales: the distance of Depth from 0 %.</summary>
        public static readonly AmountRule[] AmountRules =
        {
            AmountRule.Lin(P.Depth, 0),
        };
        // end snippet AmountRules

        // snippet EngineCtor
        public TremoloEngine(ParamValues raw)
        {
            fx = new FxStage(raw, P.Amount, P.Mix, AmountRules, maxLatency: Latency, maxBlock: MaxBlock);
            values = fx.Effective;
            SetSampleRate(48000);
        }
        // end snippet EngineCtor

        public readonly LevelMeter InMeter = new LevelMeter();
        public readonly LevelMeter OutMeter = new LevelMeter();

        /// <summary>No latency: the gain is applied sample by sample.</summary>
        public int Latency => 0;

        // snippet EngineRate
        public void SetSampleRate(float sr)
        {
            fx.SetSampleRate(sr);
            sampleRate = sr;
            depth.Setup(SmoothSeconds, sr);
            offset.Setup(SmoothSeconds, sr);
            triangle.Setup(SmoothSeconds, sr);
            InMeter.SetSampleRate(sr);
            OutMeter.SetSampleRate(sr);
            Update(true);
        }

        public void Reset()
        {
            fx.Reset();
            phase = 0;
        }
        // end snippet EngineRate

        // snippet EngineUpdate
        /// <summary>Reads the parameters that changed. Runs once per block, never per sample.</summary>
        void Update(bool force)
        {
            if (!force && !values.AnyDirty) return;
            increment = values.Value(P.Rate) / sampleRate;
            float d = values.F(P.Depth);
            float o = (float)(values.Value(P.Phase) / 360.0);
            float t = values.Choice(P.Shape) == 1 ? 1f : 0f;
            if (force)
            {
                depth.Reset(d);
                offset.Reset(o);
                triangle.Reset(t);
            }
            else
            {
                depth.SetTarget(d);
                offset.SetTarget(o);
                triangle.SetTarget(t);
            }
            values.Clean();
        }
        // end snippet EngineUpdate

        // snippet EngineProcess
        /// <summary>Processes a stereo block in place.</summary>
        public void Process(float[] left, float[] right, int n)
        {
            if (n > MaxBlock) throw new ArgumentOutOfRangeException(nameof(n));
            fx.Begin(left, right, n);     // the effective values for this block, and the dry input kept for Mix
            Update(false);
            InMeter.Feed(left, right, n);
            for (int i = 0; i < n; i++)
            {
                float d = depth.Next();
                float shape = triangle.Next();
                float o = offset.Next();
                left[i] *= Gain(phase, d, shape);
                right[i] *= Gain(phase + o, d, shape);
                phase += increment;
                if (phase >= 1) phase -= 1;
            }
            OutMeter.Feed(left, right, n);
            fx.End(left, right, n, Latency);   // Mix and Amount's bypass: the dry input blended in
        }

        /// <summary>The gain at a position in the cycle: 1 at the top, 1 − depth at the bottom. Sine and triangle crossfade by <paramref name="shape"/> (0 sine, 1 triangle).</summary>
        static float Gain(double position, float depth, float shape)
        {
            double p = position - Math.Floor(position);
            float sine = (float)(0.5 - 0.5 * PortableMath.Cos(2 * Math.PI * p));
            float tri = (float)(p < 0.5 ? 2 * p : 2 - 2 * p);
            float dip = sine + shape * (tri - sine);
            return 1f - depth * dip;
        }
        // end snippet EngineProcess

        /// <summary>The gain curve over <paramref name="cycles"/> cycles for the values <paramref name="v"/> (the editor draws it; the engine runs the same Gain).</summary>
        public static double CurveAt(ParamValues v, double cycle, bool right)
        {
            float o = (float)(v.Value(P.Phase) / 360.0);
            return Gain(cycle + (right ? o : 0), v.F(P.Depth), v.Choice(P.Shape) == 1 ? 1f : 0f);
        }
    }
}
