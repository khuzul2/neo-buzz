#nullable disable
// Neo Learn Sampler's macro map (docs/specs/neo-learn.md §9, L2b as built): Dye Essence's Sample engine on slot A, playing one source (Dye's five built-in
// samples, or the user's WAV: the machine keeps which), from a start offset, optionally looped (a crossfaded region after the start), then the shared engine's
// tone tilt, grit (12 bits, a lower rate, wow) and small room, which run after the voices (LearnFx.cs).

using System;
using NeoBuzz.Dsp;
using NeoBuzz.DyeEssence;
using DyeP = NeoBuzz.DyeEssence.P;

namespace NeoBuzz.Learn
{
    internal sealed class SamplerMap : MacroMap
    {
        // The core controls' positions.
        public const int StartOffset = 0, Tone = 1, SourceControl = 2, LoopAmount = 3, Grit = 4, Room = 5;

        /// <summary>Start Offset 100 % starts this far into the sample (not the very end: a note would be silent).</summary>
        public const double MaxStart = 0.8;
        /// <summary>The shortest loop (a share of the sample) and the crossfade at its seam, in milliseconds.</summary>
        public const double MinLoop = 0.02, SeamMs = 40;

        static readonly int[] SlotA = { DyeP.AType };

        /// <summary>Whether the user's own sample is loaded (whether the user's sample is loaded: Source's User choice plays the first built-in without it). The machine sets it from its state and asks the host to map again.</summary>
        public bool UserAvailable { get; set; }

        readonly ParamDef[] controls;

        public SamplerMap(ParamDef[] controls)
        {
            this.controls = controls;
        }

        public override ParamDef[] Controls => controls;

        public override int FxFlags => FxTilt | FxGrit | FxRoom;

        public override void Post(double[] c, LearnFxSet fx)
        {
            if (fx == null) return;
            if (fx.Tilt != null) fx.Tilt.Tone = (float)Math.Clamp(c[Tone], -1, 1);
            if (fx.Grit != null) fx.Grit.Amount = (float)Math.Clamp(c[Grit], 0, 1);
            if (fx.Room != null) fx.Room.Amount = (float)Math.Clamp(c[Room], 0, 1);
        }

        public override void Base(IDyeSink dye)
        {
            dye.Set(DyeP.VoiceMode, 0);
            dye.Set(DyeP.Polyphony, 16);
            dye.Set(DyeP.GlideMode, 0);
            dye.Set(DyeP.Quality, 1);
            dye.Set(DyeP.Volume, 0);
            dye.Set(DyeP.AType, 3);                                // Sample
            dye.Set(DyeP.BType, 0);
            dye.Set(DyeP.CType, 0);
            dye.Set(DyeP.AOct, 0);
            dye.Set(DyeP.ASemi, 0);
            dye.Set(DyeP.AFine, 0);
            dye.Set(DyeP.APan, 0);
            dye.Set(DyeP.ALevel, 1);
            dye.Set(DyeP.ASmpRoot, 60);
            dye.Set(DyeP.AToF2, 0);
            dye.Set(DyeP.SubLevel, 0);
            dye.Set(DyeP.NoiseLevel, 0);
            dye.Set(DyeP.F1Type, 0);
            dye.Set(DyeP.F2Type, 0);
        }

        public override void Map(double[] c, IDyeSink dye)
        {
            double start = Math.Clamp(c[StartOffset], 0, 1) * MaxStart, loop = Math.Clamp(c[LoopAmount], 0, 1);
            int source = (int)Math.Clamp(Math.Round(c[SourceControl]), 0, 5);
            if (source == 5 && !UserAvailable) source = 0;
            dye.Set(DyeP.ASmpSrc, source);
            dye.Set(DyeP.APos, start);
            dye.Set(DyeP.ASmpLoop, loop > 0 ? 1 : 0);
            dye.Set(DyeP.ASmpLoopStart, start);
            dye.Set(DyeP.ASmpLoopEnd, Math.Min(1, start + MinLoop + (1 - MinLoop) * loop));
            dye.Set(DyeP.ASmpFade, SeamMs);
            Globals(c, dye, 5, SlotA);
        }

        /// <summary>The loop region (start, end) as shares of the sample for a start offset and loop amount (0: no loop, both 0 … a picture marks what the engine plays).</summary>
        public static void Region(double offset, double loop, out double start, out double end)
        {
            start = Math.Clamp(offset, 0, 1) * MaxStart;
            end = loop > 0 ? Math.Min(1, start + MinLoop + (1 - MinLoop) * Math.Clamp(loop, 0, 1)) : 1;
        }
    }
}
