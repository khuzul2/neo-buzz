#nullable disable
// Neo Learn Formant's macro map (docs/specs/neo-learn.md §9, L2a as built): a glottal pulse (slot A, Analog pulse) and breath (Dye's noise) through the
// shared engine's vowel filter (Machines/DyeEssence/Engine/VowelLpg.cs) on filter 1. The six core controls: the vowel (the four formants' morph
// a-e-i-o-u), the tract size, the formants' Q, the glottal edge (pulse width, a sub-glottal sub oscillator and drive into the filter), the breath and
// the Talking Env, which sweeps the vowel by the filter envelope on every key (a quick syllable of its own: the filter envelope is not the ADSR here).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class FormantMap : MacroMap
    {
        // The core controls' positions.
        public const int VowelMorph = 0, TractScale = 1, FormantQ = 2, GlottalEdge = 3, BreathAir = 4, TalkingEnv = 5;

        /// <summary>The glottal pulse's width (% of the cycle) at Glottal 0 % (a round, soft pulse) and 100 % (a narrow, buzzing one).</summary>
        public const double PulseWidthSoft = 42, PulseWidthEdge = 6;

        /// <summary>At full Glottal: the sub oscillator's level and the drive into the formant bank (dB).</summary>
        public const double SubAtEdge = 0.55, DriveAtEdge = 14;

        /// <summary>The breath noise's level at Breath 100 % (it goes through the vowel filter with the pulse).</summary>
        public const double BreathLevel = 0.7;

        /// <summary>The talking syllable's filter-envelope times: instant rise, this fall, nothing held.</summary>
        public const double SyllableFall = 0.16;

        static readonly int[] SlotA = { DyeP.AType };

        readonly ParamDef[] controls;

        public FormantMap(ParamDef[] controls)
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
            dye.Set(DyeP.AType, 2);                                // Analog: the glottal pulse
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AShape, 4);                               // Pulse
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.APhase, 0);
            dye.Set(DyeP.ARandom, 1);
            dye.Set(DyeP.AWarp1, 0);
            dye.Set(DyeP.AWarp2, 0);
            dye.Set(DyeP.ADrift, 0.12);                            // a voice is never quite steady
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubShape, 2);                             // a saw an octave down: the "bass" in Glottal / Bass
            dye.Set(DyeP.SubOct, 1);
            dye.Set(DyeP.SubToF2, 0);
            dye.Set(DyeP.NoiseColor, 0.6);
            dye.Set(DyeP.NoiseToF2, 0);
            dye.Set(DyeP.F1Type, 0);                               // (the vowel filter, below)
            dye.Set(DyeP.F2Type, 0);
            dye.Ext(ExtSlot.Filter1, SlotExt.FKind, 1);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            double edge = Math.Clamp(c[GlottalEdge], 0, 1);
            dye.Set(DyeP.APw, PulseWidthSoft + (PulseWidthEdge - PulseWidthSoft) * edge);
            dye.Set(DyeP.SubLevel, SubAtEdge * edge);
            dye.Set(DyeP.NoiseLevel, BreathLevel * c[BreathAir]);
            dye.Set(DyeP.F1Drive, DriveAtEdge * edge);
            dye.Ext(ExtSlot.Filter1, SlotExt.FMorph, c[VowelMorph]);
            dye.Ext(ExtSlot.Filter1, SlotExt.FTract, c[TractScale]);
            dye.Ext(ExtSlot.Filter1, SlotExt.FQ, c[FormantQ]);
            dye.Ext(ExtSlot.Filter1, SlotExt.FEnvMod, c[TalkingEnv]);
            Globals(c, dye, 5, SlotA);
            // The filter envelope is the syllable: an instant rise and a fall, whatever the ADSR (which shapes the loudness).
            dye.Set(DyeP.FltA, 0.0005);
            dye.Set(DyeP.FltD, SyllableFall);
            dye.Set(DyeP.FltS, 0);
            dye.Set(DyeP.FltR, SyllableFall);
        }

        /// <summary>The four formants for the controls (centres Hz, bandwidths Hz, levels) as the filter makes them, with the envelope's sweep at <paramref name="env"/> (0 … 1).</summary>
        public static void Formants(double morph, double tract, double q, double envMod, double env, double sampleRate, double[] hz, double[] bw, double[] amp) =>
            VowelTable.At(Math.Clamp(morph + envMod * env, 0, 1), tract, q, 0.45 * sampleRate * 2, hz, bw, amp);
    }
}
