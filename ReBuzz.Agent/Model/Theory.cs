// Agent server (docs/specs/mcp-server.md §5.5, phase A3): music theory the agent can trust, so it
// never does interval arithmetic in its head. Spelled note names (a letter and an accidental, so
// Bb in F major and A# in B major), scales and keys, chord symbols to pitches, voicings and voice
// leading, roman-numeral progressions in a key, scale degrees and in-key transposition, and how well
// a melody fits a key. Pure: no ReBuzz types. Pitches are MIDI numbers (60 = C4); a spelled name's
// octave follows its letter, so Cb4 = 59 and B#3 = 60, as Pitch.TryParse reads them.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ReBuzz.Agent.Mcp;

namespace ReBuzz.Agent.Model
{
    /// <summary>Helpers shared by the theory types.</summary>
    public static class Theory
    {
        public static int Mod(int a, int m) => ((a % m) + m) % m;

        public static int FloorDiv(int a, int b) => (int)Math.Floor(a / (double)b);

        /// <summary>"#", "##", "b", "bb" or "" for an accidental.</summary>
        public static string Accidentals(int accidental) => accidental > 0 ? new string('#', accidental) : new string('b', -accidental);

        /// <summary>A MIDI pitch's name, spelled for the key when there is one ("Bb3" in F major); otherwise Pitch.Name's sharps.</summary>
        public static string Name(int midi, MusicKey? key) => key?.NameOf(midi) ?? Pitch.Name(midi);

        /// <summary>Letter steps above a tonic for each of the 12 semitones, spelled 1 b2 2 b3 3 4 #4 5 b6 6 b7 7.</summary>
        internal static readonly int[] SemitoneLetters = { 0, 1, 1, 2, 2, 3, 3, 4, 5, 5, 6, 6 };

        /// <summary>The major scale's semitones per letter step (the reference for degree labels).</summary>
        internal static readonly int[] MajorSemitones = { 0, 2, 4, 5, 7, 9, 11 };

        /// <summary>Unicode music signs to the ASCII the parsers read.</summary>
        internal static string Normalize(string text) =>
            text.Trim().Replace('♭', 'b').Replace('♯', '#').Replace('−', '-').Replace('º', '°').Replace('Ø', 'ø').Replace('^', 'Δ').Replace('△', 'Δ');

        internal static string Fmt(double x) => Math.Round(x, 3).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>A spelled note: a letter (0 = C … 6 = B) and an accidental (−1 flat, +1 sharp, ±2 double).</summary>
    public readonly record struct NoteName(int Letter, int Accidental)
    {
        public const string LetterNames = "CDEFGAB";
        static readonly int[] Naturals = { 0, 2, 4, 5, 7, 9, 11 };

        public static int Natural(int letter) => Naturals[Theory.Mod(letter, 7)];

        public int PitchClass => Theory.Mod(Natural(Letter) + Accidental, 12);

        public override string ToString() => LetterNames[Theory.Mod(Letter, 7)] + Theory.Accidentals(Accidental);

        /// <summary>The scientific octave of this spelling at a MIDI pitch: Cb4 is 59, B#3 is 60.</summary>
        public int Octave(int midi) => Theory.FloorDiv(midi - Accidental - Natural(Letter), 12) - 1;

        /// <summary>"Bb3" for MIDI 58.</summary>
        public string Name(int midi) => ToString() + Octave(midi).ToString(CultureInfo.InvariantCulture);

        /// <summary>The MIDI pitch of this spelling in a scientific octave (Bb3 → 58).</summary>
        public int Midi(int octave) => (octave + 1) * 12 + Natural(Letter) + Accidental;

        /// <summary>The note an interval above, spelled by letter steps: a minor third is 2 letters and 3 semitones.</summary>
        public NoteName Up(int letterSteps, int semitones)
        {
            int letter = Theory.Mod(Letter + letterSteps, 7);
            int target = Theory.Mod(PitchClass + semitones, 12);
            return new NoteName(letter, Theory.Mod(target - Natural(letter) + 6, 12) - 6);
        }

        /// <summary>The plain spelling of a pitch class: a natural, else one sharp or one flat.</summary>
        public static NoteName Plain(int pitchClass, bool flats)
        {
            int pc = Theory.Mod(pitchClass, 12);
            int natural = Array.IndexOf(Naturals, pc);
            if (natural >= 0) return new NoteName(natural, 0);
            return flats ? new NoteName(Array.IndexOf(Naturals, pc + 1), -1) : new NoteName(Array.IndexOf(Naturals, pc - 1), 1);
        }

        /// <summary>Reads a letter (either case) and its accidentals at <paramref name="index"/>.</summary>
        public static bool TryRead(string text, ref int index, out NoteName name)
        {
            name = default;
            if (index >= text.Length) return false;
            int letter = LetterNames.IndexOf(char.ToUpperInvariant(text[index]));
            if (letter < 0) return false;
            int i = index + 1, accidental = 0;
            while (i < text.Length && (text[i] == '#' || text[i] == '♯' || text[i] == 'b' || text[i] == '♭'))
            {
                accidental += text[i] == '#' || text[i] == '♯' ? 1 : -1;
                i++;
            }
            name = new NoteName(letter, accidental);
            index = i;
            return true;
        }
    }

    /// <summary>A pitch with its spelling, so names follow the chord or key ("Fb5", not "E5", in Gb7).</summary>
    public readonly record struct SpelledPitch(int Midi, NoteName Spelling)
    {
        public string Name => Spelling.Name(Midi);

        public override string ToString() => Name;
    }

    /// <summary>A scale: semitones above the tonic and the letter steps that spell them.</summary>
    public sealed class Scale
    {
        static readonly int[] Seven = { 0, 1, 2, 3, 4, 5, 6 };

        Scale(string name, int[] intervals, int[]? letters, bool diatonic, params string[] aliases)
        {
            Name = name;
            Intervals = intervals;
            Letters = letters;
            Diatonic = diatonic;
            Aliases = aliases;
        }

        public string Name { get; }
        public IReadOnlyList<int> Intervals { get; }
        /// <summary>Letter steps above the tonic for each note (null for the chromatic scale, spelled by the key).</summary>
        public IReadOnlyList<int>? Letters { get; }
        /// <summary>A mode of the major scale: it has its own key signature.</summary>
        public bool Diatonic { get; }
        public IReadOnlyList<string> Aliases { get; }

        public bool IsHeptatonic => Intervals.Count == 7;
        public bool HasMinorThird => Intervals.Contains(3) && !Intervals.Contains(4);
        /// <summary>The scale whose key signature this one is written with (harmonic minor uses the minor key's).</summary>
        public Scale SignatureScale => Diatonic ? this : HasMinorThird ? Minor : Major;
        public bool Contains(int semitones) => Intervals.Contains(Theory.Mod(semitones, 12));
        public override string ToString() => Name;

        public static readonly Scale Major = new("major", new[] { 0, 2, 4, 5, 7, 9, 11 }, Seven, true, "", "major", "maj", "ionian", "dur");
        public static readonly Scale Minor = new("minor", new[] { 0, 2, 3, 5, 7, 8, 10 }, Seven, true, "minor", "min", "aeolian", "natural minor", "nat minor", "moll");
        public static readonly Scale HarmonicMinor = new("harmonic minor", new[] { 0, 2, 3, 5, 7, 8, 11 }, Seven, false, "harmonic minor", "harmonic", "harm minor", "minor harmonic");
        public static readonly Scale MelodicMinor = new("melodic minor", new[] { 0, 2, 3, 5, 7, 9, 11 }, Seven, false, "melodic minor", "melodic", "jazz minor", "minor melodic");
        public static readonly Scale Dorian = new("dorian", new[] { 0, 2, 3, 5, 7, 9, 10 }, Seven, true, "dorian");
        public static readonly Scale Phrygian = new("phrygian", new[] { 0, 1, 3, 5, 7, 8, 10 }, Seven, true, "phrygian");
        public static readonly Scale Lydian = new("lydian", new[] { 0, 2, 4, 6, 7, 9, 11 }, Seven, true, "lydian");
        public static readonly Scale Mixolydian = new("mixolydian", new[] { 0, 2, 4, 5, 7, 9, 10 }, Seven, true, "mixolydian", "mixo");
        public static readonly Scale Locrian = new("locrian", new[] { 0, 1, 3, 5, 6, 8, 10 }, Seven, true, "locrian");
        public static readonly Scale MajorPentatonic = new("major pentatonic", new[] { 0, 2, 4, 7, 9 }, new[] { 0, 1, 2, 4, 5 }, false, "major pentatonic", "pentatonic", "pentatonic major", "maj pentatonic", "penta");
        public static readonly Scale MinorPentatonic = new("minor pentatonic", new[] { 0, 3, 5, 7, 10 }, new[] { 0, 2, 3, 4, 6 }, false, "minor pentatonic", "pentatonic minor", "min pentatonic", "m pentatonic");
        public static readonly Scale Blues = new("blues", new[] { 0, 3, 5, 6, 7, 10 }, new[] { 0, 2, 3, 4, 4, 6 }, false, "blues", "minor blues");
        public static readonly Scale WholeTone = new("whole tone", new[] { 0, 2, 4, 6, 8, 10 }, new[] { 0, 1, 2, 3, 4, 5 }, false, "whole tone", "wholetone");
        public static readonly Scale Chromatic = new("chromatic", Enumerable.Range(0, 12).ToArray(), null, false, "chromatic");

        public static readonly IReadOnlyList<Scale> All = new[]
        {
            Major, Minor, HarmonicMinor, MelodicMinor, Dorian, Phrygian, Lydian, Mixolydian, Locrian,
            MajorPentatonic, MinorPentatonic, Blues, WholeTone, Chromatic,
        };

        /// <summary>A scale by name or alias ("minor", "aeolian", "mixo", "whole-tone"), any case; null if unknown.</summary>
        public static Scale? Find(string text)
        {
            string t = string.Join(" ", text.ToLowerInvariant().Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
            return All.FirstOrDefault(s => s.Aliases.Contains(t));
        }
    }

    /// <summary>A key: a spelled tonic and a scale ("F# major", "D dorian", "A harmonic minor").</summary>
    public sealed class MusicKey
    {
        static readonly int[] SharpOrder = { 3, 0, 4, 1, 5, 2, 6 };   // F C G D A E B
        static readonly int[] FlatOrder = { 6, 2, 5, 1, 4, 0, 3 };    // B E A D G C F
        readonly List<NoteName> signatureNotes;

        public MusicKey(NoteName tonic, Scale scale)
        {
            Tonic = tonic;
            Scale = scale;
            var sig = scale.SignatureScale;
            signatureNotes = sig.Letters!.Select((l, i) => tonic.Up(l, sig.Intervals[i])).ToList();
            Signature = signatureNotes.Sum(n => n.Accidental);
            Notes = scale.Letters != null
                ? scale.Letters.Select((l, i) => tonic.Up(l, scale.Intervals[i])).ToList()
                : scale.Intervals.Select(s => s == 0 ? tonic : Outside(tonic.PitchClass + s)).ToList();
        }

        public NoteName Tonic { get; }
        public Scale Scale { get; }
        /// <summary>The scale's notes, spelled ("F G A Bb C D E" in F major).</summary>
        public IReadOnlyList<NoteName> Notes { get; }
        /// <summary>Sharps (positive) or flats (negative) in the key signature.</summary>
        public int Signature { get; }
        public string Name => $"{Tonic} {Scale.Name}";
        public override string ToString() => Name;

        /// <summary>The signature's accidentals in the order they are written (F# C# G# …, Bb Eb Ab …).</summary>
        public IReadOnlyList<NoteName> SignatureAccidentals =>
            (Signature >= 0 ? SharpOrder : FlatOrder).Select(l => signatureNotes.First(n => n.Letter == l)).Where(n => n.Accidental != 0).ToList();

        public bool Contains(int midi) => Scale.Contains(midi - Tonic.PitchClass);

        /// <summary>How the key spells a pitch: its scale spelling; else a natural; else the signature's sharps or flats
        /// (keys without accidentals spell b2 b3 #4 b6 b7 from the tonic: Eb in C major, G# in A minor).</summary>
        public NoteName Spell(int midi)
        {
            int pc = Theory.Mod(midi, 12);
            foreach (var n in Notes)
                if (n.PitchClass == pc) return n;
            return Outside(pc);
        }

        NoteName Outside(int pitchClass)
        {
            var plain = NoteName.Plain(pitchClass, Signature < 0);
            if (plain.Accidental == 0 || Signature != 0) return plain;
            int interval = Theory.Mod(pitchClass - Tonic.PitchClass, 12);
            var n = Tonic.Up(Theory.SemitoneLetters[interval], interval);
            return Math.Abs(n.Accidental) <= 1 ? n : plain;
        }

        /// <summary>The key-aware name of a MIDI pitch: "Bb3" in F major, "A#3" in B major.</summary>
        public string NameOf(int midi) => Spell(midi).Name(midi);

        public SpelledPitch Spelled(int midi) => new(midi, Spell(midi));

        /// <summary>The pitch's 1-based position in the scale, or null when it isn't in it.</summary>
        public int? Degree(int midi)
        {
            int i = IndexOf(Scale.Intervals, Theory.Mod(midi - Tonic.PitchClass, 12));
            return i < 0 ? null : i + 1;
        }

        /// <summary>
        /// The scale degree as musicians write it: "3" in the scale, "b3" or "#7" when altered. Seven-note scales
        /// count their own degrees (G# in A minor is "#7"); other scales, or <paramref name="majorReference"/>, count from the
        /// major scale ("b3" in a minor pentatonic or in dorian).
        /// </summary>
        public string DegreeLabel(int midi, bool majorReference = false)
        {
            var name = Spell(midi);
            int letter = Theory.Mod(name.Letter - Tonic.Letter, 7);
            int reference = Scale.IsHeptatonic && !majorReference ? Notes[letter].PitchClass : Theory.Mod(Tonic.PitchClass + Theory.MajorSemitones[letter], 12);
            int alter = Theory.Mod(name.PitchClass - reference + 6, 12) - 6;
            return Theory.Accidentals(alter) + (letter + 1).ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Moves a pitch by steps of the scale (2 = a third up in the key). A pitch outside the scale keeps its distance
        /// above the scale note below it; <paramref name="chromatic"/> says so.
        /// </summary>
        public int Step(int midi, int steps, out bool chromatic)
        {
            var intervals = Scale.Intervals;
            int n = intervals.Count;
            int rel = midi - Tonic.PitchClass;
            int octave = Theory.FloorDiv(rel, 12), within = Theory.Mod(rel, 12);
            int index = 0;
            for (int i = 0; i < n; i++)
                if (intervals[i] <= within) index = i;
            int offset = within - intervals[index];
            chromatic = offset != 0;
            int target = index + steps;
            return Tonic.PitchClass + (octave + Theory.FloorDiv(target, n)) * 12 + intervals[Theory.Mod(target, n)] + offset;
        }

        public MusicKey? Relative =>
            Scale == Scale.Major ? new MusicKey(Notes[5], Scale.Minor) : Scale == Scale.Minor ? new MusicKey(Notes[2], Scale.Major) : null;

        public MusicKey? Parallel =>
            Scale == Scale.Major ? new MusicKey(Tonic, Scale.Minor) : Scale == Scale.Minor ? new MusicKey(Tonic, Scale.Major) : null;

        /// <summary>The chords built in thirds on each degree of a seven-note scale (empty for other scales).</summary>
        public List<DiatonicChord> DiatonicChords(bool sevenths)
        {
            var list = new List<DiatonicChord>();
            if (!Scale.IsHeptatonic) return list;
            for (int d = 0; d < 7; d++)
            {
                int third = Rel(d, 2), fifth = Rel(d, 4), seventh = Rel(d, 6);
                var tones = new List<NoteName> { Notes[d], Notes[(d + 2) % 7], Notes[(d + 4) % 7] };
                if (sevenths) tones.Add(Notes[(d + 6) % 7]);
                var (suffix, upper, mark) = Quality(third, fifth, sevenths ? seventh : null);
                string numeral = upper ? Roman.Numerals[d] : Roman.Numerals[d].ToLowerInvariant();
                list.Add(new DiatonicChord(numeral + mark, $"{Notes[d]}{suffix}", tones));
            }
            return list;
        }

        int Rel(int degree, int up) => Theory.Mod(Scale.Intervals[(degree + up) % 7] - Scale.Intervals[degree], 12);

        static (string Suffix, bool Upper, string Mark) Quality(int third, int fifth, int? seventh) => (third, fifth, seventh) switch
        {
            (4, 7, null) => ("", true, ""),
            (3, 7, null) => ("m", false, ""),
            (3, 6, null) => ("dim", false, "°"),
            (4, 8, null) => ("aug", true, "+"),
            (4, 7, 11) => ("maj7", true, "maj7"),
            (4, 7, 10) => ("7", true, "7"),
            (3, 7, 10) => ("m7", false, "7"),
            (3, 6, 10) => ("m7b5", false, "ø7"),
            (3, 6, 9) => ("dim7", false, "°7"),
            (3, 7, 11) => ("m(maj7)", false, "maj7"),
            (4, 8, 11) => ("maj7#5", true, "+maj7"),
            (4, 8, 10) => ("7#5", true, "+7"),
            _ => ("", true, ""),
        };

        static int IndexOf(IReadOnlyList<int> list, int value)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i] == value) return i;
            return -1;
        }

        /// <summary>A key from text: "A minor", "F# major", "D dorian", "Bbm", "C", "E harmonic minor", "A minor pentatonic".</summary>
        public static MusicKey Parse(string text) => TryParse(text, out var key, out var error) ? key! : throw new ToolError(error!);

        public static bool TryParse(string? text, out MusicKey? key, out string? error)
        {
            key = null;
            error = null;
            string t = Theory.Normalize(text ?? "");
            int i = 0;
            if (!NoteName.TryRead(t, ref i, out var tonic))
            {
                error = $"\"{text}\" isn't a key: start with the tonic, like \"A minor\", \"F# major\", \"D dorian\" or \"Bbm\".";
                return false;
            }
            string rest = t.Substring(i).Trim();
            var scale = rest switch { "M" => Scale.Major, "m" => Scale.Minor, _ => Scale.Find(rest) };
            if (scale == null)
            {
                var close = Text.Closest(rest.ToLowerInvariant(), Scale.All.Select(s => s.Name));
                error = $"\"{text}\": unknown scale \"{rest}\"." + (close.Count > 0 ? $" Did you mean \"{tonic} {close[0]}\"?" : "") +
                        " Scales: " + string.Join(", ", Scale.All.Select(s => s.Name)) + ".";
                return false;
            }
            key = new MusicKey(tonic, scale);
            return true;
        }
    }

    /// <summary>A chord on a scale degree: "ii7", "Dm7", D F A C.</summary>
    public sealed record DiatonicChord(string Numeral, string Symbol, IReadOnlyList<NoteName> Tones);

    /// <summary>A chord tone: its degree above the root (1, 3, 5, 7, 9, 11, 13; 2, 4, 6 for sus and added tones) and semitones.</summary>
    public readonly record struct ChordTone(int Degree, int Semitones)
    {
        /// <summary>"b3", "#11", "bb7".</summary>
        public string Label => Theory.Accidentals(Semitones - Natural(Degree)) + Degree.ToString(CultureInfo.InvariantCulture);

        public static int Natural(int degree) => degree switch
        {
            1 => 0, 2 => 2, 3 => 4, 4 => 5, 5 => 7, 6 => 9, 7 => 11, 9 => 14, 11 => 17, 13 => 21,
            _ => throw new ArgumentOutOfRangeException(nameof(degree)),
        };
    }

    /// <summary>A chord read from a symbol ("Am9/E", "F#m7b5", "G7(b9,#11)", "C6/9").</summary>
    public sealed class Chord
    {
        Chord(string symbol, NoteName root, NoteName? bass, List<ChordTone> tones)
        {
            Symbol = symbol;
            Root = root;
            Bass = bass;
            Tones = tones;
        }

        public string Symbol { get; }
        public NoteName Root { get; }
        /// <summary>The slash bass ("E" in "Am9/E"), if any.</summary>
        public NoteName? Bass { get; }
        /// <summary>Root position, lowest first.</summary>
        public IReadOnlyList<ChordTone> Tones { get; }

        public IReadOnlyList<NoteName> ToneNames => Tones.Select(Spell).ToList();
        public NoteName Spell(ChordTone t) => Root.Up(t.Degree - 1, t.Semitones);
        /// <summary>"1 b3 5 b7 9".</summary>
        public string Intervals => string.Join(" ", Tones.Select(t => t.Label));
        public bool IsMinor => Tones.Any(t => t.Degree == 3 && t.Semitones == 3);
        /// <summary>Every pitch class the chord sounds, the slash bass included.</summary>
        public IEnumerable<NoteName> AllNames => Bass is NoteName b ? ToneNames.Prepend(b) : ToneNames;

        static readonly string[] CommonSuffixes =
        {
            "", "m", "dim", "aug", "sus2", "sus4", "5", "6", "m6", "6/9", "7", "maj7", "m7", "m7b5", "dim7", "m(maj7)", "7sus4",
            "9", "maj9", "m9", "11", "m11", "13", "maj13", "m13", "add9", "madd9", "7b9", "7#9", "7#11", "7b13", "7alt", "9sus4",
        };

        public static Chord Parse(string symbol) => TryParse(symbol, out var chord, out var error) ? chord! : throw new ToolError(error!);

        public static bool TryParse(string? symbol, out Chord? chord, out string? error)
        {
            chord = null;
            error = null;
            const string Shapes = "Chord symbols look like C, Am, F#m7b5, Bbmaj9, G7(b9), Dsus4, C6/9, Am9/E.";
            string s = Theory.Normalize(symbol ?? "").Replace(" ", "");
            int i = 0;
            if (!NoteName.TryRead(s, ref i, out var root))
            {
                error = $"\"{symbol}\" doesn't start with a root note A–G. {Shapes}";
                return false;
            }
            string rest = s.Substring(i);
            NoteName? bass = null;
            int slash = rest.LastIndexOf('/');
            if (slash >= 0 && slash + 1 < rest.Length && char.IsLetter(rest[slash + 1]))
            {
                string b = rest.Substring(slash + 1);
                int j = 0;
                if (!NoteName.TryRead(b, ref j, out var bn) || j != b.Length)
                {
                    error = $"\"{symbol}\": after \"/\" comes the bass note, like \"Am9/E\" or \"D/F#\".";
                    return false;
                }
                bass = bn;
                rest = rest.Substring(0, slash);
            }
            string suffix = rest.Replace("6/9", "69").Replace("(", "").Replace(")", "").Replace(",", "");
            var tones = new List<ChordTone>();
            string? problem = new SuffixReader(suffix).Read(tones);
            if (problem != null)
            {
                string rootText = root.ToString();
                var close = CommonSuffixes.Select(x => (x, d: Text.Distance(x, rest))).Where(x => x.d <= Math.Max(1, rest.Length / 2))
                    .OrderBy(x => x.d).Take(2).Select(x => $"\"{rootText}{x.x}\"").ToList();
                error = $"Can't read the chord \"{symbol}\": {problem}." + (close.Count > 0 ? $" Did you mean {string.Join(" or ", close)}?" : "") + " " + Shapes;
                return false;
            }
            chord = new Chord(Theory.Normalize(symbol!), root, bass, tones);
            return true;
        }

        /// <summary>Reads what follows the root: quality, extension, then sus/add/alterations/omissions.</summary>
        sealed class SuffixReader
        {
            readonly string s;
            int i;

            public SuffixReader(string suffix) => s = suffix;

            bool Eat(string token, bool ignoreCase = false)
            {
                if (i + token.Length > s.Length || string.Compare(s, i, token, 0, token.Length, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) != 0)
                    return false;
                i += token.Length;
                return true;
            }

            bool DigitAt(int k) => k < s.Length && char.IsDigit(s[k]);

            bool EatMajorMark() => Eat("maj", true) || Eat("M") || Eat("Δ");

            /// <summary>An optional sign and a degree (2, 4, 5, 6, 9, 11, 13) as a tone; null if there isn't one.</summary>
            ChordTone? Degree(bool signRequired)
            {
                int start = i, sign = 0;
                while (i < s.Length && (s[i] == 'b' || s[i] == '#' || s[i] == '+' || s[i] == '-'))
                {
                    sign += s[i] == '#' || s[i] == '+' ? 1 : -1;
                    i++;
                }
                if (signRequired && sign == 0) { i = start; return null; }
                foreach (int d in new[] { 11, 13, 2, 4, 5, 6, 9 })
                    if (Eat(d.ToString(CultureInfo.InvariantCulture)))
                        return new ChordTone(d, ChordTone.Natural(d) + sign);
                i = start;
                return null;
            }

            /// <summary>Fills <paramref name="tones"/>; returns a problem, or null.</summary>
            public string? Read(List<ChordTone> tones)
            {
                bool minor = false, dim = false, halfDim = false, aug = false, maj = false, delta = false, power = false;
                if (s == "5") { power = true; i = 1; }
                else if (Eat("maj", true)) maj = true;
                else if (i + 2 < s.Length && string.Compare(s, 0, "ma", 0, 2, StringComparison.OrdinalIgnoreCase) == 0 && DigitAt(2)) { i = 2; maj = true; }
                else if (Eat("M")) maj = true;
                else if (Eat("Δ")) { maj = true; delta = true; }
                else if (Eat("min", true) || Eat("mi") || Eat("m") || Eat("-"))
                {
                    minor = true;
                    if (EatMajorMark()) maj = true;
                }
                else if (Eat("dim", true) || Eat("°") || (!s.StartsWith("omit", StringComparison.OrdinalIgnoreCase) && Eat("o"))) dim = true;
                else if (Eat("ø")) halfDim = true;
                else if (Eat("aug", true) || Eat("+"))
                {
                    aug = true;
                    if (EatMajorMark()) maj = true;
                }

                int ext = 0;
                foreach (int e in new[] { 69, 13, 11, 9, 7, 6 })
                    if (Eat(e.ToString(CultureInfo.InvariantCulture))) { ext = e; break; }

                bool sus2 = false, sus4 = false, alt = false, no3 = false, no5 = false;
                var adds = new List<ChordTone>();
                var alters = new List<ChordTone>();
                while (i < s.Length)
                {
                    int at = i;
                    if (Eat("sus", true))
                    {
                        if (Eat("2")) sus2 = true;
                        else { Eat("4"); sus4 = true; }
                        continue;
                    }
                    if (Eat("add", true))
                    {
                        if (Degree(false) is not ChordTone added || added.Degree == 5) return $"\"{s.Substring(at)}\": add takes 2, 4, 6, 9, 11 or 13 (add9, addb9)";
                        adds.Add(added);
                        continue;
                    }
                    if (Eat("alt", true)) { alt = true; continue; }
                    if (Eat("omit", true) || Eat("no", true))
                    {
                        if (Eat("3")) no3 = true;
                        else if (Eat("5")) no5 = true;
                        else return $"\"{s.Substring(at)}\": omit 3 or 5 (no3, omit5)";
                        continue;
                    }
                    if (s[i] == '+' && !DigitAt(i + 1)) { i++; alters.Add(new ChordTone(5, 8)); continue; }
                    if (Degree(true) is ChordTone altered)
                    {
                        if (altered.Degree is 2 or 4) return $"\"{s.Substring(at)}\" isn't an alteration (b5, #5, b9, #9, #11, b13)";
                        alters.Add(altered);
                        continue;
                    }
                    return $"\"{s.Substring(at)}\" isn't a chord part";
                }

                if (power && (ext != 0 || adds.Count > 0 || alters.Count > 0)) return "a power chord (C5) has no other parts";
                var list = new List<ChordTone> { new(1, 0) };
                if (sus2 || sus4)
                {
                    if (sus2) list.Add(new ChordTone(2, 2));
                    if (sus4) list.Add(new ChordTone(4, 5));
                }
                else if (!no3 && !power) list.Add(new ChordTone(3, minor || dim || halfDim ? 3 : 4));

                var fifths = alters.Where(a => a.Degree == 5).ToList();
                if (power) list.Add(new ChordTone(5, 7));
                else if (!no5 && !alt) list.AddRange(fifths.Count > 0 ? fifths : new List<ChordTone> { new(5, dim || halfDim ? 6 : aug ? 8 : 7) });

                if (ext is 6 or 69) list.Add(new ChordTone(6, 9));
                list.AddRange(alters.Where(a => a.Degree == 6));

                if (ext is 7 or 9 or 11 or 13 || halfDim || alt || (delta && ext == 0))
                    list.Add(new ChordTone(7, maj ? 11 : dim ? 9 : 10));

                var ninths = alters.Where(a => a.Degree == 9).ToList();
                if (alt) list.AddRange(new[] { new ChordTone(9, 13), new ChordTone(9, 15) });
                else if (ninths.Count > 0) list.AddRange(ninths);
                else if (ext is 9 or 11 or 13 or 69) list.Add(new ChordTone(9, 14));

                var elevenths = alters.Where(a => a.Degree == 11).ToList();
                if (elevenths.Count > 0) list.AddRange(elevenths);
                else if (ext == 11 || (ext == 13 && (minor || dim || halfDim) && !sus4)) list.Add(new ChordTone(11, 17));

                var thirteenths = alters.Where(a => a.Degree == 13).ToList();
                if (alt) list.Add(new ChordTone(13, 20));
                else if (thirteenths.Count > 0) list.AddRange(thirteenths);
                else if (ext == 13) list.Add(new ChordTone(13, 21));

                list.AddRange(adds);
                // One tone per pitch class (C7b5#11 sounds F# once), lowest first.
                var seen = new HashSet<int>();
                foreach (var t in list.OrderBy(t => t.Semitones))
                    if (seen.Add(Theory.Mod(t.Semitones, 12))) tones.Add(t);
                return null;
            }
        }
    }

    public enum Voicing { Close, Open, Drop2, Drop3 }

    /// <summary>Chords to pitches: voicings, inversions, registers and voice leading.</summary>
    public static class Voicer
    {
        public static readonly string[] VoicingNames = { "close", "open", "drop2", "drop3" };

        public static Voicing ParseVoicing(string? text)
        {
            if (text == null) return Voicing.Close;
            int i = Array.IndexOf(VoicingNames, text.Trim().ToLowerInvariant().Replace(" ", "").Replace("-", ""));
            if (i >= 0) return (Voicing)i;
            var close = Text.Closest(text, VoicingNames);
            throw new ToolError($"Unknown voicing \"{text}\"." + (close.Count > 0 ? $" Did you mean \"{close[0]}\"?" : "") + " Voicings: close, open, drop2, drop3.");
        }

        /// <summary>
        /// The chord's pitches, lowest first. Close: root position stacked upward from the root in <paramref name="octave"/>
        /// (9ths and above sit above the 7th). Inversions move the lowest notes up an octave; open raises every second note an
        /// octave; drop2/drop3 lower the second/third highest an octave. A slash bass goes just below the rest.
        /// </summary>
        public static List<SpelledPitch> Voice(Chord chord, Voicing voicing = Voicing.Close, int inversion = 0, int octave = 4)
        {
            int root = chord.Root.Midi(octave);
            var notes = chord.Tones.Select(t => new SpelledPitch(root + t.Semitones, chord.Spell(t))).ToList();
            if (inversion < 0 || inversion >= notes.Count)
                throw new ToolError($"{chord.Symbol} has {notes.Count} notes, so inversion is 0–{notes.Count - 1} (0 = root position, 1 = the next chord tone in the bass).");
            for (int k = 0; k < inversion; k++)
            {
                var low = notes[0];
                notes.RemoveAt(0);
                int p = low.Midi + 12;
                while (notes.Any(n => n.Midi == p)) p += 12;
                notes.Add(low with { Midi = p });
                Sort(notes);
            }
            switch (voicing)
            {
                case Voicing.Open:
                    for (int k = 1; k < notes.Count; k += 2) notes[k] = notes[k] with { Midi = notes[k].Midi + 12 };
                    break;
                case Voicing.Drop2 when notes.Count >= 2:
                    notes[^2] = notes[^2] with { Midi = notes[^2].Midi - 12 };
                    break;
                case Voicing.Drop3 when notes.Count >= 3:
                    notes[^3] = notes[^3] with { Midi = notes[^3].Midi - 12 };
                    break;
            }
            Sort(notes);
            if (chord.Bass is NoteName bass)
            {
                int below = notes[0].Midi - 1;
                notes.Insert(0, new SpelledPitch(below - Theory.Mod(below - bass.PitchClass, 12), bass));
            }
            return notes;
        }

        /// <summary>Moves a voicing by octaves so its lowest note is at or just above <paramref name="register"/>.</summary>
        public static List<SpelledPitch> Place(List<SpelledPitch> notes, int register)
        {
            int shift = 12 * (int)Math.Ceiling((register - notes[0].Midi) / 12.0);
            return notes.Select(n => n with { Midi = n.Midi + shift }).ToList();
        }

        /// <summary>
        /// The voicing of <paramref name="chord"/> (any inversion, any octave) that moves least from <paramref name="previous"/>:
        /// the sum of each voice's movement when the counts match, else of each note's distance to the nearest note of the other chord.
        /// </summary>
        public static List<SpelledPitch> VoiceLead(Chord chord, IReadOnlyList<int> previous, Voicing voicing = Voicing.Close)
        {
            if (previous.Count == 0) return Voice(chord, voicing);
            double center = previous.Average();
            List<SpelledPitch>? best = null;
            int bestCost = int.MaxValue;
            double bestCenter = double.MaxValue;
            for (int inv = 0; inv < chord.Tones.Count; inv++)
            {
                var voiced = Voice(chord, voicing, inv, 4);
                for (int k = -6; k <= 6; k++)
                {
                    var candidate = voiced.Select(n => n with { Midi = n.Midi + 12 * k }).ToList();
                    if (candidate[0].Midi < 0 || candidate[^1].Midi > 127) continue;
                    int cost = Movement(previous, candidate.Select(n => n.Midi).ToList());
                    double off = Math.Abs(candidate.Average(n => n.Midi) - center);
                    if (cost < bestCost || (cost == bestCost && off < bestCenter - 1e-9))
                    {
                        best = candidate;
                        bestCost = cost;
                        bestCenter = off;
                    }
                }
            }
            return best ?? Voice(chord, voicing);
        }

        /// <summary>Semitones moved between two chords (voice by voice when they have as many notes).</summary>
        public static int Movement(IReadOnlyList<int> from, IReadOnlyList<int> to)
        {
            var a = from.OrderBy(x => x).ToList();
            var b = to.OrderBy(x => x).ToList();
            if (a.Count == 0 || b.Count == 0) return 0;
            if (a.Count == b.Count) return a.Zip(b, (x, y) => Math.Abs(x - y)).Sum();
            return b.Sum(y => a.Min(x => Math.Abs(x - y))) + a.Sum(x => b.Min(y => Math.Abs(x - y)));
        }

        /// <summary>Throws a ToolError naming the way out when a voicing leaves MIDI's 0–127.</summary>
        public static void CheckRange(IReadOnlyList<SpelledPitch> notes, string what)
        {
            if (notes[0].Midi < 0) throw new ToolError($"{what} goes below C-1 (MIDI 0); use a higher octave or register.");
            if (notes[^1].Midi > 127) throw new ToolError($"{what} goes above G9 (MIDI 127); use a lower octave or register.");
        }

        static void Sort(List<SpelledPitch> notes) => notes.Sort((x, y) => x.Midi.CompareTo(y.Midi));
    }

    /// <summary>A chord in a progression: what was written, its symbol in the key, and an inversion from figured bass.</summary>
    public sealed record ProgressionChord(string Token, string? Numeral, string Symbol, Chord Chord, int Inversion);

    /// <summary>Roman numerals in a key: "ii7", "V7/V", "bVII", "viiø7", "V65".</summary>
    public static class Roman
    {
        internal static readonly string[] Numerals = { "I", "II", "III", "IV", "V", "VI", "VII" };
        static readonly string[] Longest = { "VII", "VI", "V", "IV", "III", "II", "I" };

        /// <summary>Whether a token is a numeral (after any b or # prefix it starts with I or V, either case).</summary>
        public static bool LooksRoman(string token)
        {
            string t = Theory.Normalize(token);
            int i = 0;
            while (i < t.Length && (t[i] == 'b' || t[i] == '#')) i++;
            return i < t.Length && (t[i] is 'I' or 'V' or 'i' or 'v');
        }

        /// <summary>
        /// A numeral as a chord in <paramref name="key"/>. Case gives the triad (V major, ii minor); °, ø and + make it diminished,
        /// half-diminished or augmented; the rest reads as a chord symbol's suffix (V7 is a dominant seventh, Imaj7 a major
        /// seventh). Plain numerals take the key's scale degrees (VI in A minor is F); b and # alter the major scale's degree
        /// (bVII in C is Bb; bVI in A minor is F). vii° in a minor key sits on the leading tone. "/V" makes a secondary chord;
        /// figured bass 64, 65, 43, 42 gives an inversion. "6" is an added sixth, as in chord symbols.
        /// </summary>
        public static ProgressionChord Parse(string numeral, MusicKey key)
        {
            string t = Theory.Normalize(numeral);
            string head = t;
            string? bass = null;
            var k = key;
            int slash = t.IndexOf('/');
            if (slash >= 0)
            {
                string tail = t.Substring(slash + 1);
                head = t.Substring(0, slash);
                if (tail.Length > 0 && LooksRoman(tail))
                {
                    var target = Parse(tail, key);
                    k = new MusicKey(target.Chord.Root, target.Chord.IsMinor ? Scale.Minor : Scale.Major);
                }
                else if (tail.Length > 0 && NoteName.LetterNames.IndexOf(char.ToUpperInvariant(tail[0])) >= 0) bass = tail;
                else throw new ToolError($"\"{numeral}\": after \"/\" comes a numeral (V/V, vii°7/ii) or a bass note (I/E).");
            }

            int i = 0, accidental = 0;
            while (i < head.Length && (head[i] == 'b' || head[i] == '#'))
            {
                accidental += head[i] == '#' ? 1 : -1;
                i++;
            }
            string? found = null;
            bool upper = false;
            foreach (var n in Longest)
            {
                if (string.CompareOrdinal(head, i, n, 0, n.Length) == 0) { found = n; upper = true; break; }
                if (string.CompareOrdinal(head, i, n.ToLowerInvariant(), 0, n.Length) == 0) { found = n; break; }
            }
            if (found == null)
                throw new ToolError($"\"{numeral}\" isn't a roman numeral: use I–VII for major chords and i–vii for minor ones, with b or # in front for borrowed roots (bVII), e.g. \"ii7\", \"V7/V\", \"bVII\".");
            i += found.Length;
            int degree = Array.IndexOf(Numerals, found) + 1;

            string quality = upper ? "major" : "minor";
            string rest = head.Substring(i);
            const StringComparison Ord = StringComparison.Ordinal;
            if (rest.StartsWith("°", Ord) || (rest.StartsWith("o", Ord) && !rest.StartsWith("omit", Ord)) || rest.StartsWith("dim", StringComparison.OrdinalIgnoreCase))
            {
                quality = "dim";
                rest = rest.StartsWith("dim", StringComparison.OrdinalIgnoreCase) ? rest.Substring(3) : rest.Substring(1);
            }
            else if (rest.StartsWith("ø", Ord)) { quality = "halfdim"; rest = rest.Substring(1); }
            else if (rest.StartsWith("+", Ord) && (rest.Length == 1 || !char.IsDigit(rest[1]))) { quality = "aug"; rest = rest.Substring(1); }
            else if (rest.StartsWith("aug", StringComparison.OrdinalIgnoreCase)) { quality = "aug"; rest = rest.Substring(3); }

            int inversion = 0;
            switch (rest)
            {
                case "64": inversion = 2; rest = ""; break;
                case "65": inversion = 1; rest = "7"; break;
                case "43": inversion = 2; rest = "7"; break;
                case "42": inversion = 3; rest = "7"; break;
            }

            var baseScale = k.Scale.IsHeptatonic ? k.Scale : k.Scale.HasMinorThird ? Scale.Minor : Scale.Major;
            NoteName root;
            if (accidental != 0)
            {
                var major = new MusicKey(k.Tonic, Scale.Major).Notes[degree - 1];
                root = new NoteName(major.Letter, major.Accidental + accidental);
            }
            else
            {
                root = new MusicKey(k.Tonic, baseScale).Notes[degree - 1];
                if (degree == 7 && baseScale == Scale.Minor && quality is "dim" or "halfdim") root = new NoteName(root.Letter, root.Accidental + 1);
            }

            string suffix = quality switch
            {
                "minor" => "m" + rest,
                "dim" => rest == "7" ? "dim7" : "dim" + rest,
                "halfdim" => rest is "" or "7" ? "m7b5" : rest == "9" ? "m9b5" : "m7b5" + rest,
                "aug" => "aug" + rest,
                _ => rest,
            };
            string symbol = $"{root}{suffix}" + (bass != null ? "/" + bass : "");
            if (!Chord.TryParse(symbol, out var chord, out var error))
                throw new ToolError($"\"{numeral}\" in {key}: {error}");
            if (inversion >= chord!.Tones.Count) inversion = 0;
            string display = inversion > 0 ? $"{symbol}/{chord.ToneNames[inversion]}" : symbol;
            return new ProgressionChord(numeral, numeral, display, chord, inversion);
        }

        /// <summary>Splits a progression at spaces, commas, bars, en and em dashes, and hyphens between chords ("ii7-V7-I").</summary>
        public static List<string> Split(string progression)
        {
            var tokens = new List<string>();
            var sb = new StringBuilder();
            string t = progression ?? "";
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                bool separator = char.IsWhiteSpace(c) || c is ',' or '|' or '–' or '—' or ';' or '→';
                if (c == '-' && sb.Length > 0 && i + 1 < t.Length && "IViv#b♭♯ABCDEFG".IndexOf(t[i + 1]) >= 0) separator = true;
                if (!separator) { sb.Append(c); continue; }
                if (sb.Length > 0) tokens.Add(sb.ToString());
                sb.Clear();
            }
            if (sb.Length > 0) tokens.Add(sb.ToString());
            return tokens;
        }

        /// <summary>A progression of numerals (needs a key) or chord symbols, or both.</summary>
        public static List<ProgressionChord> ParseProgression(string progression, MusicKey? key)
        {
            var tokens = Split(progression);
            if (tokens.Count == 0) throw new ToolError("The progression is empty: give chords like \"ii7–V7–Imaj7\" (with a key) or \"Am F C G\".");
            var list = new List<ProgressionChord>();
            foreach (var token in tokens)
            {
                if (LooksRoman(token))
                {
                    if (key == null) throw new ToolError($"\"{token}\" is a roman numeral, which needs a key: add key=\"C major\" (or \"A minor\", \"D dorian\").");
                    list.Add(Parse(token, key));
                }
                else
                {
                    var chord = Chord.Parse(token);
                    list.Add(new ProgressionChord(token, null, chord.Symbol, chord, 0));
                }
            }
            return list;
        }
    }

    /// <summary>A melody note for fitting: pitch, weight (its length; 1 when unknown) and where it is, if known.</summary>
    public readonly record struct FitNote(int Pitch, double Weight, double? Start = null, double? Length = null);

    /// <summary>A key's fit to some notes: share of time in its scale and the Krumhansl–Kessler profile correlation.</summary>
    public sealed record KeyFit(MusicKey Key, double Share, double Correlation);

    /// <summary>How well notes fit a key, and which keys fit them best.</summary>
    public static class MelodyFit
    {
        // Krumhansl & Kessler (1982) key profiles, tonic first.
        static readonly double[] MajorProfile = { 6.35, 2.23, 3.48, 2.33, 4.38, 4.09, 2.52, 5.19, 2.39, 3.66, 2.29, 2.88 };
        static readonly double[] MinorProfile = { 6.33, 2.68, 3.52, 5.38, 2.60, 3.53, 2.54, 4.75, 3.98, 2.69, 3.34, 3.17 };
        // The usual spelling of each tonic.
        static readonly string[] MajorTonics = { "C", "Db", "D", "Eb", "E", "F", "F#", "G", "Ab", "A", "Bb", "B" };
        static readonly string[] MinorTonics = { "C", "C#", "D", "Eb", "E", "F", "F#", "G", "G#", "A", "Bb", "B" };

        /// <summary>Weight per pitch class.</summary>
        public static double[] Profile(IEnumerable<FitNote> notes)
        {
            var p = new double[12];
            foreach (var n in notes) p[Theory.Mod(n.Pitch, 12)] += Math.Max(0, n.Weight);
            return p;
        }

        /// <summary>The share (0–1) of the notes' weight that is in the key's scale.</summary>
        public static double Share(double[] profile, MusicKey key)
        {
            double total = profile.Sum();
            if (total <= 0) return 0;
            double inKey = 0;
            for (int pc = 0; pc < 12; pc++)
                if (key.Contains(pc)) inKey += profile[pc];
            return inKey / total;
        }

        /// <summary>Pearson correlation of the profile with the key's major or minor Krumhansl–Kessler profile (−1 … 1).</summary>
        public static double Correlation(double[] profile, MusicKey key)
        {
            var reference = key.Scale.HasMinorThird ? MinorProfile : MajorProfile;
            var r = new double[12];
            for (int pc = 0; pc < 12; pc++) r[pc] = reference[Theory.Mod(pc - key.Tonic.PitchClass, 12)];
            double ma = profile.Average(), mb = r.Average(), sab = 0, saa = 0, sbb = 0;
            for (int i = 0; i < 12; i++)
            {
                sab += (profile[i] - ma) * (r[i] - mb);
                saa += (profile[i] - ma) * (profile[i] - ma);
                sbb += (r[i] - mb) * (r[i] - mb);
            }
            return saa <= 0 || sbb <= 0 ? 0 : sab / Math.Sqrt(saa * sbb);
        }

        /// <summary>
        /// The best-fitting major and minor keys (a minor key is tried as harmonic minor too, and listed that way when that
        /// fits more): highest share of time in the key first, then the highest profile correlation.
        /// </summary>
        public static List<KeyFit> Rank(IEnumerable<FitNote> notes, int count = 5)
        {
            var profile = Profile(notes);
            var fits = new List<KeyFit>();
            for (int pc = 0; pc < 12; pc++)
            {
                fits.Add(Fit(profile, MusicKey.Parse(MajorTonics[pc] + " major")));
                var minor = Fit(profile, MusicKey.Parse(MinorTonics[pc] + " minor"));
                var harmonic = Fit(profile, MusicKey.Parse(MinorTonics[pc] + " harmonic minor"));
                fits.Add(harmonic.Share > minor.Share + 1e-9 ? harmonic : minor);
            }
            return fits.OrderByDescending(f => Math.Round(f.Share, 9)).ThenByDescending(f => f.Correlation).Take(count).ToList();
        }

        static KeyFit Fit(double[] profile, MusicKey key) => new(key, Share(profile, key), Correlation(profile, key));
    }
}
