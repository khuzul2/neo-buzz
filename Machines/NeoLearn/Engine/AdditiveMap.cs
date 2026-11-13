#nullable disable
// Neo Learn Additive's macro map (docs/specs/neo-learn.md §9, L1a as built): Dye Essence's Harmonic engine on slot A with the Saw
// set (partial k at 1/k), tilt, odd/even and the partial count, plus the shared engine's additions: inharmonicity (partial k at
// k times the root of 1 + B k squared), the comb mask and per-partial shimmer.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class AdditiveMap : MacroMap
    {
        // The core controls' positions.
        public const int Tilt = 0, OddEven = 1, Partials = 2, Inharmonicity = 3, Comb = 4, Shimmer = 5;

        /// <summary>The stiffness B at 100 % Inharmonicity, reached along the square of the knob (a fine start): partial 10 is then 18 % sharp.</summary>
        public const double StiffnessAtFull = 0.004;
        /// <summary>The comb's period in partials: 12 when it starts to bite, 4 at 100 %.</summary>
        public const double CombPeriodSoft = 12, CombPeriodHard = 4;

        static readonly int[] SlotA = { DyeP.AType };

        readonly ParamDef[] controls;

        public AdditiveMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 0);
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 5);                                // Harmonic
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.APhase, 0);
            dye.Set(DyeP.ARandom, 1);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.APos, 0);
            dye.Set(DyeP.AHSet, 0);                                // Saw: 1/k
            dye.Set(DyeP.AHSet2, 0);
            dye.Set(DyeP.AHStretch, 0);
            dye.Set(DyeP.AHDamp, 0);
            dye.Set(DyeP.AWarp1, 0);
            dye.Set(DyeP.AWarp2, 0);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            dye.Set(DyeP.AHTilt, c[Tilt]);
            // Dye's + weakens the even partials and - the odd ones. The control is the other way round (left: odd partials only,
            // right: even only), so the sign is flipped.
            dye.Set(DyeP.AHOddEven, -100 * c[OddEven]);
            dye.Set(DyeP.AHPartials, c[Partials]);
            double inh = c[Inharmonicity];
            dye.Ext(0, SlotExt.Inharm, StiffnessAtFull * inh * inh);
            dye.Ext(0, SlotExt.Comb, c[Comb]);
            dye.Ext(0, SlotExt.CombPeriod, CombPeriodSoft + (CombPeriodHard - CombPeriodSoft) * c[Comb]);
            dye.Ext(0, SlotExt.Shimmer, c[Shimmer]);
            Globals(c, dye, 3, SlotA);
        }

        /// <summary>
        /// The 64 partials' relative levels (0 … 1, the strongest 1) the controls give, as the picture draws them: the Saw set, the partial
        /// count's cut, tilt, odd/even and the comb (the engine's own formulas).
        /// </summary>
        public static void Spectrum(double tilt, double oddEven, int partials, double comb, double[] bars)
        {
            double peak = 0;
            var set = HarmonicSets.Get(0);
            for (int j = 0; j < bars.Length; j++)
            {
                int k = j + 1;
                double v = Math.Abs(set[j]) * HarmonicSets.Base(k, partials, (float)tilt, (float)-oddEven);
                if (comb > 0) v *= HarmonicSets.CombMask(k, (float)comb, (float)(CombPeriodSoft + (CombPeriodHard - CombPeriodSoft) * comb));
                bars[j] = v;
                peak = Math.Max(peak, v);
            }
            if (peak > 0) for (int j = 0; j < bars.Length; j++) bars[j] /= peak;
        }
    }
}
