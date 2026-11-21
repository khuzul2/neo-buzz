// Fork (docs/specs/pitch.md section 9.2): the drum converter on a synthetic kit (kick, snare, closed and open hat) with exact hit lists.
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using static ReBuzz.SessionTests.Convert.ConvertSignals;

namespace ReBuzz.SessionTests.Convert;

[TestFixture]
public class ConvertDrumTests
{
    static List<ConvertedNote> Run(float[] x) =>
        AudioConverter.Run(x, Rate, new ConvertOptions { Mode = ConvertMode.Drums }).Notes;

    [Test]
    public void AKickSnareHatLoopGivesItsHits()
    {
        var hits = new List<(string, double)>();
        for (int bar = 0; bar < 2; bar++)
        {
            double t0 = 0.3 + bar * 2.0;
            hits.Add(("kick", t0)); hits.Add(("snare", t0 + 0.5)); hits.Add(("kick", t0 + 1.0)); hits.Add(("snare", t0 + 1.5));
            for (int i = 0; i < 4; i++) hits.Add(("hat", t0 + 0.25 + i * 0.5));                // between the kicks and snares
        }
        var got = Run(Drums(hits, 4.5));
        foreach (string drum in new[] { "kick", "snare", "hat" })
        {
            string label = drum switch { "kick" => DrumConverter.Kick, "snare" => DrumConverter.Snare, _ => DrumConverter.ClosedHat };
            var want = hits.Where(h => h.Item1 == drum).Select(h => h.Item2).ToList();
            var found = got.Where(n => n.Label == label).Select(n => n.Start).ToList();
            Assert.That(found.Count, Is.EqualTo(want.Count), $"{drum}: how many ({string.Join(", ", found.Select(f => f.ToString("0.000")))})");
            for (int i = 0; i < want.Count; i++) Assert.That(found[i], Is.EqualTo(want[i]).Within(0.03), $"{drum} {i}");
        }
        Assert.That(got.Count, Is.EqualTo(hits.Count), "nothing else: " + string.Join(", ", got.Select(n => n.Label + "@" + n.Start.ToString("0.000"))));
    }

    [Test]
    public void HitsAreGeneralMidiNotesWithVelocityFromLevel()
    {
        var x = new float[(int)(Rate * 2.2)];
        Hit(x, "kick", 0.3, 0.9);
        Hit(x, "kick", 1.2, 0.15);
        var got = Run(x);
        Assert.That(got.Select(n => n.Pitch), Is.All.EqualTo(36));
        Assert.That(got.Count, Is.EqualTo(2));
        Assert.That(got[0].Velocity, Is.GreaterThan(got[1].Velocity + 20));
    }

    [Test]
    public void AnOpenHatIsToldFromAClosedOne()
    {
        var x = new float[(int)(Rate * 2.2)];
        Hit(x, "hat", 0.3);
        Hit(x, "open", 1.2);
        var got = Run(x);
        Assert.That(got.Select(n => n.Label), Is.EqualTo(new[] { DrumConverter.ClosedHat, DrumConverter.OpenHat }));
        Assert.That(got.Select(n => n.Pitch), Is.EqualTo(new[] { 42, 46 }));
    }

    [Test]
    public void SilenceAndNoiseFloorHaveNoHits()
    {
        Assert.That(Run(new float[Rate * 2]), Is.Empty);
        var rnd = new Random(3);
        var floor = new float[Rate * 2];
        for (int i = 0; i < floor.Length; i++) floor[i] = (float)((rnd.NextDouble() * 2 - 1) * 0.002);
        Assert.That(Run(floor), Is.Empty, "a steady noise floor is not a hit");
    }

    [Test]
    public void TheClassifierFitsTheFeatureVector()
    {
        Assert.That(DrumModelData.Mean.Length, Is.EqualTo(DrumFeatures.Count));
        Assert.That(DrumModelData.Sd.Length, Is.EqualTo(DrumFeatures.Count));
        Assert.That(DrumModelData.Threshold.Length, Is.EqualTo(DrumModel.Classes));
        for (int m = 0; m < DrumModelData.Hidden.Length; m++)
        {
            Assert.That(DrumModelData.W1[m].Length, Is.EqualTo(DrumModelData.Hidden[m] * DrumFeatures.Count));
            Assert.That(DrumModelData.W2[m].Length, Is.EqualTo(DrumModel.Classes * DrumModelData.Hidden[m]));
        }
        var p = new float[DrumModel.Classes];
        DrumModel.Predict(new float[DrumFeatures.Count], p);
        Assert.That(p, Is.All.InRange(0f, 1f));
    }
}
