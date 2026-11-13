#nullable disable
// Neo Learn Subtractive's macro map (docs/specs/neo-learn.md §9, L0 as built): Dye Essence's Analog engine on slots A, B and
// C (the wave morph), the Sub, filter 1 as the 24 dB/oct ladder low-pass with drive, per-voice drift and the filter envelope.

using System;
using NeoBuzz.Dsp;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class SubtractiveMap : MacroMap
    {
        // The core controls' positions.
        public const int Cutoff = 0, Resonance = 1, WaveShape = 2, SubOsc = 3, EnvToFilter = 4, Drive = 5;

        /// <summary>Slot A plays the sine, then (past the middle of the morph) the pulse; B the triangle; C the saw.</summary>
        public const int SinePulseSlot = 0, TriangleSlot = 1, SawSlot = 2;
        // Dye's Analog shapes.
        const int Sine = 0, Triangle = 1, Saw = 2, Pulse = 4, Square = 3;
        /// <summary>The pulse's width: narrow enough to sound hollow and nasal next to the sub's square.</summary>
        public const double PulseWidth = 0.25;
        /// <summary>The octaves ±100 % of Env to Filter moves the cutoff by.</summary>
        public const double EnvOctaves = 6;
        /// <summary>The ladder's drive in dB at 100 % Drive.</summary>
        public const double DriveDb = 18;
        /// <summary>The per-voice drift (a fraction of Dye's ±12 cents) at 100 % Drive.</summary>
        public const double DriftAtFullDrive = 0.6;

        readonly ParamDef[] controls;

        public SubtractiveMap(ParamDef[] controls)
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
                dye.Set(b, 2);                                    // Analog
                dye.Set(b + (DyeP.AOct - DyeP.AType), 0);
                dye.Set(b + (DyeP.ASemi - DyeP.AType), 0);
                dye.Set(b + (DyeP.AFine - DyeP.AType), 0);
                dye.Set(b + (DyeP.APan - DyeP.AType), 0);
                dye.Set(b + (DyeP.APhase - DyeP.AType), 0);
                dye.Set(b + (DyeP.ARandom - DyeP.AType), 1);   // seeded random start phases: unison adds in power, so enabling it does not jump in level
                dye.Set(b + (DyeP.AWarp1 - DyeP.AType), 0);
                dye.Set(b + (DyeP.AWarp2 - DyeP.AType), 0);
                dye.Set(b + (DyeP.AToF2 - DyeP.AType), 0);
            }
            dye.Set(DyeP.AShape, Sine);
            dye.Set(DyeP.BShape, Triangle);
            dye.Set(DyeP.CShape, Saw);
            dye.Set(DyeP.APw, PulseWidth * 100);
            dye.Set(DyeP.SubShape, Square);
            dye.Set(DyeP.SubOct, 1);                              // one octave below the note
            dye.Set(DyeP.SubToF2, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 1);                              // Ladder 24
            dye.Set(DyeP.F1Key, 0);                               // the cutoff is the cutoff, whatever the key
            dye.Set(DyeP.F1Vel, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        /// <summary>The weights of the four waves (sine, triangle, saw, pulse) at a morph position 0 … 1: hats over thirds, so two neighbours at most sound and the weights sum to 1.</summary>
        public static void Weights(double morph, out double sine, out double triangle, out double saw, out double pulse)
        {
            double x = Math.Clamp(morph, 0, 1) * 3;
            sine = Math.Max(0, 1 - x);
            triangle = Math.Max(0, 1 - Math.Abs(x - 1));
            saw = Math.Max(0, 1 - Math.Abs(x - 2));
            pulse = Math.Max(0, x - 2);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            Weights(c[WaveShape], out double w0, out double w1, out double w2, out double w3);
            // Slot A holds the sine until the middle of the morph and the pulse after it: it is silent in between (the sine's
            // weight is 0 from a third and the pulse's until two thirds), so the change of shape is never heard.
            bool pulse = c[WaveShape] >= 0.5;
            dye.Set(DyeP.AShape, pulse ? Pulse : Sine);
            dye.Set(DyeP.ALevel, w0 + w3);
            dye.Set(DyeP.BLevel, w1);
            dye.Set(DyeP.CLevel, w2);

            dye.Set(DyeP.SubLevel, c[SubOsc]);

            dye.Set(DyeP.F1Cutoff, c[Cutoff]);
            dye.Set(DyeP.F1Reso, c[Resonance]);
            dye.Set(DyeP.F1Drive, DriveDb * c[Drive]);
            dye.Set(DyeP.F1Env, EnvOctaves * c[EnvToFilter]);
            double drift = DriftAtFullDrive * c[Drive];
            dye.Set(DyeP.ADrift, drift);
            dye.Set(DyeP.BDrift, drift);
            dye.Set(DyeP.CDrift, drift);

            Globals(c, dye, 5, AllSlots);
        }
    }
}
