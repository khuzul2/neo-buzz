using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The editor kit's response graph and band handles (Neo EQ's editor), toolkit-free
    /// (docs/specs/cross-platform.md §8.3): axes, hit-testing, drags, the wheel and double-clicks.
    /// </summary>
    [TestFixture]
    public class ResponseGraphTests
    {
        sealed class FakeBands : IBandHandles
        {
            public readonly double[] Hz = { 30, 100, 250, 600, 1500, 4000, 8000, 18000 };
            public readonly double[] Db = new double[8];
            public readonly bool[] On = { false, true, true, true, true, true, true, false };
            public readonly bool[] HasGain = { false, true, true, true, true, true, true, false };
            public readonly int[] Q = new int[8];

            public int Count => 8;
            public (double Hz, double Db, bool On, bool HasGain) Band(int i) => (Hz[i], Db[i], On[i], HasGain[i]);

            public void Move(int i, double hz, double? db)
            {
                Hz[i] = hz;
                if (db.HasValue) Db[i] = db.Value;
            }

            public void StepQ(int i, int steps) => Q[i] += steps;
            public void SetOn(int i, bool on) => On[i] = on;
        }

        static (ResponseGraph Graph, FakeBands Bands, BandHandleInput Input) Make()
        {
            var graph = new ResponseGraph(-24, 24, 6);
            graph.Layout(new NeoRect(0, 0, 680, 260));
            var bands = new FakeBands();
            return (graph, bands, new BandHandleInput(graph, bands));
        }

        [Test]
        public void TheAxesMapBothWays()
        {
            var (g, _, _) = Make();
            Assert.That(g.X(20), Is.EqualTo(g.Plot.Left).Within(1e-9));
            Assert.That(g.X(20000), Is.EqualTo(g.Plot.Right).Within(1e-9));
            Assert.That(g.X(632.4555), Is.EqualTo(g.Plot.Left + g.Plot.Width / 2).Within(1e-3), "log scale: the middle is √(20 · 20000)");
            foreach (double hz in new[] { 20.0, 320, 1000, 15000 }) Assert.That(g.Hz(g.X(hz)), Is.EqualTo(hz).Within(hz * 1e-9));
            Assert.That(g.Y(0), Is.EqualTo(g.Plot.Top + g.Plot.Height / 2).Within(1e-9));
            Assert.That(g.Db(g.Y(-3)), Is.EqualTo(-3).Within(1e-9));
            Assert.That(ResponseGraph.FreqLabel(2000), Is.EqualTo("2k"));
            Assert.That(ResponseGraph.FreqLabel(500), Is.EqualTo("500"));
        }

        [Test]
        public void DraggingAHandleSetsFrequencyAndGain()
        {
            var (g, bands, input) = Make();
            var p = input.Position(2);
            Assert.That(input.HandleAt(p.X + 3, p.Y - 2), Is.EqualTo(2));
            Assert.That(input.Down(p.X + 3, p.Y - 2, 1, false), Is.True);
            Assert.That(input.Selected, Is.EqualTo(2), "touching a handle selects its band");
            Assert.That(input.Capturing, Is.True);
            // To 320 Hz and −3 dB (relative to where the pointer took hold).
            input.Move(g.X(320) + 3, g.Y(-3) - 2, false);
            input.Up();
            Assert.That(bands.Hz[2], Is.EqualTo(320).Within(1e-6));
            Assert.That(bands.Db[2], Is.EqualTo(-3).Within(1e-9));
            Assert.That(input.Capturing, Is.False);

            // Shift: a tenth of the movement.
            double x0 = g.X(320), y0 = g.Y(-3);
            input.Down(x0, y0, 1, true);
            input.Move(x0 + 100, y0, true);
            input.Up();
            Assert.That(g.X(bands.Hz[2]), Is.EqualTo(x0 + 10).Within(1e-6));
        }

        [Test]
        public void CutsMoveOnlyInFrequencyAndAnOffBandSwitchesOnWhenDragged()
        {
            var (g, bands, input) = Make();
            var p = input.Position(0);
            Assert.That(p.Y, Is.EqualTo(g.Y(0)), "a band without a gain sits on the 0 dB line");
            input.Down(p.X, p.Y, 1, false);
            Assert.That(bands.On[0], Is.False, "a click alone doesn't switch it on");
            input.Move(g.X(80), p.Y + 40, false);
            input.Up();
            Assert.That(bands.On[0], Is.True);
            Assert.That(bands.Hz[0], Is.EqualTo(80).Within(1e-6));
            Assert.That(bands.Db[0], Is.EqualTo(0), "no gain for a cut");
        }

        [Test]
        public void DoubleClickSwitchesAndTheWheelSetsQ()
        {
            var (_, bands, input) = Make();
            var p = input.Position(5);
            input.Down(p.X, p.Y, 1, false);
            input.Up();
            input.Down(p.X, p.Y, 2, false);
            input.Up();
            Assert.That(bands.On[5], Is.False);
            Assert.That(input.Wheel(p.X, p.Y, 120, false), Is.True);
            Assert.That(bands.Q[5], Is.EqualTo(10));
            input.Wheel(p.X, p.Y, -240, true);
            Assert.That(bands.Q[5], Is.EqualTo(8), "Shift: one step per notch");
            input.Select(1);
            input.Wheel(1, 1, 120, false);
            Assert.That(bands.Q[1], Is.EqualTo(10), "away from any handle the wheel sets the selected band's Q");
        }

        [Test]
        public void HandlesAndGridDraw()
        {
            var (g, _, input) = Make();
            var c = new RecordingCanvas();
            g.DrawGrid(c);
            Assert.That(c.Of(NeoDrawKind.Text).Select(t => t.Text), Does.Contain("1k"));
            Assert.That(c.Of(NeoDrawKind.Text).Select(t => t.Text), Does.Contain("−12"));
            c.Clear();
            var colours = Enumerable.Range(0, 8).Select(i => NeoColor.FromRgb((byte)(30 * i), 100, 200)).ToArray();
            input.Draw(c, colours);
            Assert.That(c.Of(NeoDrawKind.Ellipse).Count(), Is.EqualTo(8));
            Assert.That(c.Of(NeoDrawKind.Text).Select(t => t.Text).OrderBy(t => t), Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6", "7", "8" }));
            var curve = g.Curve(hz => hz < 1000 ? 6 : -6);
            Assert.That(curve.PointCount, Is.EqualTo(241));
            Assert.That(curve.Point(0).Y, Is.EqualTo(g.Y(6)));
        }
    }
}
