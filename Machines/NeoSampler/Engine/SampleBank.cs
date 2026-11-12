#nullable disable
// Neo Sampler (docs/specs/machines.md §5): the wave slots a zone's Wave names (split from
// SampleData.cs so that other machines can read SampleLayer without the built-in samples).

using System;
using System.Threading;

namespace NeoBuzz.Sampler
{
    /// <summary>A wave slot: its layers, by root (ascending), and its name.</summary>
    internal sealed class SampleSlot
    {
        public SampleSlot(string name, SampleLayer[] layers)
        {
            Name = name ?? "";
            var sorted = (SampleLayer[])(layers ?? Array.Empty<SampleLayer>()).Clone();
            Array.Sort(sorted, (a, b) => a.Root.CompareTo(b.Root));
            Layers = sorted;
        }

        public string Name { get; }
        public SampleLayer[] Layers { get; }
    }

    /// <summary>
    /// What a zone's Wave reaches: the song's 200 wavetable slots (set from outside: the machine
    /// copies the song's waves in, tests set their own) and the built-in samples above them.
    /// Slots are swapped whole, so a voice keeps the layer it started with.
    /// </summary>
    internal sealed class SampleBank
    {
        public const int SongSlots = 200;
        public const int MaxWave = 254;

        readonly SampleSlot[] slots = new SampleSlot[SongSlots];

        /// <summary>The slot a zone's Wave names (1 … 254), or null (off, empty, missing, a built-in still being made).</summary>
        public SampleSlot Get(int wave)
        {
            if (wave >= 1 && wave <= SongSlots) return Volatile.Read(ref slots[wave - 1]);
            if (wave > SongSlots && wave <= MaxWave) return BuiltIns.Get(wave - SongSlots);
            return null;
        }

        /// <summary>A song slot (1 … 200); null empties it.</summary>
        public void Set(int wave, SampleSlot slot)
        {
            if (wave >= 1 && wave <= SongSlots) Volatile.Write(ref slots[wave - 1], slot);
        }
    }
}
