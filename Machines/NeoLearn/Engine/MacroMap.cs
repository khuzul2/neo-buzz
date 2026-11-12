#nullable disable
// Neo Learn (docs/specs/neo-learn.md §4): a machine's macro map. A pure function from the machine's 12 control values
// (in their units: Hz, seconds, dB, fractions) to Dye Essence's controls (in theirs). One macro may drive several Dye
// controls, along curves. The host evaluates it when a control changes, and Dye ramps across the tick as it does for every
// control, so an edit, the agent or automation never steps. The base patch (what the machine fixes) is code, so a song
// reloads to the same bits.

using System;
using NeoBuzz.Dsp;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    /// <summary>Where a map writes: a Dye Essence control by its index, in its own units (a choice as its number).</summary>
    internal interface IDyeSink
    {
        void Set(int dyeParam, double value);

        /// <summary>One of the shared engine's additions for a slot (0 A, 1 B, 2 C) that Dye Essence's own controls do not reach (L1: the host writes them into the engine).</summary>
        void Ext(int slot, SlotExt what, double value);
    }

    /// <summary>The shared engine's Neo Learn additions, per slot (OscSettings: all off at 0).</summary>
    internal enum SlotExt { ExactRatio, Feedback, Comb, CombPeriod, Shimmer, Inharm }

    internal abstract class MacroMap
    {
        /// <summary>Control positions: core 1–6, then the six global controls (neo-learn-controls.md A1).</summary>
        public const int Core = 6;
        public const int EnvAttack = 6, EnvDecay = 7, EnvSustain = 8, EnvRelease = 9, UnisonSpread = 10, OutputGain = 11;
        public const int Count = 12;

        /// <summary>The machine's 12 controls, in order.</summary>
        public abstract ParamDef[] Controls { get; }

        /// <summary>The fixed base patch, written once before the first block (a map may also write these controls again).</summary>
        public abstract void Base(IDyeSink dye);

        /// <summary>Dye's controls from the 12 control values <paramref name="c"/> (units of <see cref="Controls"/>).</summary>
        public abstract void Map(double[] c, IDyeSink dye);

        /// <summary>Dye's three slots' engine-type controls (a static array: a map runs on the audio thread and allocates nothing).</summary>
        protected static readonly int[] AllSlots = { DyeP.AType, DyeP.BType, DyeP.CType };

        /// <summary>The slots' pitch-and-level controls the globals drive: the amp and filter envelopes from the one ADSR, and unison on the slots in use.</summary>
        protected static void Globals(double[] c, IDyeSink dye, int unisonVoices, int[] slotTypeParams)
        {
            dye.Set(DyeP.AmpA, c[EnvAttack]);
            dye.Set(DyeP.AmpD, c[EnvDecay]);
            dye.Set(DyeP.AmpS, c[EnvSustain]);
            dye.Set(DyeP.AmpR, c[EnvRelease]);
            // The filter envelope follows the same ADSR ("the main ADSR envelope sweeps the filter", A2.1).
            dye.Set(DyeP.FltA, c[EnvAttack]);
            dye.Set(DyeP.FltD, c[EnvDecay]);
            dye.Set(DyeP.FltS, c[EnvSustain]);
            dye.Set(DyeP.FltR, c[EnvRelease]);
            // Unison: off is one voice per note (a detent); above 0 it is 5 (3 for Additive, Granular and Spectral),
            // detuned up to ±50 cents and spread across the stereo field as the value grows.
            double u = Math.Clamp(c[UnisonSpread], 0, 1);
            foreach (int type in slotTypeParams)
            {
                int slot = (type - DyeP.AType) / (DyeP.BType - DyeP.AType);
                int b = DyeP.AType + slot * (DyeP.BType - DyeP.AType);
                dye.Set(b + (DyeP.AUnison - DyeP.AType), u > 0 ? unisonVoices : 1);
                dye.Set(b + (DyeP.ADetune - DyeP.AType), 50 * u);
                dye.Set(b + (DyeP.ASpread - DyeP.AType), u);
            }
        }
    }
}
