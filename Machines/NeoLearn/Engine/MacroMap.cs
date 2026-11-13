#nullable disable
// Neo Learn (docs/specs/neo-learn.md §4): a machine's macro map. A pure function from the machine's 12 control values
// (in their units: Hz, seconds, dB, fractions) to Dye Essence's controls (in theirs). One macro may drive several Dye
// controls, along curves. The host evaluates it when a control changes, and Dye ramps across the tick as it does for every
// control, so an edit, the agent or automation never steps. The base patch (what the machine fixes) is code, so a song
// reloads to the same bits.

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
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
    internal enum SlotExt
    {
        ExactRatio, Feedback, Comb, CombPeriod, Shimmer, Inharm, GrainMorph, GrainOctave, ExciterMorph, GeomBlend, GeomTo,
        // L2a: a slot's type (SlotType.String), the String engine, the wavefolder.
        TypeExt, StrPick, StrDamp, StrHard, StrDecay, StrStiff, StrSymp, FoldStages, FoldDrive, FoldSym,
        // L2a: a filter's kind (slots 3 and 4 are filters 1 and 2): 1 vowel, 2 low-pass gate; the vowel filter's and the gate's controls.
        FKind, FMorph, FTract, FQ, FEnvMod, FMode, FStrike,
        // L2b: the phase distortion's shape (-1: off; 0 … 5 puts Warp.Pd in warp 1) and a level the host moves between ticks (1: as Dye's controls say; also for the sub and the noise).
        PdShape, LevelExt,
    }

    /// <summary>The slots <see cref="IDyeSink.Ext"/> addresses: the three oscillators, the two filters, then the sub and the noise (their <see cref="SlotExt.LevelExt"/> only).</summary>
    internal static class ExtSlot
    {
        public const int Filter1 = 3, Filter2 = 4, Sub = 5, Noise = 6, Count = 7;
    }

    internal abstract class MacroMap
    {
        /// <summary>Control positions: core 1–6, then the six global controls (neo-learn-controls.md A1).</summary>
        public const int Core = 6;
        public const int EnvAttack = 6, EnvDecay = 7, EnvSustain = 8, EnvRelease = 9, UnisonSpread = 10, OutputGain = 11;
        public const int Count = 12;

        /// <summary>The machine's 12 controls, in order.</summary>
        public abstract ParamDef[] Controls { get; }

        /// <summary>A map that plays String slots needs their delay lines made (off the audio thread): the voice rate's oversampling, else 0.</summary>
        public virtual int StringOversample => 0;

        /// <summary>The effects after the engine this machine's map asks for (L2b): a sum of <see cref="FxChorus"/>, <see cref="FxGrit"/>, <see cref="FxRoom"/>, <see cref="FxTilt"/>.</summary>
        public virtual int FxFlags => 0;
        public const int FxChorus = 1, FxGrit = 2, FxRoom = 4, FxTilt = 8;

        /// <summary>The effects' amounts from the 12 control values (called with <see cref="Map"/>, on the audio thread; the set exists when <see cref="FxFlags"/> is not 0).</summary>
        public virtual void Post(double[] c, LearnFxSet fx) { }

        /// <summary>
        /// A map that moves something continuously (the Vector machine's orbit) says how often, in samples: the host then plays the engine in pieces of at most this many
        /// samples and calls <see cref="Tick"/> before each, with the piece's length. 0: one call for the block.
        /// </summary>
        public virtual int TickSamples => 0;

        /// <summary>The host's sample rate (a map that moves something in time needs it); called whenever it changes.</summary>
        public virtual void SetSampleRate(float rate) { }

        /// <summary>Moves what the map moves by <paramref name="samples"/> (audio thread, allocation-free); writes through <paramref name="dye"/> as <see cref="Map"/> does.</summary>
        public virtual void Tick(double[] c, int samples, IDyeSink dye) { }

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
