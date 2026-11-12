#nullable disable
// Neo Learn (docs/specs/neo-learn.md §4): the host. It runs one DyeEngine (Machines/DyeEssence/Engine, unchanged: the engine
// already stands apart from the Dye Essence machine class, it takes a ParamValues) with the base patch its machine's macro
// map fixes, and keeps Dye's controls following the machine's 12. The engine's controls are written when a control changes and
// Dye ramps across the tick, as it does for any control. Output is the host's gain after synthesis (a block ramp, no limiter),
// with a clip light for anything above 0 dBFS. It also measures what the agent reads (machine_readings): rms_db, peak_db and
// active_voices.

using System;
using System.Threading;
using System.Collections.Generic;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;

namespace NeoBuzz.Learn
{
    internal sealed class LearnHost
    {
        /// <summary>The clip light stays lit this long after a sample above 0 dBFS.</summary>
        public const double ClipHoldSeconds = 1.0;
        /// <summary>The RMS reading's time constant.</summary>
        public const double RmsSeconds = 0.5;

        readonly MacroMap map;
        readonly ParamValues controls;
        readonly ParamValues dyeValues;
        readonly Sink sink;
        readonly double[] c = new double[MacroMap.Count];
        readonly LevelMeter meter = new LevelMeter();
        float gain = 1f, rate = 48000;
        double meanSquare;
        long clipLeft;
        int lastNotes;
        volatile float peakReading;
        double rmsReading;

        /// <param name="map">The machine's macro map.</param>
        /// <param name="controls">The machine's 12 controls (its Values).</param>
        public LearnHost(MacroMap map, ParamValues controls)
        {
            this.map = map;
            this.controls = controls;
            dyeValues = new ParamValues(NeoBuzz.DyeEssence.Params.Table);
            sink = new Sink(dyeValues);
            // The base patch and the controls' values are in before the engine is made, so its first block starts from them.
            map.Base(sink);
            Read();
            map.Map(c, sink);
            Engine = new DyeEngine(dyeValues);
            SetSampleRate(48000);
            gain = TargetGain();
            controls.Clean();
        }

        /// <summary>The engine that plays: notes go to it (BeginBlock), and tests reach its parts.</summary>
        internal DyeEngine Engine { get; }

        internal ParamValues DyeValues => dyeValues;

        public MacroMap Map => map;

        public void SetSampleRate(float sr)
        {
            rate = sr;
            Engine.SetSampleRate(sr);
            meter.SetSampleRate(sr);
        }

        void Read()
        {
            for (int i = 0; i < MacroMap.Count; i++) c[i] = controls.Value(i);
        }

        float TargetGain()
        {
            double db = c[MacroMap.OutputGain];
            return double.IsNegativeInfinity(db) ? 0f : (float)DspMath.DbToGain(db);
        }

        /// <summary>One block: Dye's controls follow the machine's when one changed, the engine plays, then Output.</summary>
        /// <summary>The picture's scope: the last ScopeSize samples of the output (mono), a ring the audio thread writes.</summary>
        public const int ScopeSize = 4096;
        readonly float[] scope = new float[ScopeSize];
        int scopeAt;

        /// <summary>Copies the last <paramref name="dst"/>.Length samples (at most ScopeSize), oldest first; the UI thread's, racing the audio thread harmlessly.</summary>
        public void CopyScope(float[] dst)
        {
            int n = Math.Min(dst.Length, ScopeSize);
            int at = Volatile.Read(ref scopeAt);
            for (int i = 0; i < n; i++) dst[i] = scope[(at - n + i) & (ScopeSize - 1)];
        }

        public bool Process(float[] left, float[] right, int n)
        {
            if (controls.AnyDirty)
            {
                Read();
                map.Map(c, sink);
                controls.Clean();
            }
            bool any = Engine.Process(left, right, n);
            float target = TargetGain();
            float g0 = gain, step = (target - g0) / n;
            double sum = 0;
            float peak = 0;
            for (int i = 0; i < n; i++)
            {
                g0 += step;
                float l = left[i] * g0, r = right[i] * g0;
                if (!float.IsFinite(l)) l = 0;
                if (!float.IsFinite(r)) r = 0;
                left[i] = l;
                right[i] = r;
                float a = MathF.Max(MathF.Abs(l), MathF.Abs(r));
                if (a > peak) peak = a;
                sum += 0.5 * ((double)l * l + (double)r * r);
                scope[scopeAt] = 0.5f * (l + r);
                scopeAt = (scopeAt + 1) & (ScopeSize - 1);
            }
            gain = target;
            meter.Feed(left, right, n);
            double k = 1 - PortableMath.Exp(-n / (RmsSeconds * rate));
            meanSquare += (sum / n - meanSquare) * k;
            if (meanSquare < 1e-30) meanSquare = 0;
            if (peak > 1f) clipLeft = (long)(ClipHoldSeconds * rate);
            else if (clipLeft > 0) clipLeft -= n;
            peakReading = meter.Peak;
            lastNotes = Engine.ActiveNotes;
            return any || peak > 1e-6f;
        }

        /// <summary>The clip light: anything above 0 dBFS in the last second.</summary>
        public bool Clipping => clipLeft > 0;

        /// <summary>The output's level meter (a gain, with the fall of Dye's meter).</summary>
        public float Peak => meter.Peak;

        /// <summary>Notes sounding now (a note is a voice here; unison voices belong to their note).</summary>
        public int ActiveVoices => lastNotes;

        /// <summary>What the agent reads (machine_readings): the output's RMS over about half a second and its peak, in dBFS, and the voices sounding.</summary>
        public IReadOnlyDictionary<string, double> Readings()
        {
            double rms = meanSquare > 0 ? 10 * PortableMath.Log10(meanSquare) : double.NegativeInfinity;
            double peak = peakReading > 1e-9f ? 20 * PortableMath.Log10(peakReading) : double.NegativeInfinity;
            return new Dictionary<string, double> { ["rms_db"] = rms, ["peak_db"] = peak, ["active_voices"] = lastNotes };
        }

        /// <summary>Writes a map's values into Dye's ParamValues, converting each to the control's raw step.</summary>
        sealed class Sink : IDyeSink
        {
            readonly ParamValues values;

            public Sink(ParamValues values)
            {
                this.values = values;
            }

            public void Set(int dyeParam, double value) => values.Set(dyeParam, values.Table[dyeParam].Raw(value));
        }
    }
}
