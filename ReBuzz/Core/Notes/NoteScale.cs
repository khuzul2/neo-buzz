// Fork (docs/specs/daw-modes.md AR2): a key and scale for the piano roll: which rows are highlighted, where a note snaps to, and
// which rows a fold keeps. Pure; the scales are the agent's theory (ReBuzz.Agent.Model.Scale).

using System;
using System.Collections.Generic;
using System.Linq;
using ReBuzz.Agent.Model;

namespace ReBuzz.Core.Notes
{
    internal enum FoldMode { Off, Used, Scale }

    internal sealed class NoteScale
    {
        readonly bool[] inScale = new bool[12];

        public NoteScale(int root, Scale scale)
        {
            Root = ((root % 12) + 12) % 12;
            Scale = scale ?? Scale.Major;
            foreach (int step in Scale.Intervals) inScale[(Root + step) % 12] = true;
        }

        /// <summary>The tonic's pitch class, 0 (C) to 11.</summary>
        public int Root { get; }

        public Scale Scale { get; }

        public bool Contains(int note) => inScale[((note % 12) + 12) % 12];

        public bool IsRoot(int note) => ((note % 12) + 12) % 12 == Root;

        /// <summary>The note itself when it is in the scale, else the nearest one that is (the lower of two equally near).</summary>
        public int Snap(int note)
        {
            note = Math.Clamp(note, 0, 127);
            for (int d = 0; d < 12; d++)
            {
                if (note - d >= 0 && Contains(note - d)) return note - d;
                if (note + d <= 127 && Contains(note + d)) return note + d;
            }
            return note;
        }

        /// <summary>
        /// The pitches a roll shows, highest first, or null for all 128: <see cref="FoldMode.Used"/> keeps the pitches in
        /// <paramref name="used"/> (none used: all), <see cref="FoldMode.Scale"/> the scale's pitches and those used.
        /// </summary>
        public static int[] Rows(FoldMode mode, IEnumerable<int> used, NoteScale scale)
        {
            var keep = new SortedSet<int>(used ?? Array.Empty<int>());
            switch (mode)
            {
                case FoldMode.Used:
                    return keep.Count == 0 ? null : keep.Reverse().ToArray();
                case FoldMode.Scale when scale != null:
                    for (int n = 0; n < 128; n++)
                        if (scale.Contains(n)) keep.Add(n);
                    return keep.Reverse().ToArray();
                default:
                    return null;
            }
        }
    }
}
