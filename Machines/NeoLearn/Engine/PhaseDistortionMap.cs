#nullable disable
// Neo Learn Phase Distortion's macro map (docs/specs/neo-learn.md §9, L2b as built): a sine (slot A, Analog) whose phase is read through a bent geometry (the
// shared engine's Warp.Pd: six shapes, the bend the first warp's amount), a ring modulator from a second sine (slot B at an inharmonic ratio: Dye's Ring warp as the
// second warp), a hard-synced, windowed sine (slot C: Dye's Window-sync warp) that Resonance mixes in, and the shared engine's chorus after the voices. Env2 decays
// the bend (and the resonance sweep with it) on every note: Bend Decay is its decay time. The engine runs at High quality (4x): the bend is an edged warp.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class PhaseDistortionMap : MacroMap
    {
        // The core controls' positions.
        public const int PhaseBend = 0, Resonance = 1, PdWave = 2, BendDecay = 3, RingMod = 4, ChorusAmount = 5;

        /// <summary>The share of Phase Bend (and of the resonance sweep) that stays after Env2's decay has run out: the note warms up to this and holds.</summary>
        public const double Body = 0.25;
        /// <summary>The ring modulator's carrier: this multiple of the note (inharmonic, so the sidebands ring like a bell).</summary>
        public const double RingRatio = 2.756;
        /// <summary>At Resonance 100 % the windowed sine is this loud (linear) and the plain wave this much quieter.</summary>
        public const double ResonanceLevel = 0.7, ResonanceDuck = 0.35;

        static readonly int[] Slots = { DyeP.AType, DyeP.CType };

        readonly ParamDef[] controls;

        public PhaseDistortionMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override int FxFlags => FxChorus;

        public override void Post(double[] c, LearnFxSet fx)
        {
            if (fx?.Chorus != null) fx.Chorus.Amount = (float)Math.Clamp(c[ChorusAmount], 0, 1);
        }

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 1);                              // High (4x): the bend and the window sync are edged
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
                dye.Set(b + (DyeP.ARandom - DyeP.AType), 0);       // every voice starts at phase 0: the same note, the same bits
                dye.Set(b + (DyeP.AWarp1Amt - DyeP.AType), 0);
                dye.Set(b + (DyeP.AWarp2Amt - DyeP.AType), 0);
                dye.Set(b + (DyeP.AToF2 - DyeP.AType), 0);
            }
            dye.Set(DyeP.AShape, 0);
            dye.Set(DyeP.BShape, 0);
            dye.Set(DyeP.CShape, 0);
            dye.Set(DyeP.ADrift, 0);
            dye.Set(DyeP.BDrift, 0);
            dye.Set(DyeP.CDrift, 0);
            dye.Set(DyeP.AWarp1, 0);                               // (the phase distortion, below: Warp.Pd is not one of Dye's choices)
            dye.Set(DyeP.AWarp2, 11);                              // Ring, from B
            dye.Set(DyeP.AMod, 1);
            dye.Set(DyeP.BWarp1, 0);
            dye.Set(DyeP.BWarp2, 0);
            dye.Set(DyeP.CWarp1, 7);                               // Window sync
            dye.Set(DyeP.CWarp2, 0);
            dye.Set(DyeP.BLevel, 0);                              // the carrier of the ring modulator is not heard
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
            dye.Ext(1, SlotExt.ExactRatio, RingRatio);
            // Env2: instant attack, decays to nothing in Bend Decay; it carries the bend and the resonance sweep.
            dye.Set(DyeP.E2A, 0.0005);
            dye.Set(DyeP.E2Hold, 0);
            dye.Set(DyeP.E2S, 0);
            dye.Set(DyeP.R1Src, (int)RouteSource.ModEnv2);
            dye.Set(DyeP.R1Dst, (int)RouteDest.AWarp1);
            dye.Set(DyeP.R2Src, (int)RouteSource.ModEnv2);
            dye.Set(DyeP.R2Dst, (int)RouteDest.CWarp1);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            double bend = Math.Clamp(c[PhaseBend], 0, 1), res = Math.Clamp(c[Resonance], 0, 1);
            dye.Ext(0, SlotExt.PdShape, Math.Clamp(Math.Round(c[PdWave]), 0, 5));
            dye.Set(DyeP.AWarp1Amt, bend * Body);
            dye.Set(DyeP.R1Amt, bend * (1 - Body));
            dye.Set(DyeP.E2D, c[BendDecay]);
            dye.Set(DyeP.AWarp2Amt, Math.Clamp(c[RingMod], 0, 1));
            dye.Set(DyeP.ALevel, 1 - ResonanceDuck * res);
            dye.Set(DyeP.CLevel, ResonanceLevel * res);
            dye.Set(DyeP.CWarp1Amt, res * Body);
            dye.Set(DyeP.R2Amt, res * (1 - Body));
            Globals(c, dye, 5, Slots);
        }

        /// <summary>The phase the cosine is read at (0 … 1, Double Sine to 2) for a read position <paramref name="p"/> (0 … 1): the engine's own geometry at <paramref name="bend"/> (a picture draws what the voices use).</summary>
        public static double Phase(int shape, double bend, double p)
        {
            double t = Warps.PdTarget(Math.Clamp(shape, 0, Warps.PdShapes - 1), p);
            return p + Math.Clamp(bend, 0, 1) * (t - p);
        }

        /// <summary>The wave at read position <paramref name="p"/>: the sine read through <see cref="Phase"/>.</summary>
        public static double Wave(int shape, double bend, double p) => PortableMath.Sin(2 * Math.PI * Phase(shape, bend, p));
    }
}
