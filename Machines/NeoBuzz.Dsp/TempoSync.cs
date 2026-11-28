#nullable disable
// Neo machines shared DSP (docs/sdk/tempo-sync.md, SYN1): one tempo-sync vocabulary for every musical timing. A timing is Free (ms or Hz,
// the machine's own parameter) or Sync: a division from 8 bars to 1/64 with a Feel of Straight, Dotted (x 3/2), Triplet (x 2/3) or
// Quintuplet (x 4/5). This file turns tempo, division and feel into beats, seconds, Hz and samples, and reads the names a machine's
// Sync list stores (including the inline "1/16T", "1/8D", "1/2 dotted" of older lists), so every machine, the editor control and the
// agent speak the same words. Basic IEEE operations only (no libm), in a fixed order: the same values on every platform.
//
// Stored lists never change order (songs, presets and MIDI maps store a position); new divisions are appended. DisplayOrder gives the
// order a picker shows: Free, then 8 bars down to 1/64, then anything longer a machine's old list still has (16 bars), never the
// inline entries (those show as their division plus a Feel).

using System;
using System.Collections.Generic;

namespace NeoBuzz.Dsp
{
    /// <summary>A timing's feel: how a division is stretched. The raw values are what a machine's Feel parameter stores.</summary>
    internal enum TempoFeel { Straight = 0, Dotted = 1, Triplet = 2, Quintuplet = 3 }

    /// <summary>One entry of a machine's Sync (or Rate) list, as read from its name.</summary>
    internal readonly struct SyncEntry
    {
        public SyncEntry(int index, bool free, double beats, int inlineFeel, string name)
        {
            Index = index; Free = free; Beats = beats; InlineFeel = inlineFeel; Name = name;
        }

        /// <summary>The entry's position in the stored list.</summary>
        public int Index { get; }
        /// <summary>The entry is "Free" (the machine's own Hz or ms parameter is used).</summary>
        public bool Free { get; }
        /// <summary>The length in beats (a quarter note is one) with an inline feel already applied (1/16T: 1/6).</summary>
        public double Beats { get; }
        /// <summary>0 for a plain division; 1 or 2 for an older inline "D" or "T" entry.</summary>
        public int InlineFeel { get; }
        public string Name { get; }
        /// <summary>The division's beats with the inline feel taken off (1/16T: 1/4). Free: 0.</summary>
        public double DivisionBeats => InlineFeel == 1 ? Beats / 1.5 : InlineFeel == 2 ? Beats / (2.0 / 3.0) : Beats;
    }

    internal static class TempoSync
    {
        /// <summary>The divisions in musical order, longest first: 8 bars, 4 bars, 2 bars, 1 bar, 1/2, 1/4, 1/8, 1/16, 1/32, 1/64 (in beats, four to a bar).</summary>
        public static readonly double[] DivisionBeats = { 32, 16, 8, 4, 2, 1, 0.5, 0.25, 0.125, 0.0625 };

        /// <summary>The picker's words for <see cref="DivisionBeats"/>.</summary>
        public static readonly string[] DivisionLabels = { "8 bars", "4 bars", "2 bars", "1 bar", "1/2", "1/4", "1/8", "1/16", "1/32", "1/64" };

        /// <summary>Straight, Dotted, Triplet, Quintuplet: the words of a Feel parameter, in stored order.</summary>
        public static readonly string[] FeelNames = { "Straight", "Dotted", "Triplet", "Quintuplet" };

        /// <summary>What a Feel multiplies a division's length by: 1, 3/2, 2/3, 4/5.</summary>
        public static readonly double[] FeelFactors = { 1.0, 1.5, 2.0 / 3.0, 4.0 / 5.0 };

        /// <summary>The factor of a Feel choice (out of range: straight).</summary>
        public static double Factor(int feel) => feel > 0 && feel < FeelFactors.Length ? FeelFactors[feel] : 1.0;

        /// <summary>A length in beats with a Feel applied. Straight returns it untouched, so a straight timing is bit-identical to one with no feel.</summary>
        public static double Beats(double beats, int feel) => feel > 0 && feel < FeelFactors.Length ? beats * FeelFactors[feel] : beats;

        /// <summary>Seconds of <paramref name="beats"/> at <paramref name="bpm"/> (beats x 60 / bpm; a tempo below 1 reads as 1).</summary>
        public static double Seconds(double beats, double bpm) => beats * 60.0 / Math.Max(1, bpm);

        /// <summary>Cycles per second of a cycle of <paramref name="beats"/> at <paramref name="bpm"/> (bpm / 60 / beats).</summary>
        public static double Hz(double beats, double bpm) => Math.Max(1, bpm) / 60.0 / beats;

        /// <summary>Samples of <paramref name="beats"/> at a tempo and sample rate (not rounded).</summary>
        public static double Samples(double beats, double bpm, double sampleRate) => Seconds(beats, bpm) * sampleRate;

        /// <summary>The same, rounded to the nearest whole sample (at least 1).</summary>
        public static long SamplesRounded(double beats, double bpm, double sampleRate) => Math.Max(1L, (long)Math.Round(Samples(beats, bpm, sampleRate)));

        // ---- names ----------------------------------------------------------------------------------------------------

        /// <summary>
        /// Reads a stored name: "1/8", "1 bar", "1_bar", "2 bars", "1/1" (a bar), "1/16T", "1/8D", "1/2 dotted", "1/4 triplet", "Free".
        /// <paramref name="beats"/> has the inline feel applied; <paramref name="inlineFeel"/> is 0 (none), 1 (dotted) or 2 (triplet).
        /// </summary>
        public static bool TryParse(string name, out double beats, out int inlineFeel, out bool free)
        {
            beats = 0; inlineFeel = 0; free = false;
            if (string.IsNullOrWhiteSpace(name)) return false;
            string s = name.Trim().ToLowerInvariant().Replace('_', ' ');
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            if (s == "free") { free = true; return true; }
            if (s.EndsWith(" dotted")) { inlineFeel = 1; s = s.Substring(0, s.Length - 7); }
            else if (s.EndsWith(" triplet")) { inlineFeel = 2; s = s.Substring(0, s.Length - 8); }
            else if (s.Length > 2 && s[s.Length - 1] == 'd' && s.Contains('/')) { inlineFeel = 1; s = s.Substring(0, s.Length - 1); }
            else if (s.Length > 2 && s[s.Length - 1] == 't' && s.Contains('/')) { inlineFeel = 2; s = s.Substring(0, s.Length - 1); }
            double b;
            int slash = s.IndexOf('/');
            if (slash > 0)
            {
                if (!int.TryParse(s.Substring(0, slash), out int num) || !int.TryParse(s.Substring(slash + 1), out int den) || num != 1 || den < 1) return false;
                b = 4.0 / den;
            }
            else
            {
                int sp = s.IndexOf(' ');
                if (sp <= 0 || !int.TryParse(s.Substring(0, sp), out int bars) || bars < 1) return false;
                string unit = s.Substring(sp + 1);
                if (unit == "bar" || unit == "bars") b = 4.0 * bars;
                else if (unit == "beat" || unit == "beats") b = bars;
                else return false;
            }
            beats = inlineFeel == 1 ? b * 1.5 : inlineFeel == 2 ? b * (2.0 / 3.0) : b;
            return true;
        }

        /// <summary>The entries of a Sync or Rate list, read from its names (an unreadable name is a Free-less entry of 0 beats).</summary>
        public static SyncEntry[] Entries(string[] names)
        {
            var r = new SyncEntry[names.Length];
            for (int i = 0; i < names.Length; i++)
            {
                if (!TryParse(names[i], out double b, out int f, out bool free)) { b = 0; f = 0; free = false; }
                r[i] = new SyncEntry(i, free, b, f, names[i]);
            }
            return r;
        }

        /// <summary>The beats of each entry of a list, 0 for Free: what an engine indexes by the stored choice.</summary>
        public static double[] BeatsTable(string[] names)
        {
            var e = Entries(names);
            var r = new double[e.Length];
            for (int i = 0; i < r.Length; i++) r[i] = e[i].Free ? 0 : e[i].Beats;
            return r;
        }

        /// <summary>Longer than this (8 bars) is a legacy entry (16 bars): shown last.</summary>
        public const double LongestDivisionBeats = 32;

        /// <summary>
        /// The entries a picker offers, in musical order: Free first, then 8 bars down to 1/64, then legacy longer ones. Inline entries
        /// (1/16T) are left out: they are the plain division with a Feel. A division missing from a list is simply not offered.
        /// </summary>
        public static List<SyncEntry> DisplayOrder(string[] names)
        {
            var all = Entries(names);
            var r = new List<SyncEntry>();
            foreach (var e in all) if (e.Free) r.Add(e);
            var plain = new List<SyncEntry>();
            foreach (var e in all) if (!e.Free && e.InlineFeel == 0 && e.Beats > 0) plain.Add(e);
            plain.Sort((a, b) =>
            {
                bool la = a.Beats > LongestDivisionBeats, lb = b.Beats > LongestDivisionBeats;
                if (la != lb) return la ? 1 : -1;
                return b.Beats.CompareTo(a.Beats);
            });
            r.AddRange(plain);
            return r;
        }

        /// <summary>
        /// What a stored (entry, feel) pair means as a picker shows it: the plain entry and the Feel. An inline entry with a Straight Feel
        /// reads as its plain division and the inline Feel; with another Feel the stored pair is kept as it is (the engine multiplies both).
        /// Returns the list index of the plain division, or -1 when the list has none (it stays as stored).
        /// </summary>
        public static int PlainIndex(string[] names, int entry, int feel, out int shownFeel)
        {
            var all = Entries(names);
            shownFeel = feel;
            if (entry < 0 || entry >= all.Length) return entry;
            var e = all[entry];
            if (e.Free || e.InlineFeel == 0) return entry;
            if (feel != 0) return entry;
            for (int i = 0; i < all.Length; i++)
                if (!all[i].Free && all[i].InlineFeel == 0 && all[i].Beats == e.DivisionBeats) { shownFeel = e.InlineFeel; return i; }
            return entry;
        }

        /// <summary>A length as a person reads it: "1/8 dotted", "1 bar triplet", "2 bars", "5 beats" (a length that is no division).</summary>
        public static string Label(double divisionBeats, int feel)
        {
            for (int i = 0; i < DivisionBeats.Length; i++)
                if (DivisionBeats[i] == divisionBeats) return feel > 0 && feel < FeelNames.Length ? DivisionLabels[i] + " " + FeelNames[feel].ToLowerInvariant() : DivisionLabels[i];
            if (divisionBeats == 64) return feel > 0 && feel < FeelNames.Length ? "16 bars " + FeelNames[feel].ToLowerInvariant() : "16 bars";
            return divisionBeats.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " beats";
        }
    }
}
