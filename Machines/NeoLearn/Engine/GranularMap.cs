#nullable disable
// Neo Learn Granular's macro map (docs/specs/neo-learn.md §9, L1b as built): Dye Essence's Granular engine on slot A, playing one
// source (Dye's five built-in samples, or the user's WAV: the machine keeps which), the centre frozen where Scan Position puts it,
// plus the shared engine's additions: a continuous window morph from trapezoid to Gaussian (Grain Contour) and grains that sound an
// octave up (Pitch Shimmer), which take no extra grain, so Dye's cap of 10 a note holds.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class GranularMap : MacroMap
    {
        // The core controls' positions.
        public const int ScanPosition = 0, GrainSize = 1, Density = 2, TimeSpray = 3, PitchShimmer = 4, GrainContour = 5;

        /// <summary>Pitch Shimmer 100 % lets each grain wander this many cents either way (a fine start: the knob is linear), and makes this share of grains an octave up.</summary>
        public const double WanderCents = 25, OctaveShareAtFull = 0.5;

        static readonly int[] SlotA = { DyeP.AType };

        /// <summary>The source's choice (Dye's ASmpSrc: Choir, Strings, Bell, Pluck, Texture, User). The machine sets it from its state and asks the host to map again.</summary>
        public int Source { get; set; }

        readonly ParamDef[] controls;

        public GranularMap(ParamDef[] controls)
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
            dye.Set(DyeP.AType, 4);                                // Granular
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.AGrScan, 0);                              // the centre is frozen where Scan Position puts it
            dye.Set(DyeP.AGrRev, 0);
            dye.Set(DyeP.AGrWidth, 0.5);
            dye.Set(DyeP.AGrShape, 0);                             // (the morph replaces the window)
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            dye.Set(DyeP.ASmpSrc, Source);
            dye.Set(DyeP.APos, c[ScanPosition]);
            dye.Set(DyeP.AGrSize, c[GrainSize]);
            dye.Set(DyeP.AGrRate, c[Density]);
            dye.Set(DyeP.AGrSpray, c[TimeSpray]);
            double shimmer = c[PitchShimmer];
            dye.Set(DyeP.AGrJitter, WanderCents * shimmer);
            dye.Ext(0, SlotExt.GrainOctave, shimmer);              // the engine takes half of this as the share of octave-up grains
            dye.Ext(0, SlotExt.GrainMorph, c[GrainContour]);
            Globals(c, dye, 3, SlotA);
        }

        /// <summary>
        /// A grain's window at <paramref name="x"/> (0 … 1 across the grain) for a contour (0 trapezoid … 1 Gaussian), the engine's own table and blend
        /// (a picture draws what the grains use).
        /// </summary>
        public static double Window(double contour, double x)
        {
            int i = (int)Math.Round(Math.Clamp(x, 0, 1) * GrainWindows.Size);
            return GrainWindows.Morphed(Math.Clamp(contour, 0, 1), i);
        }
    }
}
