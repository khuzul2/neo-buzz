#nullable disable
// Neo Learn FM's macro map (docs/specs/neo-learn.md §9, L1a as built): three Analog sine operators on Dye Essence's slots.
// A is the carrier; B is the modulator (phase modulation through A's PM warp), with PM self-feedback and an exact ratio;
// C is the third operator, modulating B at a fixed high ratio through B's PM warp. Env2 (decay = Mod Decay) and the note's
// velocity move the index through two modulation routes each; Env1 (a fast decay) does the same for Metallic Edge, and also adds
// a click of index to the carrier, so the edge is heard at any FM Depth.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class FmMap : MacroMap
    {
        // The core controls' positions.
        public const int ModRatio = 0, FmIndex = 1, ModDecay = 2, Feedback = 3, MetallicEdge = 4, VelToFm = 5;

        /// <summary>Slots: A the carrier, B the modulator, C the third operator.</summary>
        public const int Carrier = 0, Modulator = 1, Op3 = 2;
        /// <summary>The third operator's ratio to the note.</summary>
        public const double Op3Ratio = 7;
        /// <summary>The share of the index that stays after the Env2 decay has run out (the "body" under the transient).</summary>
        public const double Body = 0.3;
        /// <summary>PM self-feedback in cycles at 100 % Feedback: the modulator's last output moves its own phase by this much.</summary>
        public const double FeedbackCycles = 0.6;
        /// <summary>Metallic Edge's envelope (Env1): a fast decay, seconds.</summary>
        public const double EdgeDecay = 0.08;
        /// <summary>At 100 % Metallic Edge the carrier's own index also gets this much, on Env1: the third operator's click is heard whatever FM Depth is.</summary>
        public const double EdgeIndex = 0.3;

        readonly ParamDef[] controls;

        public FmMap(ParamDef[] controls)
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
            for (int s = 0; s < 3; s++)
            {
                int b = DyeP.AType + s * (DyeP.BType - DyeP.AType);
                dye.Set(b, 2);                                    // Analog: a sine
                dye.Set(b + (DyeP.AOct - DyeP.AType), 0);
                dye.Set(b + (DyeP.ASemi - DyeP.AType), 0);
                dye.Set(b + (DyeP.AFine - DyeP.AType), 0);
                dye.Set(b + (DyeP.APan - DyeP.AType), 0);
                dye.Set(b + (DyeP.APhase - DyeP.AType), 0);
                dye.Set(b + (DyeP.ARandom - DyeP.AType), 0);   // every voice starts at phase 0: the same note, the same bits
                dye.Set(b + (DyeP.AWarp1Amt - DyeP.AType), 0);
                dye.Set(b + (DyeP.AWarp2 - DyeP.AType), 0);
                dye.Set(b + (DyeP.AWarp2Amt - DyeP.AType), 0);
                dye.Set(b + (DyeP.AToF2 - DyeP.AType), 0);
            }
            dye.Set(DyeP.AShape, 0);
            dye.Set(DyeP.BShape, 0);
            dye.Set(DyeP.CShape, 0);
            dye.Set(DyeP.ADrift, 0);                              // exact operators: no analog drift
            dye.Set(DyeP.BDrift, 0);
            dye.Set(DyeP.CDrift, 0);
            // A is phase-modulated by B; B by C (the PM warp, Dye's Warp.Pm = 9).
            dye.Set(DyeP.AWarp1, 9);
            dye.Set(DyeP.AMod, 1);
            dye.Set(DyeP.BWarp1, 9);
            dye.Set(DyeP.BMod, 2);
            dye.Set(DyeP.CWarp1, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.BLevel, 0);                              // the modulators are not heard: they only render as modulators
            dye.Set(DyeP.CLevel, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
            // Env2: instant attack, decays to nothing in Mod Decay. Env1: the same, fast (Metallic Edge).
            dye.Set(DyeP.E2A, 0.0005);
            dye.Set(DyeP.E2Hold, 0);
            dye.Set(DyeP.E2S, 0);
            dye.Set(DyeP.E1A, 0.0005);
            dye.Set(DyeP.E1Hold, 0);
            dye.Set(DyeP.E1D, EdgeDecay);
            dye.Set(DyeP.E1S, 0);
            // The routes: Env2 and velocity move A's PM amount (the index), Env1 and velocity B's (the edge).
            dye.Set(DyeP.R1Src, (int)RouteSource.ModEnv2);
            dye.Set(DyeP.R1Dst, (int)RouteDest.AWarp1);
            dye.Set(DyeP.R2Src, (int)RouteSource.Velocity);
            dye.Set(DyeP.R2Dst, (int)RouteDest.AWarp1);
            dye.Set(DyeP.R3Src, (int)RouteSource.ModEnv1);
            dye.Set(DyeP.R3Dst, (int)RouteDest.BWarp1);
            dye.Set(DyeP.R4Src, (int)RouteSource.Velocity);
            dye.Set(DyeP.R4Dst, (int)RouteDest.BWarp1);
            dye.Set(DyeP.R5Src, (int)RouteSource.ModEnv1);
            dye.Set(DyeP.R5Dst, (int)RouteDest.AWarp1);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            double index = c[FmIndex], vs = c[VelToFm], edge = c[MetallicEdge];

            dye.Ext(Modulator, SlotExt.ExactRatio, c[ModRatio]);
            dye.Ext(Op3, SlotExt.ExactRatio, Op3Ratio);
            dye.Ext(Modulator, SlotExt.Feedback, FeedbackCycles * c[Feedback]);

            // The index (A's PM amount, 0 … 1 of ±1 cycle): Body of it always, the rest following Env2, and velocity moves it by
            // Velocity to FM (a soft note is weaker, a hard one stronger; a note at 0.8 is as at 0). All of it is scaled by FM Depth.
            dye.Set(DyeP.AWarp1Amt, index * Body * (1 - vs));
            dye.Set(DyeP.R1Amt, index * (1 - Body));
            dye.Set(DyeP.R2Amt, index * 0.45 * vs);
            dye.Set(DyeP.E2D, c[ModDecay]);

            // Metallic Edge: the third operator's PM into B, a fast transient, scaled by velocity in the same way.
            dye.Set(DyeP.BWarp1Amt, edge * 0.1 * (1 - vs));
            dye.Set(DyeP.R3Amt, edge * 0.9);
            dye.Set(DyeP.R4Amt, edge * 0.35 * vs);
            dye.Set(DyeP.R5Amt, edge * EdgeIndex);

            Globals(c, dye, 5, AllSlots);
        }

        /// <summary>
        /// Sideband amplitudes of the carrier at the ratio and index the controls give: the Bessel series J_n(β) of a sine modulator (β = 2π ×
        /// index × the envelope's peak), as the picture draws them; entry n is the sideband at carrier + n × ratio (n = -N … N).
        /// </summary>
        public static void Sidebands(double ratio, double index, double[] amps, out double[] hz)
        {
            int n = amps.Length / 2;
            hz = new double[amps.Length];
            double beta = 2 * Math.PI * Math.Clamp(index, 0, 1);
            for (int k = -n; k <= n; k++)
            {
                amps[k + n] = Math.Abs(Bessel(Math.Abs(k), beta));
                hz[k + n] = 1 + k * ratio;
            }
        }

        /// <summary>J_n(x) by its power series (n ≥ 0, x up to about 2π; plain double arithmetic, for the picture only).</summary>
        public static double Bessel(int n, double x)
        {
            double term = 1, sum = 0;
            double half = x / 2;
            for (int i = 1; i <= n; i++) term *= half / i;
            for (int m = 0; m < 40; m++)
            {
                sum += term;
                term *= -half * half / ((m + 1) * (m + 1 + n));
            }
            return sum;
        }
    }
}
