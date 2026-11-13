#nullable disable
// Neo Learn String's macro map (docs/specs/neo-learn.md §9, L2a as built): Dye Essence's String slot (Karplus-Strong, Machines/DyeEssence/Engine/
// StringEngine.cs) on slot A. The six core controls go to the engine's String settings: the pick position, the loop's damping, the pick's hardness,
// the decay time (T60, seconds), the stiffness (dispersion) and the sympathetic strings. The globals are the one ADSR on the amp envelope (a string
// sustains by itself: the Sustain default is 100 %) and unison (5 detuned strings a note).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class StringMap : MacroMap
    {
        // The core controls' positions.
        public const int PickPosition = 0, StringDamping = 1, PickHardness = 2, DecayTime = 3, Dispersion = 4, Sympathetic = 5;

        /// <summary>The String slot's type (SlotType.String).</summary>
        public const int StringType = (int)SlotType.String;

        static readonly int[] SlotA = { DyeP.AType };

        readonly ParamDef[] controls;

        public StringMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        /// <summary>The String engine's lines are made for the voice rate at Standard quality (2x).</summary>
        public override int StringOversample => 2;

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 0);
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 6);                                // (Modal, until the extension makes it a String)
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
            dye.Ext(0, SlotExt.TypeExt, StringType);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            dye.Ext(0, SlotExt.StrPick, c[PickPosition]);
            dye.Ext(0, SlotExt.StrDamp, c[StringDamping]);
            dye.Ext(0, SlotExt.StrHard, c[PickHardness]);
            dye.Ext(0, SlotExt.StrDecay, c[DecayTime]);
            dye.Ext(0, SlotExt.StrStiff, c[Dispersion]);
            dye.Ext(0, SlotExt.StrSymp, c[Sympathetic]);
            Globals(c, dye, NoteBank.StringVoices, SlotA);
        }

        /// <summary>The fraction of the string's length from the bridge a pick position control puts the pluck (0.03 … 0.5), as the engine's comb has it.</summary>
        public static double PluckAt(double pick) => 0.03 + 0.47 * Math.Clamp(pick, 0, 1);

        /// <summary>
        /// The first <paramref name="count"/> partials' levels (0 … 1, the strongest 1) as the picture draws them: a pluck at a fraction of the length
        /// puts partial k at |sin(k pi p)| / k^(0.5 + 1.5 hardness-softness), the loop's damping takes the upper ones down, and stiffness
        /// sets them at k sqrt(1 + B k^2) (returned in <paramref name="ratio"/>).
        /// </summary>
        public static void Partials(double pick, double damping, double hardness, double stiffness, double[] level, double[] ratio, int count)
        {
            double p = PluckAt(pick), peak = 0, b = 0.0008 * stiffness * stiffness * 16;
            double la = 0.92 * PortableMath.Pow(Math.Clamp(damping, 0, 1), 1.5);
            double slope = 1.0 + 1.2 * (1 - Math.Clamp(hardness, 0, 1));
            for (int k = 1; k <= count; k++)
            {
                double w = Math.Abs(PortableMath.Sin(k * p * Math.PI)) / PortableMath.Pow(k, slope);
                // the loop's one-pole low-pass at this partial's angle (taken as 0.04 pi per partial) gain, a few trips round
                double th = 0.04 * Math.PI * k;
                double gain = (1 - la) / Math.Sqrt(1 - 2 * la * PortableMath.Cos(th) + la * la);
                w *= PortableMath.Pow(gain, 6);
                level[k - 1] = w;
                ratio[k - 1] = k * Math.Sqrt(1 + b * k * k);
                peak = Math.Max(peak, w);
            }
            if (peak > 0) for (int k = 0; k < count; k++) level[k] /= peak;
        }
    }
}
