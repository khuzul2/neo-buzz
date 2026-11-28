#nullable disable
// Fork addition (neo-buzz, docs/specs/notes.md section 9, CU6b): chords in the tracker. A clip shows as many note+velocity column pairs as
// the user wants for its Notes lane (a machine that takes real notes): + and - on the column header's menu and a shortcut. A pair has no data
// of its own: it is a view of the one Notes lane (clips.md section 5.3), a note sits on the lowest pair free at its time (NoteLanes), so a
// chord typed across the pairs is several notes at one time. The count is view state kept with the Notes lane (Key), saved
// with the clip and set by one undo step; the agent reads and writes the same count. Pure: a clip with no stored count shows as before.

using System;
using System.Collections.Generic;

namespace BuzzGUI.Common
{
    public static class NoteColumns
    {
        /// <summary>The metadata key (ClipMeta.NoteColumns).</summary>
        public const string Key = "NoteColumns";

        /// <summary>The most pairs a clip shows (the most voices a machine plays on the tracks route; a chord of more is stolen by the machine, not refused by the tracker).</summary>
        public const int Max = 32;

        /// <summary>The count stored in a lane's metadata, or null when it has none (or something that is not a count).</summary>
        public static int? Stored(IDictionary<string, string> meta) =>
            meta != null && meta.TryGetValue(Key, out var v) && int.TryParse(v, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n >= 1
                ? Math.Min(n, Max) : null;

        /// <summary>Writes the count into a lane's metadata (null takes it away: the clip shows as it did before it had one).</summary>
        public static void Store(IDictionary<string, string> meta, int? count)
        {
            if (count is int n) meta[Key] = Math.Clamp(n, 1, Max).ToString(System.Globalization.CultureInfo.InvariantCulture);
            else meta.Remove(Key);
        }

        /// <summary>
        /// The pairs shown: the stored count, never fewer than the notes need; with none stored the pairs the notes need and one free to type
        /// the next note in, when the lanes are shown at all (<paramref name="visible"/>: the column holds notes, or the user asked for them), else 0.
        /// </summary>
        public static int Shown(int? stored, int needed, bool visible)
        {
            if (stored is int n) return Math.Clamp(Math.Max(n, needed), 1, Max);
            return visible ? Math.Min(needed + 1, Max) : 0;
        }

        /// <summary>The count after +, or why not (null reason: done).</summary>
        public static string Add(int shown, out int count)
        {
            count = shown;
            if (shown >= Max) return $"A clip shows at most {Max} note columns.";
            count = shown + 1;
            return null;
        }

        /// <summary>The count after -, or why not: it never hides a note, and the clip keeps one pair.</summary>
        public static string Remove(int shown, NoteLanes lanes, out int count)
        {
            count = shown;
            if (shown <= 1) return "The clip keeps at least one note column.";
            int hidden = lanes?.NotesBeyond(shown - 1) ?? 0;
            if (hidden > 0)
                return $"{hidden} note{(hidden == 1 ? "" : "s")} would not be shown; keep at least {shown} columns.";
            count = shown - 1;
            return null;
        }
    }
}
