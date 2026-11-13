#nullable disable
// Neo Learn Wavetable's macro map (docs/specs/neo-learn.md §9, L1a as built): Dye Essence's Wavetable engine on slot A (Dye's ten
// tables, or User), Bend (left of centre) and Sync (right) as the two warps at High quality, LFO 1 moving the frame position
// through a route, and a sine Sub at the root pitch.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class WavetableMap : MacroMap
    {
        // The core controls' positions.
        public const int FramePos = 0, PhaseWarp = 1, TableSelect = 2, MotionRate = 3, MotionDepth = 4, SubAnchor = 5;

        /// <summary>The table choice that means the user's own (Dye's "User", after its ten built-in tables).</summary>
        public const int UserTable = 10;
        /// <summary>Motion Depth 100 % moves the position by this much (of the table) each way from where it sits.</summary>
        public const double MotionTravel = 0.5;

        static readonly int[] SlotA = { DyeP.AType };

        readonly ParamDef[] controls;

        public WavetableMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 1);                              // High (4x): Bend and Sync are edged warps
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 1);                                // Wavetable
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.APhase, 0);
            dye.Set(DyeP.ARandom, 1);                              // random start phases: unison adds in power
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.AWarp1, 1);                               // Bend+ (left of centre)
            dye.Set(DyeP.AWarp2, 6);                               // Sync (right of centre)
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubShape, 0);                             // a sine ...
            dye.Set(DyeP.SubOct, 0);                               // ... at the root pitch
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
            // LFO 1 (a sine, restarting with each note from zero) moves A's frame position.
            dye.Set(DyeP.L1Shape, 0);
            dye.Set(DyeP.L1Sync, 0);
            dye.Set(DyeP.L1Retrig, 1);
            dye.Set(DyeP.L1Phase, 0);
            dye.Set(DyeP.L1Fade, 0);
            dye.Set(DyeP.R1Src, (int)RouteSource.Lfo1);
            dye.Set(DyeP.R1Dst, (int)RouteDest.APosition);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            dye.Set(DyeP.APos, c[FramePos]);
            double warp = c[PhaseWarp];
            dye.Set(DyeP.AWarp1Amt, Math.Max(0, -warp));          // left of centre: Bend, 0 at the centre
            dye.Set(DyeP.AWarp2Amt, Math.Max(0, warp));           // right of centre: Sync
            dye.Set(DyeP.ATable, c[TableSelect]);
            dye.Set(DyeP.L1Rate, c[MotionRate]);
            dye.Set(DyeP.R1Amt, MotionTravel * c[MotionDepth]);
            dye.Set(DyeP.SubLevel, c[SubAnchor]);
            Globals(c, dye, 5, SlotA);
        }
    }
}
