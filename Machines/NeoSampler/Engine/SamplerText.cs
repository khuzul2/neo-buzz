#nullable disable
// Neo Sampler (docs/specs/machines.md §5): value texts. Keys and roots read as note names (MIDI 60
// is C4, as the agent server writes pitches), a zone's Wave as the wave's name ("Off", a slot's name
// from the song's wavetable, "Built-in Kick"), choke groups as "Off" or their number. The agent
// server takes these texts as values too (docs/specs/mcp-server.md §3.3).

using System;
using System.Globalization;
using NeoBuzz.Dsp;

namespace NeoBuzz.Sampler
{
    internal static class SamplerText
    {
        static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>MIDI 60 → "C4".</summary>
        public static string Note(int midi) => Sharps[((midi % 12) + 12) % 12] + ((int)Math.Floor(midi / 12.0) - 1).ToString(CultureInfo.InvariantCulture);

        /// <summary>The song's wave names by slot (1 … 200); null or "" when the slot is empty. Set by the machine.</summary>
        public static Func<int, string> SongWaveName { get; set; }

        /// <summary>A zone's Wave as text.</summary>
        public static string Wave(int raw)
        {
            if (raw <= 0) return "Off";
            if (raw > SampleBank.SongSlots)
            {
                int n = raw - SampleBank.SongSlots;
                return n <= BuiltIns.Count ? "Built-in " + BuiltIns.Names[n - 1] : $"Built-in {n} (none)";
            }
            string name = null;
            try { name = SongWaveName?.Invoke(raw); }
            catch (Exception) { }
            return string.IsNullOrWhiteSpace(name) ? $"Slot {raw}" : name.Trim();
        }

        static bool installed;

        /// <summary>Sets the table's texts for keys, roots, waves and choke groups (once).</summary>
        public static void Install(ParamDef[] table)
        {
            if (installed) return;
            installed = true;
            table[P.KitBase].Describer = Note;
            for (int z = 0; z < SamplerEngine.ZoneCount; z++)
            {
                table[SamplerEngine.ZoneParam(z, SamplerEngine.FWave)].Describer = Wave;
                table[SamplerEngine.ZoneParam(z, SamplerEngine.FKeyLo)].Describer = Note;
                table[SamplerEngine.ZoneParam(z, SamplerEngine.FKeyHi)].Describer = Note;
                table[SamplerEngine.ZoneParam(z, SamplerEngine.FRoot)].Describer = raw => raw >= SamplerEngine.RootFromWave ? "Wave" : Note(raw);
                table[SamplerEngine.ZoneParam(z, SamplerEngine.FChoke)].Describer = raw => raw <= 0 ? "Off" : raw.ToString(CultureInfo.InvariantCulture);
            }
        }
    }
}
