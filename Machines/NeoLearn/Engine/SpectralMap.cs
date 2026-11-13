#nullable disable
// Neo Learn Spectral's macro map (docs/specs/neo-learn.md §9, L3 as built): Dye Essence's Spectral engine on slot A (a sample analysed once into up to 64
// tracked partials and a noise residual, resynthesised: Dye Essence P5a, docs/specs/dye-essence.md §4.12), over one of Dye's five built-in samples or the
// user's WAV (the machine keeps which, and its analysis). Six core controls: Stretch (the partials' spacing), Time Smear (blur), Freeze (the reading
// slows to a stop at a moment of the sample), Source, Partial Gate (how many of the loudest partials sound) and Tone / Noise (the analysed halves' balance).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class SpectralMap : MacroMap
    {
        // The core controls' positions.
        public const int Stretch = 0, Smear = 1, Freeze = 2, SourceControl = 3, Gate = 4, ToneNoise = 5;

        /// <summary>Freeze 100 % holds the sample at this share of its length (past the attack, in the sustained part of most sounds); at 0 % it plays from the start.</summary>
        public const double FreezeAt = 0.4;
        /// <summary>The partials the Gate leaves at 0 % and 100 % (the loudest first).</summary>
        public const int MaxPartials = 64, MinPartials = 1;

        static readonly int[] SlotA = { DyeP.AType };

        /// <summary>Whether the user's own sample is loaded (Source's User choice plays the first built-in without it). The machine sets it from its state and asks the host to map again.</summary>
        public bool UserAvailable { get; set; }

        readonly ParamDef[] controls;

        public SpectralMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 1);
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, (int)SlotType.Spectral);
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.ASmpRoot, 60);
            dye.Set(DyeP.ASmpLoop, 1);                              // forward over the whole sample: a held note goes round
            dye.Set(DyeP.ASmpLoopStart, 0);
            dye.Set(DyeP.ASmpLoopEnd, 1);
            dye.Set(DyeP.ASmpFade, 40);
            dye.Set(DyeP.ASpKey, 1);
            dye.Set(DyeP.ASpFormant, 0);
            dye.Set(DyeP.ASpSnap, 0);
            dye.Set(DyeP.ASpTilt, 0);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            double freeze = Math.Clamp(c[Freeze], 0, 1);
            int source = (int)Math.Clamp(Math.Round(c[SourceControl]), 0, 5);
            if (source == 5 && !UserAvailable) source = 0;
            dye.Set(DyeP.ASmpSrc, source);
            dye.Set(DyeP.ASpStretch, Math.Clamp(c[Stretch], -1, 1) * 100);
            dye.Set(DyeP.ASpBlur, Math.Clamp(c[Smear], 0, 1));
            dye.Set(DyeP.ASpSpeed, 100 * (1 - freeze));
            dye.Set(DyeP.APos, FreezeAt * freeze);
            dye.Set(DyeP.ASpPartials, Partials(c[Gate]));
            dye.Set(DyeP.ASpBalance, Math.Clamp(c[ToneNoise], 0, 1));
            Globals(c, dye, 3, SlotA);
        }

        /// <summary>The partials that sound at a Gate value (0 … 1): 64 at 0 %, 1 at 100 %, in between linearly in the count.</summary>
        public static int Partials(double gate) => (int)Math.Round(MaxPartials - (MaxPartials - MinPartials) * Math.Clamp(gate, 0, 1));

        /// <summary>Where the reading starts at a Freeze value (a share of the sample), and the speed it reads at (a share of the sample's own).</summary>
        public static void Reading(double freeze, out double position, out double speed)
        {
            freeze = Math.Clamp(freeze, 0, 1);
            position = FreezeAt * freeze;
            speed = 1 - freeze;
        }
    }
}
