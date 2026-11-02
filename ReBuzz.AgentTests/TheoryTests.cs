using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    /// <summary>Phase A3's music theory (docs/specs/mcp-server.md §5.5): spelling, chords, voicings, numerals, keys, fit, the theory tool.</summary>
    [TestFixture]
    public class TheoryTests
    {
        /// <summary>
        /// Chord symbols and their close root-position pitches with the root in octave 4, spelled. Written out by hand and checked
        /// interval by interval: 13th chords leave out the 11th unless the third is minor, 7alt is 1 3 b7 b9 #9 b13, and a slash
        /// bass sits just below the root.
        /// </summary>
        static readonly (string Symbol, string Pitches)[] ChordBook =
        {
            ("C", "C4 E4 G4"),
            ("Cmaj", "C4 E4 G4"),
            ("Cm", "C4 Eb4 G4"),
            ("C-", "C4 Eb4 G4"),
            ("Cdim", "C4 Eb4 Gb4"),
            ("Caug", "C4 E4 G#4"),
            ("C+", "C4 E4 G#4"),
            ("Csus2", "C4 D4 G4"),
            ("Csus4", "C4 F4 G4"),
            ("Csus", "C4 F4 G4"),
            ("C5", "C4 G4"),
            ("C6", "C4 E4 G4 A4"),
            ("Cm6", "C4 Eb4 G4 A4"),
            ("C6/9", "C4 E4 G4 A4 D5"),
            ("C69", "C4 E4 G4 A4 D5"),
            ("Cm6/9", "C4 Eb4 G4 A4 D5"),
            ("C7", "C4 E4 G4 Bb4"),
            ("Cmaj7", "C4 E4 G4 B4"),
            ("CM7", "C4 E4 G4 B4"),
            ("CΔ7", "C4 E4 G4 B4"),
            ("CΔ", "C4 E4 G4 B4"),
            ("Cm7", "C4 Eb4 G4 Bb4"),
            ("Cmin7", "C4 Eb4 G4 Bb4"),
            ("C-7", "C4 Eb4 G4 Bb4"),
            ("Cm7b5", "C4 Eb4 Gb4 Bb4"),
            ("Cø7", "C4 Eb4 Gb4 Bb4"),
            ("Cdim7", "C4 Eb4 Gb4 Bbb4"),
            ("C°7", "C4 Eb4 Gb4 Bbb4"),
            ("Cm(maj7)", "C4 Eb4 G4 B4"),
            ("CmMaj7", "C4 Eb4 G4 B4"),
            ("C7sus4", "C4 F4 G4 Bb4"),
            ("C7sus2", "C4 D4 G4 Bb4"),
            ("C9", "C4 E4 G4 Bb4 D5"),
            ("Cmaj9", "C4 E4 G4 B4 D5"),
            ("Cm9", "C4 Eb4 G4 Bb4 D5"),
            ("C11", "C4 E4 G4 Bb4 D5 F5"),
            ("Cm11", "C4 Eb4 G4 Bb4 D5 F5"),
            ("C13", "C4 E4 G4 Bb4 D5 A5"),
            ("Cmaj13", "C4 E4 G4 B4 D5 A5"),
            ("Cm13", "C4 Eb4 G4 Bb4 D5 F5 A5"),
            ("Cadd9", "C4 E4 G4 D5"),
            ("C(add9)", "C4 E4 G4 D5"),
            ("Cmadd9", "C4 Eb4 G4 D5"),
            ("Cadd11", "C4 E4 G4 F5"),
            ("C7b9", "C4 E4 G4 Bb4 Db5"),
            ("C7#9", "C4 E4 G4 Bb4 D#5"),
            ("C7#11", "C4 E4 G4 Bb4 F#5"),
            ("C7b13", "C4 E4 G4 Bb4 Ab5"),
            ("C9#11", "C4 E4 G4 Bb4 D5 F#5"),
            ("C13b9", "C4 E4 G4 Bb4 Db5 A5"),
            ("C13#11", "C4 E4 G4 Bb4 D5 F#5 A5"),
            ("C7b9b13", "C4 E4 G4 Bb4 Db5 Ab5"),
            ("C7(b9,#11)", "C4 E4 G4 Bb4 Db5 F#5"),
            ("C7#5", "C4 E4 G#4 Bb4"),
            ("C7+", "C4 E4 G#4 Bb4"),
            ("Caug7", "C4 E4 G#4 Bb4"),
            ("C7b5", "C4 E4 Gb4 Bb4"),
            ("Cmaj7#11", "C4 E4 G4 B4 F#5"),
            ("Cmaj7#5", "C4 E4 G#4 B4"),
            ("Cmaj7(no5)", "C4 E4 B4"),
            ("C7alt", "C4 E4 Bb4 Db5 D#5 Ab5"),
            ("C9sus4", "C4 F4 G4 Bb4 D5"),
            ("C/E", "E3 C4 E4 G4"),
            ("Cmaj7/G", "G3 C4 E4 G4 B4"),
            ("C7/Bb", "Bb3 C4 E4 G4 Bb4"),
            ("Am9/E", "E4 A4 C5 E5 G5 B5"),
            ("G7/F", "F4 G4 B4 D5 F5"),
            ("D/F#", "F#3 D4 F#4 A4"),
            ("F/C", "C4 F4 A4 C5"),
            ("Bb", "Bb4 D5 F5"),
            ("B♭7", "Bb4 D5 F5 Ab5"),
            ("Bbsus4", "Bb4 Eb5 F5"),
            ("Bbm7b5", "Bb4 Db5 Fb5 Ab5"),
            ("Ebmaj7", "Eb4 G4 Bb4 D5"),
            ("Eb6", "Eb4 G4 Bb4 C5"),
            ("Ebm9", "Eb4 Gb4 Bb4 Db5 F5"),
            ("F#m7", "F#4 A4 C#5 E5"),
            ("F#7b9", "F#4 A#4 C#5 E5 G5"),
            ("F#ø7", "F#4 A4 C5 E5"),
            ("C#dim7", "C#4 E4 G4 Bb4"),
            ("C#m", "C#4 E4 G#4"),
            ("C♯m7", "C#4 E4 G#4 B4"),
            ("Abm", "Ab4 Cb5 Eb5"),
            ("Ab13", "Ab4 C5 Eb5 Gb5 Bb5 F6"),
            ("Gb7", "Gb4 Bb4 Db5 Fb5"),
            ("E7#9", "E4 G#4 B4 D5 F##5"),
            ("Dm7", "D4 F4 A4 C5"),
            ("G13", "G4 B4 D5 F5 A5 E6"),
            ("A7sus4", "A4 D5 E5 G5"),
            ("Asus2", "A4 B4 E5"),
            ("Fmaj9", "F4 A4 C5 E5 G5"),
            ("Fm", "F4 Ab4 C5"),
            ("Bmaj7", "B4 D#5 F#5 A#5"),
            ("Db9", "Db4 F4 Ab4 Cb5 Eb5"),
            ("Dbmaj7", "Db4 F4 Ab4 C5"),
            ("Gm(maj7)", "G4 Bb4 D5 F#5"),
            ("Cb", "Cb4 Eb4 Gb4"),
        };

        static string Names(IEnumerable<SpelledPitch> pitches) => string.Join(" ", pitches.Select(p => p.Name));

        [Test]
        public void ChordSymbolsSpellAndSoundRight()
        {
            Assert.That(ChordBook.Length, Is.AtLeast(60));
            var failures = new List<string>();
            foreach (var (symbol, expected) in ChordBook)
            {
                if (!Chord.TryParse(symbol, out var chord, out var error))
                {
                    failures.Add($"{symbol}: {error}");
                    continue;
                }
                var voiced = Voicer.Voice(chord!);
                string names = Names(voiced);
                // The MIDI numbers come from Pitch.Parse of the expected names: an independent reading of each spelling.
                var midi = expected.Split(' ').Select(Pitch.Parse).ToList();
                if (names != expected || !voiced.Select(p => p.Midi).SequenceEqual(midi))
                    failures.Add($"{symbol}: got {names} ({string.Join(" ", voiced.Select(p => p.Midi))}), expected {expected} ({string.Join(" ", midi)})");
            }
            Assert.That(failures, Is.Empty);
        }

        [Test]
        public void ChordIntervalsAndToneNames()
        {
            var am9 = Chord.Parse("Am9/E");
            Assert.That(am9.Intervals, Is.EqualTo("1 b3 5 b7 9"));
            Assert.That(am9.Bass.ToString(), Is.EqualTo("E"));
            Assert.That(string.Join(" ", am9.ToneNames), Is.EqualTo("A C E G B"));
            Assert.That(Chord.Parse("Cdim7").Intervals, Is.EqualTo("1 b3 b5 bb7"));
            Assert.That(Chord.Parse("C7alt").Intervals, Is.EqualTo("1 3 b7 b9 #9 b13"));
            Assert.That(Chord.Parse("C13#11").Intervals, Is.EqualTo("1 3 5 b7 9 #11 13"));
            Assert.That(Chord.Parse("Cm7").IsMinor, Is.True);
        }

        [Test]
        public void BadChordSymbolsSayWhatToWrite()
        {
            var e = Assert.Throws<ToolError>(() => Chord.Parse("Cmaj8"));
            Assert.That(e!.Message, Does.Contain("Did you mean"));
            Assert.That(e.Message, Does.Contain("\"Cmaj7\""));
            Assert.That(Assert.Throws<ToolError>(() => Chord.Parse("H7"))!.Message, Does.Contain("root note A–G"));
            Assert.That(Assert.Throws<ToolError>(() => Chord.Parse("Am/X"))!.Message, Does.Contain("bass note"));
            Assert.That(Chord.TryParse("C2", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("Chord symbols look like"));
        }

        [Test]
        public void VoicingsInversionsAndRegister()
        {
            var cmaj7 = Chord.Parse("Cmaj7");
            Assert.That(Names(Voicer.Voice(cmaj7, Voicing.Close)), Is.EqualTo("C4 E4 G4 B4"));
            Assert.That(Names(Voicer.Voice(cmaj7, Voicing.Open)), Is.EqualTo("C4 G4 E5 B5"));
            Assert.That(Names(Voicer.Voice(cmaj7, Voicing.Drop2)), Is.EqualTo("G3 C4 E4 B4"));
            Assert.That(Names(Voicer.Voice(cmaj7, Voicing.Drop3)), Is.EqualTo("E3 C4 G4 B4"));
            Assert.That(Names(Voicer.Voice(cmaj7, Voicing.Drop2, inversion: 1)), Is.EqualTo("B3 E4 G4 C5"));

            var c = Chord.Parse("C");
            Assert.That(Names(Voicer.Voice(c, inversion: 1)), Is.EqualTo("E4 G4 C5"));
            Assert.That(Names(Voicer.Voice(c, inversion: 2)), Is.EqualTo("G4 C5 E5"));
            Assert.That(Names(Voicer.Voice(c, Voicing.Open)), Is.EqualTo("C4 G4 E5"));
            Assert.That(Names(Voicer.Voice(c, octave: 2)), Is.EqualTo("C2 E2 G2"));
            Assert.That(Names(Voicer.Place(Voicer.Voice(c), Pitch.Parse("E3"))), Is.EqualTo("C4 E4 G4"));
            Assert.That(Names(Voicer.Place(Voicer.Voice(c), Pitch.Parse("C#4"))), Is.EqualTo("C5 E5 G5"));
            Assert.That(Names(Voicer.Place(Voicer.Voice(c), Pitch.Parse("C2"))), Is.EqualTo("C2 E2 G2"));
            Assert.That(Assert.Throws<ToolError>(() => Voicer.Voice(c, inversion: 3))!.Message, Does.Contain("inversion is 0–2"));
            Assert.That(Voicer.ParseVoicing("Drop 2"), Is.EqualTo(Voicing.Drop2));
            Assert.That(Assert.Throws<ToolError>(() => Voicer.ParseVoicing("drop4"))!.Message, Does.Contain("Did you mean"));
        }

        [Test]
        public void VoiceLeadingPicksTheNearestInversion()
        {
            var c = Voicer.Voice(Chord.Parse("C")).Select(p => p.Midi).ToList();
            var f = Voicer.VoiceLead(Chord.Parse("F"), c);
            Assert.That(Names(f), Is.EqualTo("C4 F4 A4"));
            Assert.That(Voicer.Movement(c, f.Select(p => p.Midi).ToList()), Is.EqualTo(3));
            var g7 = Voicer.VoiceLead(Chord.Parse("G7"), Voicer.Voice(Chord.Parse("Dm7")).Select(p => p.Midi).ToList());
            Assert.That(Voicer.Movement(new[] { 62, 65, 69, 72 }, g7.Select(p => p.Midi).ToList()), Is.LessThan(4));

            // A whole progression moves far less voice-led than in root position.
            var key = MusicKey.Parse("C major");
            var chords = Roman.ParseProgression("I vi IV V I ii V7 I", key);
            int Total(bool lead)
            {
                List<int>? before = null;
                int sum = 0;
                foreach (var pc in chords)
                {
                    var v = (lead && before != null ? Voicer.VoiceLead(pc.Chord, before) : Voicer.Voice(pc.Chord)).Select(p => p.Midi).ToList();
                    if (before != null) sum += Voicer.Movement(before, v);
                    before = v;
                }
                return sum;
            }
            int led = Total(true), root = Total(false);
            Assert.That(led, Is.LessThan(root / 2), $"voice-led {led}, root position {root}");
        }

        static string Tones(ProgressionChord pc) => string.Join(" ", pc.Chord.ToneNames);

        static void Progression(string key, string progression, params string[] expected)
        {
            var chords = Roman.ParseProgression(progression, MusicKey.Parse(key));
            Assert.That(chords.Select(c => $"{c.Symbol}: {Tones(c)}").ToList(), Is.EqualTo(expected), $"{progression} in {key}");
        }

        [Test]
        public void RomanNumeralsInKeys()
        {
            Progression("C major", "ii7–V7–Imaj7", "Dm7: D F A C", "G7: G B D F", "Cmaj7: C E G B");
            Progression("A minor", "i–VI–III–VII", "Am: A C E", "F: F A C", "C: C E G", "G: G B D");
            Progression("C major", "I-bVII-IV", "C: C E G", "Bb: Bb D F", "F: F A C");
            Progression("C major", "V7/V V7 I", "D7: D F# A C", "G7: G B D F", "C: C E G");
            Progression("F major", "I V/vi vi", "F: F A C", "A: A C# E", "Dm: D F A");
            Progression("Eb major", "ii-V-I", "Fm: F Ab C", "Bb: Bb D F", "Eb: Eb G Bb");
            Progression("A minor", "vii°7 i", "G#dim7: G# B D F", "Am: A C E");
            Progression("A minor", "iv V7 i", "Dm: D F A", "E7: E G# B D", "Am: A C E");
            Progression("D dorian", "i IV", "Dm: D F A", "G: G B D");
            Progression("C minor", "i bVI bIII bVII", "Cm: C Eb G", "Ab: Ab C Eb", "Eb: Eb G Bb", "Bb: Bb D F");
            Progression("G major", "IV iv I", "C: C E G", "Cm: C Eb G", "G: G B D");
            Progression("E major", "bII", "F: F A C");
            Progression("Bb major", "viiø7", "Am7b5: A C Eb G");
            Progression("C major", "vii° V7/ii", "Bdim: B D F", "A7: A C# E G");
            Progression("C major", "vii°7/V", "F#dim7: F# A C Eb");
            Progression("F# major", "I IV V", "F#: F# A# C#", "B: B D# F#", "C#: C# E# G#");
            Progression("Gb major", "ii7", "Abm7: Ab Cb Eb Gb");
            Progression("C major", "V65 I64", "G7/B: G B D F", "C/G: C E G");
            Progression("C major", "Am F C G", "Am: A C E", "F: F A C", "C: C E G", "G: G B D");

            var key = MusicKey.Parse("C major");
            var v65 = Roman.Parse("V65", key);
            Assert.That(v65.Inversion, Is.EqualTo(1));
            Assert.That(Names(Voicer.Voice(v65.Chord, inversion: v65.Inversion)), Is.EqualTo("B4 D5 F5 G5"));
            Assert.That(Roman.Split("ii7–V7–Imaj7 | C-7 Am-F"), Is.EqualTo(new[] { "ii7", "V7", "Imaj7", "C-7", "Am", "F" }));
            Assert.That(Assert.Throws<ToolError>(() => Roman.ParseProgression("ii V I", null))!.Message, Does.Contain("needs a key"));
            Assert.That(Assert.Throws<ToolError>(() => Roman.Parse("iix", key))!.Message, Does.Contain("Can't read the chord"));
        }

        [Test]
        public void KeysParse()
        {
            void Check(string text, string name, string notes, int signature)
            {
                var k = MusicKey.Parse(text);
                Assert.That(k.Name, Is.EqualTo(name), text);
                Assert.That(string.Join(" ", k.Notes), Is.EqualTo(notes), text);
                Assert.That(k.Signature, Is.EqualTo(signature), text);
            }
            Check("A minor", "A minor", "A B C D E F G", 0);
            Check("F# major", "F# major", "F# G# A# B C# D# E#", 6);
            Check("D dorian", "D dorian", "D E F G A B C", 0);
            Check("Bbm", "Bb minor", "Bb C Db Eb F Gb Ab", -5);
            Check("C", "C major", "C D E F G A B", 0);
            Check("CM", "C major", "C D E F G A B", 0);
            Check("Cm", "C minor", "C D Eb F G Ab Bb", -3);
            Check("e minor", "E minor", "E F# G A B C D", 1);
            Check("Ebm", "Eb minor", "Eb F Gb Ab Bb Cb Db", -6);
            Check("G mixolydian", "G mixolydian", "G A B C D E F", 0);
            Check("F lydian", "F lydian", "F G A B C D E", 0);
            Check("E phrygian", "E phrygian", "E F G A B C D", 0);
            Check("B locrian", "B locrian", "B C D E F G A", 0);
            Check("F# harmonic minor", "F# harmonic minor", "F# G# A B C# D E#", 3);
            Check("E melodic minor", "E melodic minor", "E F# G A B C# D#", 1);
            Check("A minor pentatonic", "A minor pentatonic", "A C D E G", 0);
            Check("C major pentatonic", "C major pentatonic", "C D E G A", 0);
            Check("C blues", "C blues", "C Eb F Gb G Bb", -3);
            Check("C whole-tone", "C whole tone", "C D E F# G# A#", 0);
            Check("A aeolian", "A minor", "A B C D E F G", 0);
            Check("F ionian", "F major", "F G A Bb C D E", -1);
            Check("E♭ major", "Eb major", "Eb F G Ab Bb C D", -3);

            Assert.That(string.Join(" ", MusicKey.Parse("Db major").SignatureAccidentals), Is.EqualTo("Bb Eb Ab Db Gb"));
            Assert.That(string.Join(" ", MusicKey.Parse("E major").SignatureAccidentals), Is.EqualTo("F# C# G# D#"));
            Assert.That(MusicKey.Parse("Eb major").Relative!.Name, Is.EqualTo("C minor"));
            Assert.That(MusicKey.Parse("F# minor").Relative!.Name, Is.EqualTo("A major"));
            Assert.That(MusicKey.Parse("D major").Parallel!.Name, Is.EqualTo("D minor"));

            Assert.That(Assert.Throws<ToolError>(() => MusicKey.Parse("C dorain"))!.Message, Does.Contain("Did you mean \"C dorian\""));
            Assert.That(Assert.Throws<ToolError>(() => MusicKey.Parse("H major"))!.Message, Does.Contain("isn't a key"));
            Assert.That(MusicKey.TryParse("Am7", out _, out var error), Is.False);
            Assert.That(error, Does.Contain("Scales:"));
        }

        [Test]
        public void KeyAwareNamesFollowTheSignature()
        {
            Assert.That(MusicKey.Parse("F major").NameOf(70), Is.EqualTo("Bb4"));
            Assert.That(MusicKey.Parse("B major").NameOf(70), Is.EqualTo("A#4"));
            Assert.That(Pitch.Name(70), Is.EqualTo("A#4"));
            Assert.That(Theory.Name(70, MusicKey.Parse("F major")), Is.EqualTo("Bb4"));
            Assert.That(Theory.Name(70, null), Is.EqualTo("A#4"));
            Assert.That(MusicKey.Parse("Gb major").NameOf(71), Is.EqualTo("Cb5"));
            Assert.That(MusicKey.Parse("F# major").NameOf(65), Is.EqualTo("E#4"));
            // Outside the scale: a natural if there is one, else the signature's direction.
            Assert.That(MusicKey.Parse("D major").NameOf(70), Is.EqualTo("A#4"));
            Assert.That(MusicKey.Parse("D major").NameOf(65), Is.EqualTo("F4"));
            Assert.That(MusicKey.Parse("Eb major").NameOf(64), Is.EqualTo("E4"));
            Assert.That(MusicKey.Parse("Eb major").NameOf(66), Is.EqualTo("Gb4"));
            // Keys without accidentals: b2 b3 #4 b6 b7 from the tonic.
            var c = MusicKey.Parse("C major");
            Assert.That(new[] { 61, 63, 66, 68, 70 }.Select(c.NameOf), Is.EqualTo(new[] { "Db4", "Eb4", "F#4", "Ab4", "Bb4" }));
            var a = MusicKey.Parse("A minor");
            Assert.That(new[] { 61, 63, 66, 68, 70 }.Select(a.NameOf), Is.EqualTo(new[] { "C#4", "D#4", "F#4", "G#4", "Bb4" }));
            Assert.That(string.Join(" ", MusicKey.Parse("F chromatic").Notes), Is.EqualTo("F Gb G Ab A Bb B C Db D Eb E"));
            // Every spelled name reads back as the same MIDI pitch.
            foreach (var k in new[] { "C major", "F# major", "Gb major", "A minor", "Bb minor", "D dorian", "C# major", "Cb major" })
                for (int m = 0; m < 128; m++)
                    Assert.That(Pitch.Parse(MusicKey.Parse(k).NameOf(m)), Is.EqualTo(m), $"{k} {m}");
        }

        [Test]
        public void ScaleDegreesAndStepsInKey()
        {
            var a = MusicKey.Parse("A minor");
            Assert.That(a.DegreeLabel(Pitch.Parse("G#4")), Is.EqualTo("#7"));
            Assert.That(a.Degree(Pitch.Parse("G#4")), Is.Null);
            Assert.That(a.DegreeLabel(Pitch.Parse("C5")), Is.EqualTo("3"));
            Assert.That(a.Degree(Pitch.Parse("C5")), Is.EqualTo(3));
            Assert.That(MusicKey.Parse("C major").DegreeLabel(63), Is.EqualTo("b3"));
            Assert.That(MusicKey.Parse("C major").DegreeLabel(66), Is.EqualTo("#4"));
            Assert.That(MusicKey.Parse("D dorian").DegreeLabel(65, majorReference: true), Is.EqualTo("b3"));
            Assert.That(MusicKey.Parse("A minor pentatonic").DegreeLabel(72), Is.EqualTo("b3"));
            Assert.That(MusicKey.Parse("C blues").DegreeLabel(66), Is.EqualTo("b5"));

            var c = MusicKey.Parse("C major");
            Assert.That(c.Step(60, 2, out bool chromatic), Is.EqualTo(64));
            Assert.That(chromatic, Is.False);
            Assert.That(c.Step(64, 2, out _), Is.EqualTo(67));
            Assert.That(c.Step(71, 1, out _), Is.EqualTo(72));
            Assert.That(c.Step(60, -1, out _), Is.EqualTo(59));
            Assert.That(c.Step(60, -8, out _), Is.EqualTo(47));
            Assert.That(c.Step(61, 1, out chromatic), Is.EqualTo(63));
            Assert.That(chromatic, Is.True);
            Assert.That(a.Step(69, 7, out _), Is.EqualTo(81));
            Assert.That(MusicKey.Parse("A minor pentatonic").Step(69, 1, out _), Is.EqualTo(72));
            Assert.That(MusicKey.Parse("A minor pentatonic").Step(69, -1, out _), Is.EqualTo(67));
        }

        [Test]
        public void DiatonicChords()
        {
            var c = MusicKey.Parse("C major");
            Assert.That(c.DiatonicChords(false).Select(d => $"{d.Numeral} {d.Symbol}"),
                Is.EqualTo(new[] { "I C", "ii Dm", "iii Em", "IV F", "V G", "vi Am", "vii° Bdim" }));
            Assert.That(c.DiatonicChords(true).Select(d => $"{d.Numeral} {d.Symbol}"),
                Is.EqualTo(new[] { "Imaj7 Cmaj7", "ii7 Dm7", "iii7 Em7", "IVmaj7 Fmaj7", "V7 G7", "vi7 Am7", "viiø7 Bm7b5" }));
            var h = MusicKey.Parse("A harmonic minor");
            Assert.That(h.DiatonicChords(false).Select(d => $"{d.Numeral} {d.Symbol}"),
                Is.EqualTo(new[] { "i Am", "ii° Bdim", "III+ Caug", "iv Dm", "V E", "VI F", "vii° G#dim" }));
            Assert.That(h.DiatonicChords(true)[0].Symbol, Is.EqualTo("Am(maj7)"));
            Assert.That(h.DiatonicChords(true)[6].Symbol, Is.EqualTo("G#dim7"));
            foreach (var d in h.DiatonicChords(true).Concat(c.DiatonicChords(true)))
                Assert.That(string.Join(" ", Chord.Parse(d.Symbol).ToneNames), Is.EqualTo(string.Join(" ", d.Tones)), d.Symbol);
            Assert.That(MusicKey.Parse("A blues").DiatonicChords(false), Is.Empty);
        }

        [Test]
        public void MelodyFitFindsTheKey()
        {
            static FitNote N(string pitch, double length = 1) => new(Pitch.Parse(pitch), length, null, length);
            var cScale = new[] { "C4", "D4", "E4", "F4", "G4", "A4", "B4", "C5", "G4", "E4", "C4" }.Select(p => N(p)).ToList();
            var best = MelodyFit.Rank(cScale);
            Assert.That(best[0].Key.Name, Is.EqualTo("C major"));
            Assert.That(best[0].Share, Is.EqualTo(1).Within(1e-9));
            Assert.That(best.Count, Is.EqualTo(5));

            var aHarmonic = new[] { "A3", "B3", "C4", "D4", "E4", "F4", "G#4", "A4", "E4", "A3" }.Select(p => N(p)).ToList();
            Assert.That(MelodyFit.Rank(aHarmonic)[0].Key.Name, Is.EqualTo("A harmonic minor"));

            var withFSharp = new List<FitNote> { N("C4", 3), N("F#4", 1) };
            var profile = MelodyFit.Profile(withFSharp);
            Assert.That(MelodyFit.Share(profile, MusicKey.Parse("C major")), Is.EqualTo(0.75).Within(1e-9));
            Assert.That(MelodyFit.Share(profile, MusicKey.Parse("G major")), Is.EqualTo(1).Within(1e-9));
            Assert.That(MelodyFit.Correlation(MelodyFit.Profile(cScale), MusicKey.Parse("C major")),
                Is.GreaterThan(MelodyFit.Correlation(MelodyFit.Profile(cScale), MusicKey.Parse("F# major"))));
        }

        // ---- the tool ----

        static ToolDef Tool()
        {
            var tools = new ToolRegistry();
            CompositionTools.Register(tools);
            return tools.Find("theory")!;
        }

        static JsonObject Call(string json)
        {
            var tool = Tool();
            var args = JsonNode.Parse(json)!.AsObject();
            var errors = new List<string>();
            Schema.Validate(tool.Input, args, "", errors);
            Assert.That(errors, Is.Empty, "input schema accepts " + json);
            var result = tool.Handler(new ToolContext("theory", args, CancellationToken.None)).GetAwaiter().GetResult();
            Assert.That(result.IsError, Is.False);
            Assert.That(result.Summary, Is.Not.EqualTo(""));
            Schema.Validate(tool.Output!, result.Structured, "", errors);
            Assert.That(errors, Is.Empty, "output schema describes the answer to " + json);
            return result.Structured!;
        }

        static string Fails(string json)
        {
            var args = JsonNode.Parse(json)!.AsObject();
            return Assert.Throws<ToolError>(() => CompositionTools.Answer(args))!.Message;
        }

        static string PitchNames(JsonNode? pitches) => string.Join(" ", pitches!.AsArray().Select(p => p!["name"]!.GetValue<string>()));

        [Test]
        public void TheoryToolChord()
        {
            var r = Call("""{"op": "chord", "chord": "Am9/E"}""");
            Assert.That(PitchNames(r["chord"]!["pitches"]), Is.EqualTo("E4 A4 C5 E5 G5 B5"));
            Assert.That(r["chord"]!["pitches"]![0]!["pitch"]!.GetValue<int>(), Is.EqualTo(64));
            Assert.That(r["chord"]!["bass"]!.GetValue<string>(), Is.EqualTo("E"));

            r = Call("""{"op": "chord", "chord": "Bb7", "key": "F major", "octave": 3, "voicing": "drop2"}""");
            Assert.That(PitchNames(r["chord"]!["pitches"]), Is.EqualTo("F3 Bb3 D4 Ab4"));
            Assert.That(r["chord"]!["in_key"]!.GetValue<bool>(), Is.False);
            Assert.That(r["chord"]!["out_of_key"]!.AsArray().Select(x => x!.GetValue<string>()), Is.EqualTo(new[] { "Ab" }));

            r = Call("""{"op": "chord", "chord": "F", "previous": ["C4", "E4", "G4"]}""");
            Assert.That(PitchNames(r["chord"]!["pitches"]), Is.EqualTo("C4 F4 A4"));
            Assert.That(r["chord"]!["movement"]!.GetValue<int>(), Is.EqualTo(3));

            r = Call("""{"op": "chord", "chord": "C", "register": "A2", "inversion": 1}""");
            Assert.That(PitchNames(r["chord"]!["pitches"]), Is.EqualTo("E3 G3 C4"));
        }

        [Test]
        public void TheoryToolProgression()
        {
            var r = Call("""{"op": "progression", "key": "C major", "progression": "ii7–V7–Imaj7", "octave": 3, "beats_per_chord": 2}""");
            var chords = r["chords"]!.AsArray();
            Assert.That(chords.Select(c => c!["symbol"]!.GetValue<string>()), Is.EqualTo(new[] { "Dm7", "G7", "Cmaj7" }));
            Assert.That(chords.Select(c => c!["numeral"]!.GetValue<string>()), Is.EqualTo(new[] { "ii7", "V7", "Imaj7" }));
            Assert.That(PitchNames(chords[0]!["pitches"]), Is.EqualTo("D3 F3 A3 C4"));
            Assert.That(chords[1]!["movement"]!.GetValue<int>(), Is.LessThanOrEqualTo(4));
            var notes = r["notes"]!.AsArray();
            Assert.That(notes.Count, Is.EqualTo(12));
            Assert.That(notes.Select(n => n!["start"]!.GetValue<double>()).Distinct(), Is.EqualTo(new[] { 0.0, 2.0, 4.0 }));
            Assert.That(notes.All(n => n!["length"]!.GetValue<double>() == 2), Is.True);

            r = Call("""{"op": "progression", "key": "A minor", "progression": "i VI III VII", "voice_leading": false}""");
            Assert.That(r["chords"]!.AsArray().Select(c => PitchNames(c!["pitches"])), Is.EqualTo(new[] { "A4 C5 E5", "F4 A4 C5", "C4 E4 G4", "G4 B4 D5" }));
            Assert.That(r["chords"]!.AsArray().All(c => c!["in_key"]!.GetValue<bool>()), Is.True);

            r = Call("""{"op": "progression", "progression": "Am F C G"}""");
            Assert.That(r["chords"]!.AsArray().Count, Is.EqualTo(4));
            Assert.That(r.ContainsKey("key"), Is.False);
        }

        [Test]
        public void TheoryToolScaleKeyDegreeFit()
        {
            var r = Call("""{"op": "scale", "key": "D dorian", "octave": 3}""");
            Assert.That(r["names"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "D", "E", "F", "G", "A", "B", "C" }));
            Assert.That(r["intervals"]!.AsArray().Select(n => n!.GetValue<string>()), Is.EqualTo(new[] { "1", "2", "b3", "4", "5", "6", "b7" }));
            Assert.That(PitchNames(r["pitches"]), Is.EqualTo("D3 E3 F3 G3 A3 B3 C4"));

            r = Call("""{"op": "key", "key": "F major"}""");
            Assert.That(r["key"]!["signature"]!["flats"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(r["key"]!["signature"]!["accidentals"]![0]!.GetValue<string>(), Is.EqualTo("Bb"));
            Assert.That(r["key"]!["relative"]!.GetValue<string>(), Is.EqualTo("D minor"));
            Assert.That(r["triads"]![3]!["symbol"]!.GetValue<string>(), Is.EqualTo("Bb"));
            Assert.That(r["sevenths"]![4]!["symbol"]!.GetValue<string>(), Is.EqualTo("C7"));

            r = Call("""{"op": "degree", "key": "A minor", "pitches": ["G#4", 72, "A4"], "steps": 2}""");
            var d = r["degrees"]!.AsArray();
            Assert.That(d[0]!["label"]!.GetValue<string>(), Is.EqualTo("#7"));
            Assert.That(d[0]!["in_key"]!.GetValue<bool>(), Is.False);
            Assert.That(d[0]!["chromatic"]!.GetValue<bool>(), Is.True);
            Assert.That(d[1]!["degree"]!.GetValue<int>(), Is.EqualTo(3));
            Assert.That(d[1]!["to"]!["name"]!.GetValue<string>(), Is.EqualTo("E5"));
            Assert.That(d[2]!["to"]!["name"]!.GetValue<string>(), Is.EqualTo("C5"));

            r = Call("""{"op": "fit", "key": "C major", "notes": [{"pitch": 60, "start": 0, "length": 3}, {"pitch": "F#4", "name": "F#4", "start": 3, "length": 1, "velocity": 100}]}""");
            Assert.That(r["fit"]!["share"]!.GetValue<double>(), Is.EqualTo(0.75));
            Assert.That(r["fit"]!["out_of_key"]![0]!["name"]!.GetValue<string>(), Is.EqualTo("F#4"));
            Assert.That(r["fit"]!["out_of_key"]![0]!["start"]!.GetValue<double>(), Is.EqualTo(3));
            r = Call("""{"op": "fit", "notes": ["C4", "D4", "E4", "F4", "G4", "A4", "B4", "C5"]}""");
            Assert.That(r["fit"]!["key"]!.GetValue<string>(), Is.EqualTo("C major"));
            Assert.That(r["best"]!.AsArray().Count, Is.EqualTo(5));
        }

        [Test]
        public void TheoryToolErrorsSayWhatToDo()
        {
            Assert.That(Fails("""{"op": "chrod"}"""), Does.Contain("Did you mean \"chord\""));
            Assert.That(Fails("""{"op": "chord"}"""), Does.Contain("needs chord"));
            Assert.That(Fails("""{"op": "scale"}"""), Does.Contain("needs key"));
            Assert.That(Fails("""{"op": "scale", "key": "D dorian", "voicing": "open"}"""), Does.Contain("op=chord or op=progression"));
            Assert.That(Fails("""{"op": "chord", "chord": "C", "octave": 3, "register": 40}"""), Does.Contain("not both"));
            Assert.That(Fails("""{"op": "chord", "chord": "C13", "octave": 9}"""), Does.Contain("lower octave"));
            Assert.That(Fails("""{"op": "progression", "progression": "I IV V"}"""), Does.Contain("needs a key"));
            Assert.That(Fails("""{"op": "degree", "key": "C major", "pitches": [127], "steps": 3}"""), Does.Contain("fewer steps"));
        }
    }
}
