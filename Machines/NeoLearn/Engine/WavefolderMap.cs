#nullable disable
// Neo Learn Wavefolder's macro map (docs/specs/neo-learn.md §9, L2a as built): a sine (slot A, Analog) phase-locked to a second sine (slot B at ratio 1,
// the Timbre FM through Dye's FM warp), through the shared engine's wavefolder (Warps.Fold: 1 to 6 stages in series, a symmetry offset, run at High
// quality) and the low-pass gate (FilterType.Lpg: a vactrol-like gate that follows the filter envelope, with a strike at note-on). The filter envelope
// is the one ADSR, so the gate opens and closes with the note.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class WavefolderMap : MacroMap
    {
        // The core controls' positions.
        public const int FoldDrive = 0, FoldSymmetry = 1, FoldStages = 2, TimbreFm = 3, LpgMode = 4, GateStrike = 5;

        /// <summary>Timbre FM at 100 % is Dye's FM warp at this amount (a modulation index of 2 at 1:1).</summary>
        public const double FmAtFull = 0.5;

        static readonly int[] Slots = { DyeP.AType, DyeP.BType };

        readonly ParamDef[] controls;

        public WavefolderMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 1);                              // High (4x): the folds are edged
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 2);                                // Analog sine
            dye.Set(DyeP.BType, 2);                                // the FM partner: not heard
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AShape, 0);
            dye.Set(DyeP.BShape, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.APhase, 0);
            dye.Set(DyeP.ARandom, 0);
            dye.Set(DyeP.BPhase, 0);
            dye.Set(DyeP.BRandom, 0);
            dye.Set(DyeP.ADrift, 0);
            dye.Set(DyeP.BDrift, 0);
            dye.Set(DyeP.AWarp1, 8);                               // FM, from B
            dye.Set(DyeP.AWarp2, 0);
            dye.Set(DyeP.AMod, 1);
            dye.Set(DyeP.BWarp1, 0);
            dye.Set(DyeP.BWarp2, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.BLevel, 0);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);                               // (the low-pass gate, below)
            dye.Set(DyeP.F2Type, 0);
            dye.Ext(1, SlotExt.ExactRatio, 1);
            dye.Ext(ExtSlot.Filter1, SlotExt.FKind, 2);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            dye.Ext(0, SlotExt.FoldDrive, c[FoldDrive]);
            dye.Ext(0, SlotExt.FoldSym, c[FoldSymmetry]);
            dye.Ext(0, SlotExt.FoldStages, Math.Clamp(Math.Round(c[FoldStages]), 1, 6));
            dye.Set(DyeP.AWarp1Amt, FmAtFull * c[TimbreFm]);
            dye.Ext(ExtSlot.Filter1, SlotExt.FMode, c[LpgMode]);
            dye.Ext(ExtSlot.Filter1, SlotExt.FStrike, c[GateStrike]);
            Globals(c, dye, 5, Slots);
        }

        /// <summary>One fold stage (the engine's: the identity inside ±1, reflected at ±1, ±3, ...), in plain arithmetic for the picture.</summary>
        public static double FoldOnce(double u)
        {
            double v = u * 0.25 + 0.75;
            v -= Math.Floor(v);
            return 4 * Math.Abs(v - 0.5) - 1;
        }

        /// <summary>The wavefolder's transfer curve at <paramref name="x"/> (−1 … 1): the symmetry's offset, then <paramref name="stages"/> folds of the stage gain Wavefold gives.</summary>
        public static double Transfer(double x, double drive, double symmetry, int stages)
        {
            double g = 1 + NoteBank.FoldGainAt((float)drive), y = x + 0.5 * Math.Clamp(symmetry, -1, 1);
            for (int s = 0; s < Math.Clamp(stages, 1, 6); s++) y = FoldOnce(y * g);
            return y;
        }
    }
}
