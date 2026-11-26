#nullable disable
// Neo Chord (docs/specs/neo-chord.md): the music theory, as plain tables of our own written from the intervals of each chord and
// scale: which notes a chord has, how a scale numbers its steps, and how a chord is voiced (inversion, spread, octave, bass, limit).
// Pure and allocation-free: ChordBuilder.Build fills a caller's array.

using System;

namespace NeoBuzz.Chord
{
    /// <summary>The Chord parameter's values (Params.def order).</summary>
    internal static class ChordKind
    {
        public const int Off = 0, Major = 1, Minor = 2, Power = 3, Sus2 = 4, Sus4 = 5, Diminished = 6, Augmented = 7, Major6 = 8, Minor6 = 9,
            Dominant7 = 10, Major7 = 11, Minor7 = 12, Minor7Flat5 = 13, Diminished7 = 14, Add9 = 15, MinorAdd9 = 16, Dominant9 = 17,
            Major9 = 18, Minor9 = 19, StackedFourths = 20, Custom = 21;
    }

    internal static class ChordTheory
    {
        /// <summary>Semitones above the root of each chord's notes, lowest first, indexed by <see cref="ChordKind"/> (null: Off and Custom).</summary>
        public static readonly int[][] Shapes =
        {
            null,                       // Off
            new[] { 0, 4, 7 },          // Major
            new[] { 0, 3, 7 },          // Minor
            new[] { 0, 7, 12 },         // Power: root, fifth, octave
            new[] { 0, 2, 7 },          // Sus2
            new[] { 0, 5, 7 },          // Sus4
            new[] { 0, 3, 6 },          // Diminished
            new[] { 0, 4, 8 },          // Augmented
            new[] { 0, 4, 7, 9 },       // Major 6
            new[] { 0, 3, 7, 9 },       // Minor 6
            new[] { 0, 4, 7, 10 },      // Dominant 7
            new[] { 0, 4, 7, 11 },      // Major 7
            new[] { 0, 3, 7, 10 },      // Minor 7
            new[] { 0, 3, 6, 10 },      // Minor 7 flat 5
            new[] { 0, 3, 6, 9 },       // Diminished 7
            new[] { 0, 4, 7, 14 },      // Add 9
            new[] { 0, 3, 7, 14 },      // Minor add 9
            new[] { 0, 4, 7, 10, 14 },  // Dominant 9
            new[] { 0, 4, 7, 11, 14 },  // Major 9
            new[] { 0, 3, 7, 10, 14 },  // Minor 9
            new[] { 0, 5, 10, 15 },     // Stacked fourths
            null,                       // Custom
        };

        /// <summary>The seven steps of each scale in semitones above its first note (the Scale parameter's order).</summary>
        public static readonly int[][] Scales =
        {
            new[] { 0, 2, 4, 5, 7, 9, 11 },     // Major
            new[] { 0, 2, 3, 5, 7, 8, 10 },     // Natural minor
            new[] { 0, 2, 3, 5, 7, 8, 11 },     // Harmonic minor
            new[] { 0, 2, 3, 5, 7, 9, 11 },     // Melodic minor (the rising form)
            new[] { 0, 2, 3, 5, 7, 9, 10 },     // Dorian
            new[] { 0, 1, 3, 5, 7, 8, 10 },     // Phrygian
            new[] { 0, 2, 4, 6, 7, 9, 11 },     // Lydian
            new[] { 0, 2, 4, 5, 7, 9, 10 },     // Mixolydian
            new[] { 0, 1, 3, 5, 6, 8, 10 },     // Locrian
        };

        /// <summary>The note below which nothing is built (MIDI 0) and above which nothing is sent.</summary>
        public const int Lowest = 0, Highest = 127;
    }

    /// <summary>What a chord is built from: a snapshot of the machine's parameters, in real units.</summary>
    internal struct ChordSettings
    {
        public int Chord;            // ChordKind
        public bool Diatonic;
        public int Key;              // 0 (C) … 11 (B)
        public int Scale;            // 0 … 8
        public bool Sevenths;
        public int Custom1, Custom2, Custom3, Custom4;   // semitones above the root; int.MinValue: Off
        public int Inversion;        // 0 … 3
        public int Spread;           // 0 Close, 1 Open, 2 Drop 2, 3 Wide
        public int Octave;           // −2 … +2
        public int Bass;             // 0 Off, 1 root an octave down, 2 root two octaves down
        public int Voices;           // 2 … 7

        public const int Off = int.MinValue;

        public static ChordSettings Plain(int chord) => new ChordSettings { Chord = chord, Voices = 7, Custom1 = Off, Custom2 = Off, Custom3 = Off, Custom4 = Off };
    }

    internal static class ChordBuilder
    {
        /// <summary>The most notes a built chord can have (a Custom chord's root and four intervals, and a bass).</summary>
        public const int MaxNotes = 8;

        /// <summary>
        /// The notes of the chord built on <paramref name="note"/>, lowest first, written to <paramref name="pitches"/> (at least <see cref="MaxNotes"/> long); returns how many.
        /// <paramref name="root"/> is the pitch the chord is built on (the played note, or in Diatonic mode the scale note at or just below it).
        /// Order of work: the shape (Fixed) or the scale's stacked thirds (Diatonic), the inversion, the spread, the octave shift, the bass, the limit on the
        /// voices (the highest go first) and the keyboard's range (a note outside 0 … 127 is left out). Chord Off returns the note itself.
        /// </summary>
        public static int Build(in ChordSettings s, int note, int[] pitches, out int root)
        {
            root = note;
            if (s.Chord == ChordKind.Off)
            {
                pitches[0] = note;
                return 1;
            }
            int n = 0;
            if (s.Diatonic)
            {
                var scale = ChordTheory.Scales[Math.Clamp(s.Scale, 0, 8)];
                int rel = Mod(note - s.Key, 12);
                int baseNote = note - rel;             // the key's own note at or below the played one
                int degree = 0;
                for (int d = 0; d < 7; d++) if (scale[d] <= rel) degree = d;      // the scale step at or just below the note
                root = baseNote + scale[degree];
                int count = s.Sevenths ? 4 : 3;
                for (int k = 0; k < count; k++)
                {
                    int step = degree + 2 * k;
                    pitches[n++] = baseNote + scale[step % 7] + 12 * (step / 7);
                }
            }
            else if (s.Chord == ChordKind.Custom)
            {
                pitches[n++] = note;
                Add(pitches, ref n, note, s.Custom1);
                Add(pitches, ref n, note, s.Custom2);
                Add(pitches, ref n, note, s.Custom3);
                Add(pitches, ref n, note, s.Custom4);
                Sort(pitches, n);
            }
            else
            {
                var shape = ChordTheory.Shapes[Math.Clamp(s.Chord, 0, ChordTheory.Shapes.Length - 1)];
                for (int k = 0; k < shape.Length; k++) pitches[n++] = note + shape[k];
            }

            // Inversion: the lowest note goes up an octave, as many times as asked.
            for (int i = 0; i < s.Inversion && n > 0; i++)
            {
                pitches[0] += 12;
                Sort(pitches, n);
            }

            // Spread.
            switch (s.Spread)
            {
                case 1:     // Open: every second note up an octave
                    for (int i = 1; i < n; i += 2) pitches[i] += 12;
                    Sort(pitches, n);
                    break;
                case 2:     // Drop 2: the second note from the top down an octave
                    if (n >= 2) { pitches[n - 2] -= 12; Sort(pitches, n); }
                    break;
                case 3:     // Wide: Open, then the lowest note down an octave
                    for (int i = 1; i < n; i += 2) pitches[i] += 12;
                    Sort(pitches, n);
                    pitches[0] -= 12;
                    Sort(pitches, n);
                    break;
            }

            int shift = 12 * Math.Clamp(s.Octave, -2, 2);
            if (shift != 0) for (int i = 0; i < n; i++) pitches[i] += shift;

            if (s.Bass > 0 && n < MaxNotes) pitches[n++] = root + shift - 12 * Math.Clamp(s.Bass, 1, 2);
            Sort(pitches, n);

            // The same pitch twice is one note; then the voice limit (the highest go first) and the keyboard's range.
            n = Distinct(pitches, n);
            int limit = Math.Clamp(s.Voices, 2, 7);
            if (n > limit) n = limit;
            int m = 0;
            for (int i = 0; i < n; i++)
                if (pitches[i] >= ChordTheory.Lowest && pitches[i] <= ChordTheory.Highest) pitches[m++] = pitches[i];
            return m;
        }

        static void Add(int[] pitches, ref int n, int note, int interval)
        {
            if (interval == ChordSettings.Off || n >= MaxNotes) return;
            pitches[n++] = note + interval;
        }

        static int Mod(int a, int b) => ((a % b) + b) % b;

        /// <summary>Insertion sort, ascending (at most 8 notes).</summary>
        public static void Sort(int[] a, int n)
        {
            for (int i = 1; i < n; i++)
            {
                int v = a[i], j = i - 1;
                while (j >= 0 && a[j] > v) { a[j + 1] = a[j]; j--; }
                a[j + 1] = v;
            }
        }

        static int Distinct(int[] a, int n)
        {
            if (n == 0) return 0;
            int m = 1;
            for (int i = 1; i < n; i++) if (a[i] != a[m - 1]) a[m++] = a[i];
            return m;
        }
    }
}
