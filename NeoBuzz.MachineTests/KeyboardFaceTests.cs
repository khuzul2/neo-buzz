using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The machine keyboard's toolkit-free face (docs/specs/studio-mvp.md §3.12): how many octaves fit a width (two at
    /// least), where each key is and which one a point hits (black keys over white ones, every point of the keys on
    /// exactly one key), the velocity of a strike's height (top soft, bottom loud), a drum machine's octaves and where
    /// they start, the names (as Preferences name notes; C3 = 60 by default) and the labels shortened to fit, and how a key is drawn.
    /// </summary>
    [TestFixture]
    public class KeyboardFaceTests
    {
        static readonly NeoColor Accent = NeoColor.FromRgb(0xE8, 0xA8, 0x4A);

        [Test]
        public void TheOctavesFollowTheWidthTwoAtLeast()
        {
            Assert.That(KeyboardLayout.OctavesFor(150), Is.EqualTo(2), "narrower than two octaves: still two");
            Assert.That(KeyboardLayout.OctavesFor(410), Is.EqualTo(2), "the parameter window");
            Assert.That(KeyboardLayout.OctavesFor(640), Is.EqualTo(3));
            Assert.That(KeyboardLayout.OctavesFor(950), Is.EqualTo(5), "Neo Poly's editor");
            Assert.That(KeyboardLayout.OctavesFor(5000), Is.EqualTo(KeyboardLayout.MaxOctaves));
            Assert.That(KeyboardLayout.MinWidth, Is.EqualTo(15 * KeyboardLayout.MinWhite), "two octaves and the top C");
        }

        [Test]
        public void EveryPointOfTheKeysIsOnExactlyOneKeyBlackOverWhite()
        {
            var l = new KeyboardLayout(48, 2, 15 * 24);
            Assert.That((l.First, l.Last, l.Whites), Is.EqualTo((48, 72, 15)), "C3 to C5");
            Assert.That(l.Keys().Count(), Is.EqualTo(25));
            Assert.That(l.Keys().Take(15).All(n => !KeyboardLayout.IsBlack(n)), Is.True, "white keys first: black ones draw over them");
            for (double x = 0.5; x < l.Width; x += 1)
            {
                for (double y = 0.5; y < l.KeysHeightValue; y += 2)
                {
                    int key = l.KeyAt(x, y);
                    Assert.That(key, Is.InRange(48, 72), $"({x}, {y}) is on a key");
                    var hits = l.Keys().Where(n => l.Rect(n).Contains(x, y)).ToList();
                    // Where a black key lies over a white one, the black one is hit.
                    int expected = hits.FirstOrDefault(KeyboardLayout.IsBlack, hits.FirstOrDefault(n => !KeyboardLayout.IsBlack(n), -1));
                    if (hits.Count > 0) Assert.That(key, Is.EqualTo(expected), $"({x}, {y})");
                }
            }
            Assert.That(l.KeyAt(-1, 10), Is.EqualTo(-1), "left of the keys: none");
            Assert.That(l.KeyAt(10, l.KeysHeightValue + 5), Is.EqualTo(-1), "below them: none");
            // The black keys sit between their white keys, a little to each side as on a piano.
            var cs = l.Rect(49);
            Assert.That(cs.X, Is.GreaterThan(l.Rect(48).X).And.LessThan(l.Rect(50).X));
            Assert.That(cs.Height, Is.EqualTo(l.KeysHeightValue * KeyboardLayout.BlackHeight).Within(1e-9));
        }

        [Test]
        public void AStrikesHeightIsItsVelocityTopSoftBottomLoud()
        {
            var l = new KeyboardLayout(48, 2, 360);
            foreach (int key in new[] { 60, 61 })
            {
                var r = l.Rect(key);
                Assert.That(l.VelocityAt(key, r.Y), Is.EqualTo(KeyboardLayout.SoftestVelocity), "the top: the softest");
                Assert.That(l.VelocityAt(key, r.Bottom), Is.EqualTo(127), "the bottom: 127");
                int last = 0;
                for (double y = r.Y; y <= r.Bottom; y += 1)
                {
                    int v = l.VelocityAt(key, y);
                    Assert.That(v, Is.GreaterThanOrEqualTo(last), "louder further down");
                    last = v;
                }
                Assert.That(l.VelocityAt(key, r.Y + r.Height / 2), Is.InRange(66, 72), "half way: about half");
            }
        }

        [Test]
        public void TheRangeStaysInMidiAndKeepsWholeOctaves()
        {
            Assert.That(KeyboardLayout.ClampFirst(63, 2), Is.EqualTo(60), "to its C");
            Assert.That(KeyboardLayout.ClampFirst(-30, 2), Is.EqualTo(0));
            Assert.That(KeyboardLayout.ClampFirst(127, 2), Is.EqualTo(96), "two whole octaves end at C9 (120) at the top");
            var top = new KeyboardLayout(KeyboardLayout.ClampFirst(200, 8), 8, 1500);
            Assert.That(top.Last, Is.LessThanOrEqualTo(127));
            Assert.That(new KeyboardLayout(120, 2, 300).Last, Is.EqualTo(127), "G9, MIDI's last, ends a range cut short");
        }

        [Test]
        public void ADrumMachineGetsWiderKeysStartingAtItsDrums()
        {
            // Neo Drums' keys: General MIDI's from B1 (DrumsEngine.VoiceOfKey), two keys for some voices.
            var names = new Dictionary<int, string>
            {
                [35] = "Kick", [36] = "Kick", [37] = "Rim", [38] = "Snare", [39] = "Clap", [40] = "Snare", [41] = "Low tom", [42] = "Closed hat",
                [43] = "Low tom", [44] = "Closed hat", [45] = "Mid tom", [46] = "Open hat", [47] = "Mid tom", [48] = "High tom", [49] = "Crash",
                [50] = "High tom", [51] = "Ride", [56] = "Cowbell", [57] = "Crash", [59] = "Ride",
            };
            Assert.That(KeyboardLayout.BestFirstForKit(names, 2), Is.EqualTo((36, 12)), "two octaves from C2 show all twelve drums");
            Assert.That(KeyboardLayout.BestFirstForKit(names, 4).First, Is.EqualTo(36), "on a tie, the higher start (B1 is a second kick)");
            Assert.That(KeyboardLayout.OctavesForKit(410, names), Is.EqualTo(2));
            Assert.That(KeyboardLayout.OctavesForKit(1232, names), Is.EqualTo(4), "a wide editor: keys near 40 px, not 24");
            Assert.That(KeyboardLayout.OctavesForKit(1232, names), Is.LessThan(KeyboardLayout.OctavesFor(1232)));
        }

        [Test]
        public void NamesAreThePreferencesAndLabelsShortenToFit()
        {
            // As Preferences name notes (BuzzGUI.Common.NoteNames, ux-overhaul.md §3.5), as the pattern editor and the piano
            // roll do: C3 is 60 by default.
            Assert.That(KeyboardFace.NoteName(60), Is.EqualTo("C3"));
            Assert.That(KeyboardFace.NoteName(69), Is.EqualTo("A3"));
            Assert.That(KeyboardFace.NoteName(0), Is.EqualTo("C-2"));
            Assert.That(KeyboardFace.NoteName(61), Is.EqualTo("C#3"));
            Assert.That(KeyboardFace.AccessibleName(38, "Snare"), Is.EqualTo("D1, Snare"));
            Assert.That(KeyboardFace.AccessibleName(61, null), Is.EqualTo("C#3"));
            var naming = BuzzGUI.Common.NoteNames.Naming;
            try
            {
                BuzzGUI.Common.NoteNames.Naming = BuzzGUI.Common.NoteNaming.Scientific;
                Assert.That(KeyboardFace.NoteName(60), Is.EqualTo("C4"));
                Assert.That(KeyboardFace.NoteName(0), Is.EqualTo("C-1"));
                BuzzGUI.Common.NoteNames.Naming = BuzzGUI.Common.NoteNaming.Buzz;
                Assert.That(KeyboardFace.NoteName(60), Is.EqualTo("C-5"));
                Assert.That(KeyboardFace.AccessibleName(61, null), Is.EqualTo("C#5"));
            }
            finally { BuzzGUI.Common.NoteNames.Naming = naming; }
            var m = new RecordingCanvas();      // 0.55 × size per character
            Assert.That(KeyboardFace.Fit("Kick", 40, 8.5, m), Is.EqualTo("Kick"), "fits whole");
            Assert.That(KeyboardFace.Fit("Closed hat", 24, 8.5, m), Is.EqualTo("CH"), "words: their initials");
            Assert.That(KeyboardFace.Fit("Low tom", 24, 8.5, m), Is.EqualTo("LT"));
            Assert.That(KeyboardFace.Fit("Cowbell", 20, 8.5, m), Is.EqualTo("Cowb"), "a word: as much as fits");
            Assert.That(KeyboardFace.Fit("Snare", 6, 8.5, m), Is.EqualTo("S"));
            Assert.That(KeyboardFace.Readout(38, "Snare", 96), Is.EqualTo("D1 · Snare · velocity 96"));
            Assert.That(KeyboardFace.Readout(-1, null, 0), Is.Empty);
        }

        [Test]
        public void AKeyIsDrawnLitAsDeepAsItWasStruckAndLabelled()
        {
            var c = new RecordingCanvas();
            KeyboardFace.DrawKey(c, 24, 76, 60, new KeyLook(true, false, 0, false, false), null, Accent);
            Assert.That(c.Of(NeoDrawKind.Text).Select(t => t.Text), Is.EqualTo(new[] { "C3" }), "each C is named");
            Assert.That(c.Of(NeoDrawKind.Rect).Any(r => r.Fill.Color.R == Accent.R && r.Fill.Color.G == Accent.G), Is.False, "silent: not lit");

            c.Clear();
            KeyboardFace.DrawKey(c, 24, 76, 60, new KeyLook(true, false, 127, true, false), null, Accent);
            var soft = new RecordingCanvas();
            KeyboardFace.DrawKey(soft, 24, 76, 60, new KeyLook(true, false, 32, true, false), null, Accent);
            static double Fill(RecordingCanvas rc) => rc.Of(NeoDrawKind.Rect).Where(r => r.Fill.Color.R == Accent.R && r.Fill.Color.B == Accent.B).Min(r => r.Bounds.Height);
            Assert.That(Fill(c), Is.GreaterThan(Fill(soft) * 3), "struck harder, filled deeper");

            c.Clear();
            KeyboardFace.DrawKey(c, 40, 76, 38, new KeyLook(true, false, 0, false, true), "Snare", Accent);
            Assert.That(c.Of(NeoDrawKind.Text).Select(t => t.Text), Is.EqualTo(new[] { "Snare" }), "a drum machine's key: its drum");
            c.Clear();
            KeyboardFace.DrawKey(c, 24, 76, 61, new KeyLook(false, false, 0, false, false), null, Accent);
            Assert.That(c.Of(NeoDrawKind.Text), Is.Empty, "black keys carry no note name");
        }
    }
}
