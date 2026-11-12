#nullable disable
// Dye Essence's effects rack (docs/specs/dye-essence.md section 4.10): the types a slot can take. Each is the engine of a
// Neo machine, linked as source and run with its own ParamValues; a slot's ten generic parameters (0 ... 1000) are
// converted into ten of the engine's parameters, 50 % being the engine's own default (or the default set here), so a
// type sounds as its machine does. The rest of the engine's parameters keep their defaults (what the type fixes is in
// its host's constructor). Nothing here is on the audio path except the hosts' Process.

using System;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>What a slot can host. Values are saved in songs and presets and never move; new types go at the end.</summary>
    internal enum FxType { Off = 0, Eq = 1, Filter = 2, Comp = 3, Clipper = 4, Delay = 5, Verb = 6, Dimension = 7, Bitcrusher = 8, Shifter = 9, PitchDelay = 10, Convolution = 11, Vocoder = 12 }

    /// <summary>One of a type's ten generic parameters: the engine parameter it drives, the engine value 50 % stands for (-1: the engine's default) and the label the type gives it.</summary>
    internal readonly struct FxParam
    {
        public readonly int Index, Def;
        public readonly string Label;
        public FxParam(int index, int def, string label) { Index = index; Def = def; Label = label; }
    }

    /// <summary>A rack type: its name, its engine's parameter table and its ten parameters.</summary>
    internal sealed class FxTypeInfo
    {
        public readonly FxType Type;
        public readonly string Name;
        public readonly ParamDef[] Table;
        public readonly FxParam[] Params;

        public FxTypeInfo(FxType type, string name, ParamDef[] table, FxParam[] p)
        {
            Type = type;
            Name = name;
            Table = table;
            Params = p;
        }

        /// <summary>The engine's raw value for generic value <paramref name="x"/> (0 ... 1000) of parameter <paramref name="k"/>.</summary>
        public int EngineRaw(int k, int x)
        {
            var p = Params[k];
            var d = Table[p.Index];
            return FxTypes.Map(d, p.Def < 0 ? d.Default : p.Def, x);
        }

        /// <summary>The value text of generic value <paramref name="x"/> of parameter <paramref name="k"/>, in the engine's own units.</summary>
        public string Describe(int k, int x) => Table[Params[k].Index].Describe(EngineRaw(k, x));
    }

    internal static class FxTypes
    {
        public const int Count = 13;
        public const int PerType = 10;

        static readonly FxTypeInfo[] infos = Build();

        public static FxTypeInfo Info(FxType t) => infos[(int)t];

        public static string Name(int type) => type <= 0 || type >= infos.Length ? "Off" : infos[type].Name;

        /// <summary>
        /// Generic value 0 ... 1000 to an engine parameter's raw value: a piecewise-linear map through the engine's
        /// minimum, the default (at 500) and the maximum; a choice (enum or switch) takes the same map and rounds, so
        /// every choice can be reached and the default is at the middle.
        /// </summary>
        public static int Map(ParamDef d, int def, int x)
        {
            double t = Math.Clamp(x, 0, 1000) / 1000.0;
            double u = t < 0.5 ? d.Min + (def - d.Min) * (t / 0.5) : def + (d.Max - def) * ((t - 0.5) / 0.5);
            return Math.Clamp((int)Math.Round(u), d.Min, d.Max);
        }

        /// <summary>The latency of a type at a sample rate, in samples: the same whatever its parameters (the clipper's, the bitcrusher's 2 ms wobble delay).</summary>
        public static int Latency(FxType t, double sampleRate) => t switch
        {
            FxType.Clipper => NeoBuzz.Clipper.ClipperEngine.LatencyAt(sampleRate),
            FxType.Bitcrusher => Bitcrusher.LatencySamples(sampleRate),
            _ => 0,
        };

        static FxParam F(int index, string label, int def = -1) => new FxParam(index, def, label);

        static FxTypeInfo[] Build()
        {
            int Fq(int b) => NeoBuzz.Eq.EqDesign.Freq(b);
            int Gn(int b) => NeoBuzz.Eq.EqDesign.Gain(b);
            int Qq(int b) => NeoBuzz.Eq.EqDesign.Q(b);
            var list = new FxTypeInfo[Count];
            list[(int)FxType.Off] = new FxTypeInfo(FxType.Off, "Off", Array.Empty<ParamDef>(), Array.Empty<FxParam>());
            list[(int)FxType.Eq] = new FxTypeInfo(FxType.Eq, "EQ", NeoBuzz.Eq.Params.Table, new[]
            {
                F(Fq(0), "Low freq", 2330), F(Gn(0), "Low gain", 240),
                F(Fq(1), "Mid 1 freq", 4660), F(Gn(1), "Mid 1 gain", 240), F(Qq(1), "Mid 1 Q", 807),
                F(Fq(2), "Mid 2 freq", 6990), F(Gn(2), "Mid 2 gain", 240), F(Qq(2), "Mid 2 Q", 807),
                F(Fq(3), "High freq", 8670), F(Gn(3), "High gain", 240),
            });
            list[(int)FxType.Filter] = new FxTypeInfo(FxType.Filter, "Filter", NeoBuzz.Filter.Params.Table, new[]
            {
                F(NeoBuzz.Filter.P.Type, "Type"), F(NeoBuzz.Filter.P.Slope, "Slope"), F(NeoBuzz.Filter.P.Cutoff, "Cutoff"),
                F(NeoBuzz.Filter.P.Resonance, "Resonance"), F(NeoBuzz.Filter.P.Drive, "Drive"), F(NeoBuzz.Filter.P.EnvAmount, "Env amount"),
                F(NeoBuzz.Filter.P.LfoRate, "LFO rate"), F(NeoBuzz.Filter.P.LfoSync, "LFO sync"), F(NeoBuzz.Filter.P.LfoShape, "LFO shape"),
                F(NeoBuzz.Filter.P.LfoDepth, "LFO depth"),
            });
            list[(int)FxType.Comp] = new FxTypeInfo(FxType.Comp, "Comp", NeoBuzz.Comp.Params.Table, new[]
            {
                F(NeoBuzz.Comp.P.Mode, "Mode"), F(NeoBuzz.Comp.P.Threshold, "Threshold"), F(NeoBuzz.Comp.P.Ratio, "Ratio"),
                F(NeoBuzz.Comp.P.Knee, "Knee"), F(NeoBuzz.Comp.P.Attack, "Attack"), F(NeoBuzz.Comp.P.Release, "Release"),
                F(NeoBuzz.Comp.P.ScLowCut, "SC low cut"), F(NeoBuzz.Comp.P.Makeup, "Makeup"), F(NeoBuzz.Comp.P.Character, "Character"),
                F(NeoBuzz.Comp.P.Link, "Link"),
            });
            list[(int)FxType.Clipper] = new FxTypeInfo(FxType.Clipper, "Clipper", NeoBuzz.Clipper.Params.Table, new[]
            {
                F(NeoBuzz.Clipper.P.Drive, "Drive"), F(NeoBuzz.Clipper.P.Ceiling, "Ceiling"), F(NeoBuzz.Clipper.P.Shape, "Shape"),
                F(NeoBuzz.Clipper.P.Knee, "Knee"), F(NeoBuzz.Clipper.P.Oversample, "Oversampling"), F(NeoBuzz.Clipper.P.Bands, "Bands"),
                F(NeoBuzz.Clipper.P.Xo1, "Crossover 1"), F(NeoBuzz.Clipper.P.Xo2, "Crossover 2"), F(NeoBuzz.Clipper.P.Stereo, "Stereo"),
                F(NeoBuzz.Clipper.P.Output, "Output"),
            });
            list[(int)FxType.Delay] = new FxTypeInfo(FxType.Delay, "Delay", NeoBuzz.Delay.Params.Table, new[]
            {
                F(NeoBuzz.Delay.P.Mode, "Mode"), F(NeoBuzz.Delay.P.Sync, "Sync"), F(NeoBuzz.Delay.P.Feel, "Feel"),
                F(NeoBuzz.Delay.P.Time, "Time"), F(NeoBuzz.Delay.P.Offset, "Offset"), F(NeoBuzz.Delay.P.Feedback, "Feedback"),
                F(NeoBuzz.Delay.P.LowCut, "Low cut"), F(NeoBuzz.Delay.P.HighCut, "High cut"), F(NeoBuzz.Delay.P.ModDepth, "Mod depth"),
                F(NeoBuzz.Delay.P.Width, "Width"),
            });
            list[(int)FxType.Verb] = new FxTypeInfo(FxType.Verb, "Verb", NeoBuzz.Verb.Params.Table, new[]
            {
                F(NeoBuzz.Verb.P.Mode, "Mode"), F(NeoBuzz.Verb.P.PreDelay, "Pre-delay"), F(NeoBuzz.Verb.P.Size, "Size"),
                F(NeoBuzz.Verb.P.Decay, "Decay"), F(NeoBuzz.Verb.P.LowDecay, "Low decay"), F(NeoBuzz.Verb.P.Damping, "Damping"),
                F(NeoBuzz.Verb.P.Shimmer, "Shimmer"), F(NeoBuzz.Verb.P.Width, "Width"), F(NeoBuzz.Verb.P.LowCut, "Low cut"),
                F(NeoBuzz.Verb.P.HighCut, "High cut"),
            });
            list[(int)FxType.Dimension] = new FxTypeInfo(FxType.Dimension, "Dimension", FxNew.Dim.Table, new[]
            {
                F(FxNew.Dim.Voices, "Voices"), F(FxNew.Dim.Rate, "Rate"), F(FxNew.Dim.Depth, "Depth"), F(FxNew.Dim.Delay, "Delay"),
                F(FxNew.Dim.Width, "Width"), F(FxNew.Dim.Shape, "Shape"), F(FxNew.Dim.Tone, "Tone"), F(FxNew.Dim.LowCut, "Low cut"),
                F(FxNew.Dim.Drift, "Drift"), F(FxNew.Dim.Feedback, "Feedback"),
            });
            list[(int)FxType.Bitcrusher] = new FxTypeInfo(FxType.Bitcrusher, "Bitcrusher", FxNew.Crush.Table, new[]
            {
                F(FxNew.Crush.Bits, "Bits"), F(FxNew.Crush.Rate, "Rate"), F(FxNew.Crush.AntiAlias, "Anti-alias"), F(FxNew.Crush.Drive, "Drive"),
                F(FxNew.Crush.Quantizer, "Quantizer"), F(FxNew.Crush.Bias, "Bias"), F(FxNew.Crush.Dither, "Dither"), F(FxNew.Crush.Tone, "Tone"),
                F(FxNew.Crush.Wobble, "Wobble"), F(FxNew.Crush.Output, "Output"),
            });
            list[(int)FxType.Shifter] = new FxTypeInfo(FxType.Shifter, "Freq shift", FxNew.Shift.Table, new[]
            {
                F(FxNew.Shift.Amount, "Shift"), F(FxNew.Shift.Range, "Range"), F(FxNew.Shift.Mode, "Mode"), F(FxNew.Shift.Feedback, "Feedback"),
                F(FxNew.Shift.FbTone, "Feedback tone"), F(FxNew.Shift.Stereo, "Stereo"), F(FxNew.Shift.LfoRate, "LFO rate"),
                F(FxNew.Shift.LfoDepth, "LFO depth"), F(FxNew.Shift.Glide, "Glide"), F(FxNew.Shift.Output, "Output"),
            });
            list[(int)FxType.PitchDelay] = new FxTypeInfo(FxType.PitchDelay, "Pitch delay", FxNew.PitchDelay.Table, new[]
            {
                F(FxNew.PitchDelay.Time, "Time"), F(FxNew.PitchDelay.Pitch, "Pitch"), F(FxNew.PitchDelay.Fine, "Fine"), F(FxNew.PitchDelay.Feedback, "Feedback"),
                F(FxNew.PitchDelay.Grain, "Grain"), F(FxNew.PitchDelay.Mode, "Mode"), F(FxNew.PitchDelay.Tone, "Tone"),
                F(FxNew.PitchDelay.LowCut, "Low cut"), F(FxNew.PitchDelay.Spread, "Spread"), F(FxNew.PitchDelay.Cross, "Cross-feed"),
            });
            list[(int)FxType.Convolution] = new FxTypeInfo(FxType.Convolution, "Convolution", FxNew.Conv.Table, new[]
            {
                F(FxNew.Conv.Ir, "IR"), F(FxNew.Conv.Size, "Size"), F(FxNew.Conv.PreDelay, "Pre-delay"), F(FxNew.Conv.Decay, "Decay"),
                F(FxNew.Conv.Damping, "Damping"), F(FxNew.Conv.LowCut, "Low cut"), F(FxNew.Conv.HighCut, "High cut"), F(FxNew.Conv.Width, "Width"),
                F(FxNew.Conv.Reverse, "Reverse"), F(FxNew.Conv.Output, "Output"),
            });
            list[(int)FxType.Vocoder] = new FxTypeInfo(FxType.Vocoder, "Vocoder", FxNew.Voc.Table, new[]
            {
                F(FxNew.Voc.Source, "Modulator"), F(FxNew.Voc.Bands, "Bands"), F(FxNew.Voc.Formant, "Formant"), F(FxNew.Voc.Low, "Low"),
                F(FxNew.Voc.High, "High"), F(FxNew.Voc.Attack, "Attack"), F(FxNew.Voc.Release, "Release"), F(FxNew.Voc.Sibilance, "Sibilance"),
                F(FxNew.Voc.Speed, "Speed"), F(FxNew.Voc.Output, "Output"),
            });
            return list;
        }
    }

    /// <summary>
    /// One built instance of a type at a sample rate: the engine, its parameters and the fixed settings the type needs
    /// (wet only, its bands' kinds). Built off the audio thread; the audio thread only runs it.
    /// </summary>
    internal abstract class FxHost
    {
        public readonly FxType Type;
        public readonly float Rate;
        public readonly ParamValues V;

        protected FxHost(FxType type, float rate, ParamDef[] table)
        {
            Type = type;
            Rate = rate;
            V = new ParamValues(table);
        }

        public abstract int Latency { get; }
        public abstract void Process(float[] l, float[] r, int n);
        public abstract void Reset();
        public virtual void SetTempo(double bpm) { }

        // Types whose data is built in the background after the host exists (convolution: the response; vocoder: the user's modulator).
        /// <summary>The host can play (a type that needs its data first says no until it has it).</summary>
        public virtual bool IsReady => true;
        /// <summary>A request is waiting for the builder.</summary>
        public virtual bool HasWork => false;
        /// <summary>Nothing is outstanding with the builder (a request made, or one being built, makes it false).</summary>
        public virtual bool Settled => true;
        /// <summary>Any thread, allocation-free: asks the builder for what the parameters now want.</summary>
        public virtual void Poll() { }
        /// <summary>The builder's thread: does the waiting request.</summary>
        public virtual void Work() { }

        /// <summary>Builds a type's host (any thread but the audio thread's: it allocates).</summary>
        public static FxHost Create(FxType type, float rate, FxUserData user = null) => type switch
        {
            FxType.Eq => new EqHost(rate),
            FxType.Filter => new FilterHost(rate),
            FxType.Comp => new CompHost(rate),
            FxType.Clipper => new ClipperHost(rate),
            FxType.Delay => new DelayHost(rate),
            FxType.Verb => new VerbHost(rate),
            FxType.Dimension => new DimensionHost(rate),
            FxType.Bitcrusher => new BitcrusherHost(rate),
            FxType.Shifter => new ShifterHost(rate),
            FxType.PitchDelay => new PitchDelayHost(rate),
            FxType.Convolution => new ConvolutionHost(rate, user ?? new FxUserData()),
            FxType.Vocoder => new VocoderHost(rate, user ?? new FxUserData()),
            _ => null,
        };

        sealed class EqHost : FxHost
        {
            readonly NeoBuzz.Eq.EqEngine e;
            public EqHost(float rate) : base(FxType.Eq, rate, NeoBuzz.Eq.Params.Table)
            {
                var t = NeoBuzz.Eq.Params.Table;
                var types = new[] { NeoBuzz.Eq.BandType.LowShelf, NeoBuzz.Eq.BandType.Bell, NeoBuzz.Eq.BandType.Bell, NeoBuzz.Eq.BandType.HighShelf };
                for (int b = 0; b < NeoBuzz.Eq.EqDesign.Bands; b++) V.Set(NeoBuzz.Eq.EqDesign.On(b), b < 4 ? 1 : 0);
                for (int b = 0; b < 4; b++) V.Set(NeoBuzz.Eq.EqDesign.TypeOf(b), (int)types[b]);
                foreach (int b in new[] { 0, 3 }) V.Set(NeoBuzz.Eq.EqDesign.Q(b), t[NeoBuzz.Eq.EqDesign.Q(b)].Raw(0.7071));
                V.Set(NeoBuzz.Eq.EqDesign.Slope(0), 0);
                V.Set(NeoBuzz.Eq.EqDesign.Slope(3), 0);
                e = new NeoBuzz.Eq.EqEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => e.Latency;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, n);
            public override void Reset() => e.Reset();
        }

        sealed class FilterHost : FxHost
        {
            readonly NeoBuzz.Filter.FilterEngine e;
            public FilterHost(float rate) : base(FxType.Filter, rate, NeoBuzz.Filter.Params.Table)
            {
                V.Set(NeoBuzz.Filter.P.Mix, V.Table[NeoBuzz.Filter.P.Mix].Max);
                e = new NeoBuzz.Filter.FilterEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => e.Latency;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, n);
            public override void Reset() => e.Reset();
            public override void SetTempo(double bpm) => e.Tempo = bpm;
        }

        sealed class CompHost : FxHost
        {
            readonly NeoBuzz.Comp.CompEngine e;
            public CompHost(float rate) : base(FxType.Comp, rate, NeoBuzz.Comp.Params.Table)
            {
                V.Set(NeoBuzz.Comp.P.Mix, V.Table[NeoBuzz.Comp.P.Mix].Max);
                V.Set(NeoBuzz.Comp.P.Lookahead, 0);
                e = new NeoBuzz.Comp.CompEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => e.Latency;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, null, null, n);
            public override void Reset() => e.Reset();
        }

        sealed class ClipperHost : FxHost
        {
            readonly NeoBuzz.Clipper.ClipperEngine e;
            public ClipperHost(float rate) : base(FxType.Clipper, rate, NeoBuzz.Clipper.Params.Table)
            {
                V.Set(NeoBuzz.Clipper.P.Mix, V.Table[NeoBuzz.Clipper.P.Mix].Max);
                V.Set(NeoBuzz.Clipper.P.Delta, 0);
                e = new NeoBuzz.Clipper.ClipperEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => e.Latency;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, n);
            public override void Reset() => e.Reset();
        }

        sealed class DelayHost : FxHost
        {
            readonly NeoBuzz.Delay.DelayEngine e;
            public DelayHost(float rate) : base(FxType.Delay, rate, NeoBuzz.Delay.Params.Table)
            {
                V.Set(NeoBuzz.Delay.P.Mix, V.Table[NeoBuzz.Delay.P.Mix].Max);
                e = new NeoBuzz.Delay.DelayEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => e.Latency;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, n);
            public override void Reset() => e.Reset();
            public override void SetTempo(double bpm) => e.Tempo = bpm;
        }

        sealed class VerbHost : FxHost
        {
            readonly NeoBuzz.Verb.VerbEngine e;
            public VerbHost(float rate) : base(FxType.Verb, rate, NeoBuzz.Verb.Params.Table)
            {
                V.Set(NeoBuzz.Verb.P.Mix, V.Table[NeoBuzz.Verb.P.Mix].Max);
                e = new NeoBuzz.Verb.VerbEngine(V);
                e.SetSampleRate(rate);
                e.Reset();
            }
            public override int Latency => 0;
            public override void Process(float[] l, float[] r, int n) => e.Process(l, r, n);
            public override void Reset() => e.Reset();
        }
    }
}
