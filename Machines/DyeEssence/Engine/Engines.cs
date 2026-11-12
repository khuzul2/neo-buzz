#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.4): the engines behind the slots. Every engine so far
// renders through the bank's lanes (the unison kernels): it says which band-limited table a slot
// reads, where in it, and how far each voice sits from the note. The Wavetable engine plays the
// slot's table at its position; the Analog engine plays the classic waves (sine, triangle, saw,
// square from the Basic table's exact frames, pulse widths from the PWM table) with each voice a
// little out of tune, as analog oscillators are. Engines that need kernels of their own (samples,
// grains, partials) add them behind this interface: the Sample and Granular engines (StreamEngines.cs)
// read a sample and render beside the lanes.

namespace NeoBuzz.DyeEssence
{
    /// <summary>What an engine slot holds (append only: the engines to come take the next values).</summary>
    internal enum SlotType { Off, Wavetable, Analog, Sample, Granular }

    /// <summary>The Analog engine's waves (append only).</summary>
    internal enum AnalogShape { Sine, Triangle, Saw, Square, Pulse }

    internal interface IEngine
    {
        /// <summary>The table the slot reads now; null: nothing to play (yet).</summary>
        TableArena TableOf(OscSettings os);

        /// <summary>Where in that table, 0 … 1 (the bank glides it across ticks).</summary>
        float PositionOf(OscSettings os);

        /// <summary>A voice's own fixed detune in cents, from its seeded random value <paramref name="r"/> in [−1, 1].</summary>
        float DriftCents(OscSettings os, float r);
    }

    internal sealed class WavetableEngine : IEngine
    {
        public TableArena TableOf(OscSettings os) => os.Table;
        public float PositionOf(OscSettings os) => os.Position;
        public float DriftCents(OscSettings os, float r) => 0f;
    }

    internal sealed class AnalogEngine : IEngine
    {
        /// <summary>The widest drift, in cents either side, at 100 %.</summary>
        public const float MaxDrift = 12f;

        /// <summary>The built-in Basic table (sine, triangle, saw, square at frames 0, 21, 42, 63) and PWM table (50 % → 3 %).</summary>
        public TableArena Basic, Pulse;

        public TableArena TableOf(OscSettings os) => os.Shape == AnalogShape.Pulse ? Pulse : Basic;

        public float PositionOf(OscSettings os) => os.Shape switch
        {
            AnalogShape.Sine => 0f,
            AnalogShape.Triangle => 21f / 63f,
            AnalogShape.Saw => 42f / 63f,
            AnalogShape.Square => 1f,
            // The PWM table's frames run from 50 % to 3 % evenly.
            _ => System.Math.Clamp((0.5f - os.PulseWidth) / 0.47f, 0f, 1f),
        };

        public float DriftCents(OscSettings os, float r) => MaxDrift * System.Math.Clamp(os.Drift, 0f, 1f) * r;
    }
}
