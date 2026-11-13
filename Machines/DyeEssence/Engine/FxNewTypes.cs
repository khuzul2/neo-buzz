#nullable disable
// Dye Essence's effects rack (docs/specs/dye-essence.md section 4.10), P4b1: the four types that are not a Neo machine's
// engine: dimension (a stereo-widening chorus), bitcrusher, frequency shifter and pitch-shift delay. Their kernels are
// in NeoBuzz.Dsp (DimensionChorus, Bitcrusher, FrequencyShifter, PitchShiftDelay: any machine can use them); what is
// here are the parameter tables the rack's ten generic parameters map onto (a ParamDef each, so the maps, the value
// text and the tests are the other types') and the hosts that give a kernel its parameters at every block.

using System;
using System.Linq;
using NeoBuzz.Dsp;

namespace NeoBuzz.DyeEssence
{
    /// <summary>The parameter tables of the new types: index constants and the ParamDef arrays.</summary>
    internal static class FxNew
    {
        static ParamDef D(int index, string id, string name, int min, int max, double value, MapKind kind, double a = 0, double b = 1,
            string unit = "", int digits = -1, string[] names = null, bool flag = false)
        {
            var probe = new ParamDef(index, id, name, min, max, min, kind, a, b, 1, unit, digits, names, flag);
            return new ParamDef(index, id, name, min, max, probe.Raw(value), kind, a, b, 1, unit, digits, names, flag);
        }

        static ParamDef Pct(int i, string id, string name, double v) => D(i, id, name, 0, 1000, v, MapKind.Percent);
        static ParamDef Bip(int i, string id, string name) => D(i, id, name, 0, 1000, 0, MapKind.Bipolar);
        static ParamDef Hz(int i, string id, string name, double lo, double hi, double v) => D(i, id, name, 0, 1000, v, MapKind.Freq, lo, hi);
        static ParamDef Sec(int i, string id, string name, double lo, double hi, double v) => D(i, id, name, 0, 1000, v, MapKind.Time, lo, hi);
        static ParamDef Db(int i, string id, string name, double lo, double hi, double v) => D(i, id, name, 0, 1000, v, MapKind.Db, lo, hi, digits: 1);
        static ParamDef Choice(int i, string id, string name, double v, params string[] names) => D(i, id, name, 0, names.Length - 1, v, MapKind.Enum, names: names);

        public static class Dim
        {
            public const int Voices = 0, Rate = 1, Depth = 2, Delay = 3, Width = 4, Shape = 5, Tone = 6, LowCut = 7, Drift = 8, Feedback = 9;
            public static readonly ParamDef[] Table =
            {
                D(Voices, "Voices", "Voices", 0, 2, 3, MapKind.Int, 2),
                Hz(Rate, "Rate", "Rate", 0.05, 8, 0.6),
                Pct(Depth, "Depth", "Depth", 0.5),
                Sec(Delay, "Delay", "Delay", 0.001, 0.025, 0.006),
                Pct(Width, "Width", "Width", 0.8),
                Choice(Shape, "Shape", "Shape", 0, "Sine", "Triangle"),
                Hz(Tone, "Tone", "Tone", 1000, 20000, 12000),
                Hz(LowCut, "LowCut", "Low cut", 20, 800, 120),
                Pct(Drift, "Drift", "Drift", 0.15),
                Bip(Feedback, "Feedback", "Feedback"),
            };
        }

        public static class Crush
        {
            public const int Bits = 0, Rate = 1, AntiAlias = 2, Drive = 3, Quantizer = 4, Bias = 5, Dither = 6, Tone = 7, Wobble = 8, Output = 9;
            public static readonly ParamDef[] Table =
            {
                D(Bits, "Bits", "Bits", 0, 1000, 8, MapKind.Linear, 1, 16, "bits", 1),
                Hz(Rate, "Rate", "Rate", 100, 48000, 12000),
                D(AntiAlias, "AntiAlias", "Anti-alias", 0, 1, 1, MapKind.Switch),
                Db(Drive, "Drive", "Drive", -12, 24, 0),
                Choice(Quantizer, "Quantizer", "Quantizer", 0, "Mid-tread", "Mid-rise", "Companded"),
                Bip(Bias, "Bias", "Bias"),
                Pct(Dither, "Dither", "Dither", 0),
                Hz(Tone, "Tone", "Tone", 500, 20000, 16000),
                Bip(Wobble, "Wobble", "Wobble"),
                Db(Output, "Output", "Output", -24, 12, 0),
            };
        }

        public static class Shift
        {
            public const int Amount = 0, Range = 1, Mode = 2, Feedback = 3, FbTone = 4, Stereo = 5, LfoRate = 6, LfoDepth = 7, Glide = 8, Output = 9;
            /// <summary>The ends of Shift (Hz) for each Range choice.</summary>
            public static readonly double[] RangeHz = { 100, 1000, 5000 };
            public static readonly ParamDef[] Table =
            {
                Bip(Amount, "Shift", "Shift"),
                Choice(Range, "Range", "Range", 1, "100 Hz", "1 kHz", "5 kHz"),
                Choice(Mode, "Mode", "Mode", 0, "Shift", "Ring"),
                Bip(Feedback, "Feedback", "Feedback"),
                Hz(FbTone, "FbTone", "Feedback tone", 200, 12000, 3000),
                Bip(Stereo, "Stereo", "Stereo"),
                Hz(LfoRate, "LfoRate", "LFO rate", 0.05, 10, 0.5),
                Bip(LfoDepth, "LfoDepth", "LFO depth"),
                Sec(Glide, "Glide", "Glide", 0.001, 2, 0.05),
                Db(Output, "Output", "Output", -24, 12, 0),
            };
        }

        public static class PitchDelay
        {
            public const int Time = 0, Pitch = 1, Fine = 2, Feedback = 3, Grain = 4, Mode = 5, Tone = 6, LowCut = 7, Spread = 8, Cross = 9;
            public static readonly ParamDef[] Table =
            {
                Sec(Time, "Time", "Time", 0.01, 2, 0.3),
                D(Pitch, "Pitch", "Pitch", 0, 48, 0, MapKind.Int, -24, 1, "st", flag: true),
                D(Fine, "Fine", "Fine", 0, 200, 0, MapKind.Int, -100, 1, "ct", flag: true),
                Pct(Feedback, "Feedback", "Feedback", 0.4),
                Sec(Grain, "Grain", "Grain", 0.02, 0.2, 0.06),
                Choice(Mode, "Mode", "Mode", 0, "Per repeat", "Output only"),
                Hz(Tone, "Tone", "Tone", 500, 20000, 6000),
                Hz(LowCut, "LowCut", "Low cut", 20, 1000, 80),
                Bip(Spread, "Spread", "Spread"),
                Pct(Cross, "Cross", "Cross-feed", 0.3),
            };
        }

        public static class Conv
        {
            public const int Ir = 0, Size = 1, PreDelay = 2, Decay = 3, Damping = 4, LowCut = 5, HighCut = 6, Width = 7, Reverse = 8, Output = 9;
            /// <summary>The choices of IR: the built-in responses (ImpulseResponses.Names), then the one imported by the user.</summary>
            public static readonly string[] IrNames = ImpulseResponses.Names.Concat(new[] { "User" }).ToArray();
            public static int UserChoice => IrNames.Length - 1;
            /// <summary>The parameters that change the response (all but Output): a change builds a new convolver in the background.</summary>
            public const int Designed = 9;
            public static readonly ParamDef[] Table =
            {
                Choice(Ir, "IR", "IR", 4, IrNames),
                D(Size, "Size", "Size", 0, 1000, 1.0, MapKind.Linear, 0.25, 1.5, "x", 2),
                Sec(PreDelay, "PreDelay", "Pre-delay", 0.001, 0.25, 0.01),
                Bip(Decay, "Decay", "Decay"),
                Bip(Damping, "Damping", "Damping"),
                Hz(LowCut, "LowCut", "Low cut", 20, 1000, 40),
                Hz(HighCut, "HighCut", "High cut", 1000, 20000, 16000),
                D(Width, "Width", "Width", 0, 1000, 1.0, MapKind.Linear, 0, 2, "x", 2),
                D(Reverse, "Reverse", "Reverse", 0, 1, 0, MapKind.Switch),
                Db(Output, "Output", "Output", -24, 12, 0),
            };
        }

        public static class Voc
        {
            public const int Source = 0, Bands = 1, Formant = 2, Low = 3, High = 4, Attack = 5, Release = 6, Sibilance = 7, Speed = 8, Output = 9;
            public static readonly ParamDef[] Table =
            {
                Choice(Source, "Source", "Modulator", 0, "Internal", "Sample"),
                D(Bands, "Bands", "Bands", 0, 24, 16, MapKind.Int, 8, 1, "bands"),
                D(Formant, "Formant", "Formant", 0, 24, 0, MapKind.Int, -12, 1, "st", flag: true),
                Hz(Low, "Low", "Low", 80, 400, 120),
                Hz(High, "High", "High", 3000, 16000, 10000),
                Sec(Attack, "Attack", "Attack", 0.0005, 0.02, 0.002),
                Sec(Release, "Release", "Release", 0.005, 0.5, 0.06),
                Pct(Sibilance, "Sibilance", "Sibilance", 0.35),
                D(Speed, "Speed", "Speed", 0, 1000, 1.0, MapKind.Linear, 0.25, 4, "x", 2),
                Db(Output, "Output", "Output", -24, 12, 0),
            };
        }
    }

    internal abstract class FxNewHost : FxHost
    {
        protected FxNewHost(FxType type, float rate, ParamDef[] table) : base(type, rate, table) { }

        protected double Phys(int i) => V.Value(i);
        protected float Bipolar(int i) => (float)V.Value(i);
    }

    internal sealed class DimensionHost : FxNewHost
    {
        readonly DimensionChorus c;
        public DimensionHost(float rate) : base(FxType.Dimension, rate, FxNew.Dim.Table) { c = new DimensionChorus(rate); }
        public override int Latency => c.Latency;
        public override void Reset() => c.Reset();
        public override void Process(float[] l, float[] r, int n)
        {
            c.Voices = V.Choice(FxNew.Dim.Voices) + 2;
            c.RateHz = (float)Phys(FxNew.Dim.Rate);
            c.Depth = (float)Phys(FxNew.Dim.Depth);
            c.DelayMs = (float)(Phys(FxNew.Dim.Delay) * 1000);
            c.Width = (float)Phys(FxNew.Dim.Width);
            c.Triangle = V.Choice(FxNew.Dim.Shape) == 1;
            c.ToneHz = Phys(FxNew.Dim.Tone);
            c.LowCutHz = Phys(FxNew.Dim.LowCut);
            c.Drift = (float)Phys(FxNew.Dim.Drift);
            c.Feedback = Bipolar(FxNew.Dim.Feedback) * 0.9f;
            c.Process(l, r, n);
        }
    }

    internal sealed class BitcrusherHost : FxNewHost
    {
        readonly Bitcrusher c;
        public BitcrusherHost(float rate) : base(FxType.Bitcrusher, rate, FxNew.Crush.Table) { c = new Bitcrusher(rate); }
        public override int Latency => c.Latency;
        public override void Reset() => c.Reset();
        public override void Process(float[] l, float[] r, int n)
        {
            c.Bits = (float)Phys(FxNew.Crush.Bits);
            c.RateHz = Phys(FxNew.Crush.Rate);
            c.AntiAlias = V.On(FxNew.Crush.AntiAlias);
            c.Drive = (float)DspMath.DbToGain(Phys(FxNew.Crush.Drive));
            c.Quantizer = (CrusherQuantizer)V.Choice(FxNew.Crush.Quantizer);
            c.Bias = Bipolar(FxNew.Crush.Bias);
            c.Dither = (float)Phys(FxNew.Crush.Dither);
            c.ToneHz = Phys(FxNew.Crush.Tone);
            c.Wobble = Bipolar(FxNew.Crush.Wobble);
            c.Output = (float)DspMath.DbToGain(Phys(FxNew.Crush.Output));
            c.Process(l, r, n);
        }
    }

    internal sealed class ShifterHost : FxNewHost
    {
        readonly FrequencyShifter c;
        public ShifterHost(float rate) : base(FxType.Shifter, rate, FxNew.Shift.Table) { c = new FrequencyShifter(rate); }
        public override int Latency => c.Latency;
        public override void Reset() => c.Reset();
        public override void Process(float[] l, float[] r, int n)
        {
            double range = FxNew.Shift.RangeHz[V.Choice(FxNew.Shift.Range)];
            double v = Phys(FxNew.Shift.Amount);
            c.ShiftHz = v * v * v * range;
            c.Ring = V.Choice(FxNew.Shift.Mode) == 1;
            c.Feedback = Bipolar(FxNew.Shift.Feedback) * 0.9f;
            c.FeedbackToneHz = Phys(FxNew.Shift.FbTone);
            c.StereoHz = Phys(FxNew.Shift.Stereo) * 0.05 * range;
            c.LfoRateHz = (float)Phys(FxNew.Shift.LfoRate);
            c.LfoHz = Phys(FxNew.Shift.LfoDepth) * 0.25 * range;
            c.GlideSeconds = Phys(FxNew.Shift.Glide);
            c.Output = (float)DspMath.DbToGain(Phys(FxNew.Shift.Output));
            c.Process(l, r, n);
        }
    }

    internal sealed class PitchDelayHost : FxNewHost
    {
        readonly PitchShiftDelay c;
        public PitchDelayHost(float rate) : base(FxType.PitchDelay, rate, FxNew.PitchDelay.Table) { c = new PitchShiftDelay(rate); }
        public override int Latency => c.Latency;
        public override void Reset() => c.Reset();
        public override void Process(float[] l, float[] r, int n)
        {
            c.TimeMs = (float)(Phys(FxNew.PitchDelay.Time) * 1000);
            c.Semitones = (float)Phys(FxNew.PitchDelay.Pitch);
            c.Cents = (float)Phys(FxNew.PitchDelay.Fine);
            c.Feedback = (float)Phys(FxNew.PitchDelay.Feedback) * 0.95f;
            c.GrainMs = (float)(Phys(FxNew.PitchDelay.Grain) * 1000);
            c.PerRepeat = V.Choice(FxNew.PitchDelay.Mode) == 0;
            c.ToneHz = Phys(FxNew.PitchDelay.Tone);
            c.LowCutHz = Phys(FxNew.PitchDelay.LowCut);
            c.Spread = Bipolar(FxNew.PitchDelay.Spread);
            c.Cross = (float)Phys(FxNew.PitchDelay.Cross);
            c.Process(l, r, n);
        }
    }
}
