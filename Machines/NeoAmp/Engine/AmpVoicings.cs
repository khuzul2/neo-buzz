#nullable disable
// Neo Amp (docs/specs/neo-amp.md §3): the eight voicings. A voicing is a set of numbers for the one signal chain (AmpCore): how many
// triode-like gain stages (1 to 4), the fixed gain in front of each, each stage's asymmetry and bias, the coupling high-pass in front
// of it and the low-pass behind it (the stage's capacitance), where the Gain knob adds its dB, a bright filter, the tone stack's
// capacitor scale, and the power stage's drive range, Presence and Resonance shelves and level.

namespace NeoBuzz.Amp
{
    internal sealed class Voicing
    {
        public string Name;
        public int Stages;
        /// <summary>Fixed gain in dB in front of each stage (the stage after the Gain knob gets the knob's dB as well).</summary>
        public double[] StageDb;
        /// <summary>Asymmetry k of each stage's curve: the negative half saturates at −k (1 = symmetric).</summary>
        public double[] Asym;
        /// <summary>Bias of each stage (in the curve's input units): even harmonics.</summary>
        public double[] Bias;
        /// <summary>Coupling high-pass in front of each stage, Hz.</summary>
        public double[] HpHz;
        /// <summary>Low-pass behind each stage (twice, the stage's loading), Hz.</summary>
        public double[] LpHz;
        /// <summary>The stage the Gain knob feeds (its dB is added in front of this stage).</summary>
        public int GainStage;
        /// <summary>Bright boost of the highs when the Gain knob is low, dB, and its corner.</summary>
        public double BrightDb, BrightHz;
        /// <summary>Capacitor scales of the tone stack: treble, bass side, mid side.</summary>
        public double[] CapScale;
        /// <summary>The power stage: drive in dB at Master 0 and 100 %, the curve's asymmetry, how deep Sag goes (0 … 1 of the supply).</summary>
        public double PowerMinDb, PowerMaxDb, PowerAsym, SagDepth;
        /// <summary>The power stage's input bandwidth: two one-pole low-passes in front of it, Hz.</summary>
        public double PowerLpHz = 9000;
        /// <summary>Presence: high shelf corner and gain at 100 %. Resonance: low shelf corner and gain at 100 %.</summary>
        public double PresHz, PresDb, ResHz, ResDb;
        /// <summary>Level of the amp's output in dB (so the voicings sit at a similar loudness).</summary>
        public double LevelDb;

        static double[] A(params double[] v) => v;

        static readonly double[] GuitarCaps = { 1.0, 1.0, 1.0 };
        static readonly double[] BassCaps = { 2.5, 2.5, 3.0 };

        public static readonly Voicing[] All =
        {
            new Voicing { Name = "Clean", Stages = 2, StageDb = A(1.6, -12, 0, 0), Asym = A(1.2, 1.1, 1, 1), Bias = A(0.12, 0.08, 0, 0), HpHz = A(25, 30, 0, 0), LpHz = A(14000, 11000, 0, 0),
                GainStage = 1, BrightDb = 0, BrightHz = 2500, CapScale = GuitarCaps, PowerMinDb = -10, PowerMaxDb = 6, PowerAsym = 1.0, SagDepth = 0.35,
                PresHz = 3500, PresDb = 8, ResHz = 110, ResDb = 7, LevelDb = -9.5 },
            new Voicing { Name = "Bright clean", Stages = 2, StageDb = A(3, -11, 0, 0), Asym = A(1.25, 1.1, 1, 1), Bias = A(0.14, 0.08, 0, 0), HpHz = A(30, 40, 0, 0), LpHz = A(16000, 13000, 0, 0),
                GainStage = 1, BrightDb = 9, BrightHz = 2200, CapScale = GuitarCaps, PowerMinDb = -10, PowerMaxDb = 6, PowerAsym = 1.0, SagDepth = 0.3,
                PresHz = 4000, PresDb = 8, ResHz = 100, ResDb = 5, LevelDb = -9.6 },
            new Voicing { Name = "Crunch", Stages = 3, StageDb = A(6, -3.5, 4, 0), Asym = A(1.25, 1.2, 1.1, 1), Bias = A(0.15, 0.12, 0.06, 0), HpHz = A(30, 50, 60, 0), LpHz = A(12000, 9000, 8000, 0),
                GainStage = 1, BrightDb = 4, BrightHz = 2000, CapScale = GuitarCaps, PowerMinDb = -8, PowerMaxDb = 10, PowerAsym = 1.05, SagDepth = 0.45,
                PresHz = 3200, PresDb = 8, ResHz = 105, ResDb = 7, LevelDb = -11.7 },
            new Voicing { Name = "Classic rock", Stages = 3, StageDb = A(10, 0, 10, 0), Asym = A(1.3, 1.25, 1.15, 1), Bias = A(0.18, 0.14, 0.08, 0), HpHz = A(35, 70, 90, 0), LpHz = A(10000, 8000, 7000, 0),
                GainStage = 1, BrightDb = 0, BrightHz = 2000, CapScale = GuitarCaps, PowerMinDb = -6, PowerMaxDb = 14, PowerAsym = 1.1, SagDepth = 0.55,
                PresHz = 3000, PresDb = 9, ResHz = 100, ResDb = 8, LevelDb = -12.9 },
            new Voicing { Name = "Lead", Stages = 4, StageDb = A(13.6, -2, 12, 9.6), Asym = A(1.3, 1.25, 1.2, 1.1), Bias = A(0.2, 0.16, 0.12, 0.06), HpHz = A(40, 80, 110, 130), LpHz = A(9000, 7500, 6500, 6000),
                GainStage = 1, BrightDb = 0, BrightHz = 2000, CapScale = GuitarCaps, PowerMinDb = -6, PowerMaxDb = 12, PowerAsym = 1.1, SagDepth = 0.5,
                PresHz = 3000, PresDb = 8, ResHz = 100, ResDb = 7, LevelDb = -12.0 },
            new Voicing { Name = "High gain", Stages = 4, StageDb = A(18, 1, 15.6, 14), Asym = A(1.35, 1.3, 1.25, 1.15), Bias = A(0.22, 0.18, 0.14, 0.08), HpHz = A(60, 140, 200, 240), LpHz = A(8000, 6500, 5500, 5000),
                GainStage = 1, BrightDb = 0, BrightHz = 2000, CapScale = GuitarCaps, PowerMinDb = -8, PowerMaxDb = 10, PowerAsym = 1.05, SagDepth = 0.3,
                PresHz = 3500, PresDb = 9, ResHz = 95, ResDb = 8, LevelDb = -11.8 },
            new Voicing { Name = "Bass clean", Stages = 2, StageDb = A(1.6, -12, 0, 0), Asym = A(1.15, 1.1, 1, 1), Bias = A(0.1, 0.06, 0, 0), HpHz = A(12, 15, 0, 0), LpHz = A(12000, 9000, 0, 0),
                GainStage = 1, BrightDb = 0, BrightHz = 2500, CapScale = BassCaps, PowerMinDb = -10, PowerMaxDb = 6, PowerAsym = 1.0, SagDepth = 0.25,
                PresHz = 2500, PresDb = 6, ResHz = 60, ResDb = 6, LevelDb = 0.6 },
            new Voicing { Name = "Bass drive", Stages = 3, StageDb = A(6, -3.5, 4, 0), Asym = A(1.25, 1.2, 1.1, 1), Bias = A(0.14, 0.1, 0.06, 0), HpHz = A(15, 30, 40, 0), LpHz = A(9000, 7000, 6000, 0),
                GainStage = 1, BrightDb = 0, BrightHz = 2000, CapScale = BassCaps, PowerMinDb = -8, PowerMaxDb = 10, PowerAsym = 1.05, SagDepth = 0.4,
                PresHz = 2500, PresDb = 6, ResHz = 65, ResDb = 7, LevelDb = -4.0 },
        };
    }
}
