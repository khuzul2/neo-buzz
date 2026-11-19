// Fork (docs/specs/pitch.md section 9.1): the melody converter on synthetic lines, whose note lists are known exactly.
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using static ReBuzz.SessionTests.Convert.ConvertSignals;

namespace ReBuzz.SessionTests.Convert;

[TestFixture]
public class ConvertMelodyTests
{
    static List<ConvertedNote> Run(float[] x, ConvertOptions? o = null) =>
        AudioConverter.Run(x, Rate, o ?? new ConvertOptions { Mode = ConvertMode.Melody }).Notes;

    static void Expect(IReadOnlyList<ConvertedNote> got, (double At, double Length, int Midi)[] want, double onsetTol = 0.025, double lengthTol = 0.05)
    {
        Assert.That(got.Select(n => n.Pitch), Is.EqualTo(want.Select(w => w.Midi)), "the pitches, in order");
        for (int i = 0; i < want.Length; i++)
        {
            Assert.That(got[i].Start, Is.EqualTo(want[i].At).Within(onsetTol), $"note {i} start");
            Assert.That(got[i].End - got[i].Start, Is.EqualTo(want[i].Length).Within(lengthTol), $"note {i} length");
        }
    }

    [Test]
    public void ASeparatedLineGivesItsNotes()
    {
        var want = new[] { (0.5, 0.5, 60), (1.2, 0.4, 64), (1.7, 0.6, 67), (2.5, 0.3, 67), (3.0, 0.8, 72), (4.0, 0.25, 48), (4.4, 0.5, 55) };
        var got = Run(Line(want.Select(w => (w.Item1, w.Item2, (double)w.Item3)), 5.5));
        Expect(got, want);
    }

    [Test]
    public void LegatoNotesWithoutGapsAreSeparatedByPitch()
    {
        var want = new[] { (0.5, 0.3, 60), (0.8, 0.3, 62), (1.1, 0.3, 64), (1.4, 0.3, 60), (1.7, 0.3, 72) };
        var got = Run(Line(want.Select(w => (w.Item1, w.Item2, (double)w.Item3)), 2.5));
        Expect(got, want, 0.03, 0.06);
    }

    [Test]
    public void RepeatedDecayingPlucksOfOnePitchAreSeparateNotes()
    {
        var want = Enumerable.Range(0, 6).Select(i => (0.5 + i * 0.3, 0.3, 57)).ToArray();
        var got = Run(Line(want.Select(w => (w.Item1, w.Item2, (double)w.Item3)), 3, 0, 8.0));
        Assert.That(got.Count, Is.EqualTo(6));
        Assert.That(got.Select(n => n.Pitch), Is.All.EqualTo(57));
        for (int i = 0; i < 6; i++) Assert.That(got[i].Start, Is.EqualTo(want[i].Item1).Within(0.03));
    }

    [Test]
    public void VibratoUnderTheHysteresisStaysOneNote()
    {
        var got = Run(Line(new[] { (0.5, 1.5, 69.0) }, 2.5, vibrato: 0.35));
        Assert.That(got.Count, Is.EqualTo(1));
        Assert.That(got[0].Pitch, Is.EqualTo(69));
        Assert.That(got[0].End - got[0].Start, Is.EqualTo(1.5).Within(0.06));
    }

    [Test]
    public void ASlideBecomesBendsWhenAsked()
    {
        var x = Glide(0.5, 1.0, 60, 61.6, 2);
        var plain = Run(x);
        Assert.That(plain.Count, Is.GreaterThanOrEqualTo(1));
        var bent = Run(x, new ConvertOptions { Mode = ConvertMode.Melody, PitchBends = true });
        Assert.That(bent.Count, Is.EqualTo(1), "one note carries the slide");
        Assert.That(bent[0].Bends, Is.Not.Null.And.Not.Empty);
        double span = bent[0].Bends!.Max(b => b.Cents) - bent[0].Bends!.Min(b => b.Cents);
        Assert.That(span, Is.EqualTo(160).Within(60), "the bends follow the slide (cents)");
    }

    [Test]
    public void ShortBlipsAreDroppedAndLoudnessGivesVelocity()
    {
        var x = Line(new[] { (0.5, 0.03, 64.0), (1.0, 0.5, 60.0) }, 2);
        var got = Run(x);
        Assert.That(got.Select(n => n.Pitch), Is.EqualTo(new[] { 60 }), "a 30 ms blip is not a note");
        var loud = Line(new[] { (0.5, 0.4, 60.0) }, 1.2);
        var quiet = loud.Select(v => v * 0.1f).ToArray();
        var both = new float[(int)(Rate * 2.4)];
        loud.CopyTo(both, 0);
        quiet.CopyTo(both, (int)(Rate * 1.2));
        var two = Run(both);
        Assert.That(two.Count, Is.EqualTo(2));
        Assert.That(two[0].Velocity, Is.GreaterThan(two[1].Velocity + 20), "the quieter note has the lower velocity");
    }

    [Test]
    public void SilenceHasNoNotes()
    {
        Assert.That(Run(new float[Rate * 2]), Is.Empty);
    }

    [Test]
    public void MinNoteLengthIsAnOption()
    {
        var x = Line(new[] { (0.5, 0.09, 64.0), (1.0, 0.5, 60.0) }, 2);
        Assert.That(Run(x).Select(n => n.Pitch), Is.EqualTo(new[] { 64, 60 }), "90 ms is long enough by default");
        Assert.That(Run(x, new ConvertOptions { Mode = ConvertMode.Melody, MinNoteMs = 150 }).Select(n => n.Pitch), Is.EqualTo(new[] { 60 }));
    }
}
