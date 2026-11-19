// Fork (docs/specs/pitch.md section 9.3): Basic Pitch's note creation (a port of its note_creation.py) on activations built by hand, the model on
// synthetic chords, the agreement with the Python reference on a recorded set of notes, and cancelling a run.
using System.Text.Json;
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using static ReBuzz.SessionTests.Convert.ConvertSignals;

namespace ReBuzz.SessionTests.Convert;

[TestFixture]
public class ConvertHarmonyTests
{
    const int Keys = BasicPitchNotes.Keys;

    /// <summary>Activations of <paramref name="frames"/> frames: a note on key k from frame a to frame b with an onset peak at a.</summary>
    static BasicPitchNotes.Output Activations(int frames, params (int Key, int From, int To, float Level)[] notes)
    {
        var o = new BasicPitchNotes.Output
        {
            Frames = frames,
            Note = new float[frames * Keys], Onset = new float[frames * Keys], Contour = new float[frames * BasicPitchNotes.ContourBins],
        };
        foreach (var (k, a, b, level) in notes)
        {
            for (int t = a; t < b; t++) o.Note[t * Keys + k] = level;
            o.Onset[a * Keys + k] = 0.9f;
            if (a > 0) o.Onset[(a - 1) * Keys + k] = 0.3f;
            if (a + 1 < frames) o.Onset[(a + 1) * Keys + k] = 0.3f;
        }
        return o;
    }

    [Test]
    public void NoteCreationFindsNotesLikeTheReference()
    {
        var o = Activations(200, (39, 10, 60, 0.8f), (43, 10, 60, 0.7f), (46, 100, 160, 0.6f));
        var notes = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings());
        Assert.That(notes.OrderBy(n => n.Pitch).Select(n => (n.Start, n.End, n.Pitch)),
            Is.EqualTo(new[] { (10, 60, 39 + 21), (10, 60, 43 + 21), (100, 160, 46 + 21) }));
        Assert.That(notes.First(n => n.Pitch == 60).Amplitude, Is.EqualTo(0.8).Within(0.001));
    }

    [Test]
    public void ANoteShorterThanTheMinimumIsDropped()
    {
        var o = Activations(120, (30, 10, 18, 0.8f), (34, 40, 80, 0.8f));            // 8 frames (93 ms) against 11
        var notes = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings());
        Assert.That(notes.Select(n => n.Pitch), Is.EqualTo(new[] { 34 + 21 }));
        var loose = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings { MinNoteFrames = 5 });
        Assert.That(loose.Count, Is.EqualTo(2));
    }

    [Test]
    public void ASustainWithoutAnOnsetIsFoundByTheMelodiaTrick()
    {
        var o = Activations(150, (40, 20, 60, 0.8f));
        for (int t = 90; t < 130; t++) o.Note[t * Keys + 50] = 0.7f;                 // sounding, but its onset was missed
        var on = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings());
        Assert.That(on.Select(n => n.Pitch), Is.EquivalentTo(new[] { 61, 71 }));
        var neither = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings { InferOnsets = false, MelodiaTrick = false });
        Assert.That(neither.Select(n => n.Pitch), Is.EquivalentTo(new[] { 61 }), "with no inferred onset and no melodia trick, a note without an onset is not found");
        var melodia = BasicPitchNotes.FindNotes(o, new BasicPitchNotes.Settings { InferOnsets = false });
        Assert.That(melodia.Select(n => n.Pitch), Is.EquivalentTo(new[] { 61, 71 }));
    }

    [Test]
    public void ThresholdsAndTheFrequencyRangeSelectNotes()
    {
        var o = Activations(100, (30, 10, 50, 0.25f), (50, 10, 50, 0.8f));
        var s = new BasicPitchNotes.Settings();
        Assert.That(BasicPitchNotes.FindNotes(o, s).Select(n => n.Pitch), Is.EqualTo(new[] { 71 }), "0.25 is under the 0.3 frame threshold");
        s.FrameThreshold = 0.2;
        Assert.That(BasicPitchNotes.FindNotes(o, s).Count, Is.EqualTo(2));
        s.MinHz = 220;                                                               // A3 = key 30 + 21 = 51 is just above; 51 = D#3 below it
        Assert.That(BasicPitchNotes.FindNotes(o, s).Select(n => n.Pitch), Is.EqualTo(new[] { 71 }));
    }

    [Test]
    public void SilenceHasNoNotes() =>
        Assert.That(BasicPitchNotes.FindNotes(Activations(100), new BasicPitchNotes.Settings()), Is.Empty);

    [Test]
    public void TheWindowsCoverTheAudioWithTheReferencesHop()
    {
        var audio = new float[22050 * 5];
        var windows = BasicPitchNotes.Windows(audio).ToList();
        Assert.That(windows.Count, Is.EqualTo(BasicPitchNotes.WindowCount(audio.Length)));
        Assert.That(windows.All(w => w.Length == BasicPitchNotes.WindowSamples));
        Assert.That(BasicPitchNotes.FrameTime(0), Is.EqualTo(BasicPitchNotes.MagicAlignmentOffset).Within(1e-9));
        Assert.That(BasicPitchNotes.FrameTime(142) - BasicPitchNotes.FrameTime(141), Is.EqualTo(256 / 22050.0).Within(0.01), "the next window's first kept frame follows the last one of the window before");
    }

    [Test]
    public void AChordProgressionGivesItsNotes()
    {
        var chords = new[] { (0.3, 1.6, new[] { 60, 64, 67 }), (2.2, 1.6, new[] { 57, 60, 64 }), (4.1, 1.6, new[] { 55, 59, 62, 67 }) };
        var got = AudioConverter.Run(Chords(chords, 6.2), 22050, new ConvertOptions { Mode = ConvertMode.Harmony }).Notes;
        foreach (var (at, length, pitches) in chords)
            foreach (int p in pitches)
            {
                var n = got.Where(g => g.Pitch == p && Math.Abs(g.Start - at) < 0.1).ToList();
                Assert.That(n.Count, Is.EqualTo(1), $"note {MidiNames.Of(p)} at {at}");
                Assert.That(n[0].Start, Is.EqualTo(at).Within(0.05));
                Assert.That(n[0].End, Is.EqualTo(at + length).Within(0.25), $"{MidiNames.Of(p)} end");
            }
    }

    /// <summary>
    /// The same chords through the upstream Python package (tools/convert/basic_pitch_reference.py, on the model file embedded here): the C# port agrees
    /// note for note. To regenerate: write ChordsForReference() as a 22.05 kHz WAV, run the script on it, put its notes in Data/convert-chords-reference.json.
    /// </summary>
    [Test]
    public void TheChordsMatchThePythonReference()
    {
        var chords = ChordsForReference();
        var audio = Chords(chords, 8.0);
        for (int i = 0; i < audio.Length; i++) audio[i] = (float)(Math.Round(Math.Clamp(audio[i], -1f, 1f) * 32767) / 32768);      // as the 16-bit WAV the reference read
        var got = AudioConverter.Run(audio, 22050, new ConvertOptions { Mode = ConvertMode.Harmony }).Notes;
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestContext.CurrentContext.TestDirectory, "Convert", "Data", "convert-chords-reference.json")));
        var reference = doc.RootElement.EnumerateArray().Select(e => (S: e[0].GetDouble(), E: e[1].GetDouble(), P: e[2].GetInt32())).ToList();
        Assert.That(got.Count, Is.EqualTo(reference.Count), "the same number of notes");
        foreach (var r in reference)
        {
            var n = got.FirstOrDefault(g => g.Pitch == r.P && Math.Abs(g.Start - r.S) < 0.03);
            Assert.That(n, Is.Not.Null, $"{MidiNames.Of(r.P)} at {r.S:0.000}");
            Assert.That(n!.End, Is.EqualTo(r.E).Within(0.03), $"{MidiNames.Of(r.P)} end");
        }
    }

    internal static (double, double, int[])[] ChordsForReference() => new[]
    {
        (0.3, 1.8, new[] { 48, 60, 64, 67 }), (2.4, 1.8, new[] { 53, 57, 60, 65 }), (4.5, 1.8, new[] { 55, 59, 62, 67 }), (6.5, 1.4, new[] { 48, 52, 55, 60 }),
    };

    [Test]
    public void CancellingStopsARun()
    {
        var audio = Chords(new[] { (0.2, 60.0, new[] { 60, 64 }) }, 62);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => AudioConverter.Run(audio, 22050, new ConvertOptions { Mode = ConvertMode.Harmony }, null, cts.Token));
        using var later = new CancellationTokenSource();
        double last = 0;
        void Progress(double f) { last = f; if (f > 0.1) later.Cancel(); }
        Assert.Throws<OperationCanceledException>(() => AudioConverter.Run(audio, 22050, new ConvertOptions { Mode = ConvertMode.Harmony }, Progress, later.Token));
        Assert.That(last, Is.LessThan(0.9), "it stopped part way");
    }
}
