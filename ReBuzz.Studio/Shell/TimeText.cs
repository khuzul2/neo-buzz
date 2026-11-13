// Fork (docs/specs/ux-overhaul.md §2, one time unit): a place in the song or in a clip as bars.beats.ticks counted from 1,
// as the transport shows it ("9.1.1"), a length in bars and beats ("2 bars", "1 bar 2 beats"), and ticks only where the
// precision matters (a third field, a tooltip). Ticks were on the ruler, in the clip panel and in tooltips (gallery
// mac-030, mac-041), bars.beats.ticks in the transport, bars in the notes.

using System;
using System.Globalization;

namespace ReBuzz.Studio.Shell
{
    internal static class TimeText
    {
        /// <summary>A tick as bar.beat.tick, each from 1 ("1.1.1" is the start).</summary>
        public static string Position(int ticks, int ticksPerBeat, int beatsPerBar)
        {
            int tpb = Math.Max(1, ticksPerBeat), bpb = Math.Max(1, beatsPerBar);
            int t = Math.Max(0, ticks);
            int beat = t / tpb;
            return $"{beat / bpb + 1}.{beat % bpb + 1}.{t % tpb + 1}";
        }

        /// <summary>
        /// Where a tick is in minutes and seconds at a steady tempo ("1:05", "1:02:03" from an hour): the top bar's clock. A song with tempo
        /// changes (scenes with their own) is a little off after them; it is a guide, as the arrangement's ruler is not.
        /// </summary>
        public static string Clock(int ticks, int ticksPerBeat, double bpm)
        {
            int tpb = Math.Max(1, ticksPerBeat);
            double seconds = Math.Max(0, ticks) / (double)tpb * 60.0 / Math.Max(1, bpm);
            long s = (long)seconds;
            return s >= 3600 ? $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}" : $"{s / 60}:{s % 60:00}";
        }

        /// <summary>A tick as bar.beat from 1, the ticks left out ("2.1").</summary>
        public static string BarBeat(int ticks, int ticksPerBeat, int beatsPerBar)
        {
            int tpb = Math.Max(1, ticksPerBeat), bpb = Math.Max(1, beatsPerBar);
            int beat = Math.Max(0, ticks) / tpb;
            return $"{beat / bpb + 1}.{beat % bpb + 1}";
        }

        /// <summary>A length in words: "2 bars", "1 bar 2 beats", "3 beats", "1 beat 2 ticks", "5 ticks".</summary>
        public static string Length(int ticks, int ticksPerBeat, int beatsPerBar)
        {
            int tpb = Math.Max(1, ticksPerBeat), bpb = Math.Max(1, beatsPerBar);
            int t = Math.Max(0, ticks);
            int bars = t / (tpb * bpb), beats = t / tpb % bpb, rest = t % tpb;
            string Unit(int n, string one) => n == 1 ? $"1 {one}" : $"{n} {one}s";
            var parts = new System.Collections.Generic.List<string>();
            if (bars > 0) parts.Add(Unit(bars, "bar"));
            if (beats > 0) parts.Add(Unit(beats, "beat"));
            if (rest > 0 || parts.Count == 0) parts.Add(Unit(rest, "tick"));
            return string.Join(" ", parts);
        }

        /// <summary>
        /// A place typed as bar.beat.tick from 1 ("2.1.1", "2.1", "2"), or as ticks with a t ("16t"). False when it is
        /// neither, or before the start.
        /// </summary>
        public static bool TryParsePosition(string text, int ticksPerBeat, int beatsPerBar, out int ticks)
        {
            ticks = 0;
            text = text?.Trim() ?? "";
            if (text.Length == 0) return false;
            int tpb = Math.Max(1, ticksPerBeat), bpb = Math.Max(1, beatsPerBar);
            if (text.EndsWith("t", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(text.Substring(0, text.Length - 1).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out ticks);
            var parts = text.Split('.');
            if (parts.Length > 3) return false;
            var v = new[] { 1, 1, 1 };
            for (int i = 0; i < parts.Length; i++)
                if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out v[i]) || v[i] < 1) return false;
            if (v[1] > bpb || v[2] > tpb) return false;
            long t = ((long)(v[0] - 1) * bpb + (v[1] - 1)) * tpb + (v[2] - 1);
            if (t > 1_000_000) return false;
            ticks = (int)t;
            return true;
        }
    }
}
