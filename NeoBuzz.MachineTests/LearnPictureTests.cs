using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>The Neo Learn picture (docs/specs/neo-learn.md §5.4): the pad's input and the filter curve it draws.</summary>
    [TestFixture]
    public class LearnPictureTests
    {
        sealed class Pad : IPictureTarget
        {
            public ParamDef XDef { get; } = new ParamDef(0, "filter_cutoff", "Cutoff", 0, 1000, 666, MapKind.Freq, a: 20, b: 20000);
            public ParamDef YDef { get; } = new ParamDef(1, "filter_resonance", "Resonance", 0, 1000, 150, MapKind.Percent);
            public int XRaw { get; set; } = 666;
            public int YRaw { get; set; } = 150;
            public int Begins, Ends, Writes;
            public void Set(int x, int y) { XRaw = x; YRaw = y; Writes++; }
            public void BeginGesture() => Begins++;
            public void EndGesture() => Ends++;
        }

        static readonly NeoRect Plot = new NeoRect(10, 20, 400, 200);

        [Test]
        public void APressTakesTheHandleAndADragMovesBothControlsInOneGesture()
        {
            var pad = new Pad();
            var input = new LearnPictureInput(pad, () => Plot);
            Assert.That(input.Down(10 + 100, 20 + 100, 1, false), Is.True);
            Assert.That(input.Capturing, Is.True);
            Assert.That(pad.XRaw, Is.EqualTo(250).Within(1), "a quarter across");
            Assert.That(pad.YRaw, Is.EqualTo(500).Within(1), "half way up");
            input.Move(10 + 300, 20 + 50, false);
            input.Move(10 + 300, 20 + 50, false);
            Assert.That(pad.XRaw, Is.EqualTo(750).Within(1));
            Assert.That(pad.YRaw, Is.EqualTo(750).Within(1));
            input.Up();
            Assert.That((pad.Begins, pad.Ends), Is.EqualTo((1, 1)), "one gesture: one undo step");
            Assert.That(input.Capturing, Is.False);
        }

        [Test]
        public void TheDragStaysInsideTheRangeAndShiftMovesATenthAsFar()
        {
            var pad = new Pad();
            var input = new LearnPictureInput(pad, () => Plot);
            input.Down(200, 100, 1, false);
            input.Move(2000, -500, false);
            Assert.That((pad.XRaw, pad.YRaw), Is.EqualTo((1000, 1000)));
            input.Up();

            pad = new Pad();
            input = new LearnPictureInput(pad, () => Plot);
            input.Down(200, 100, 1, true);   // fine: the press does not jump the handle
            Assert.That((pad.XRaw, pad.YRaw), Is.EqualTo((666, 150)));
            input.Move(240, 100, true);      // 40 px of 400 = 10 %, a tenth: 1 %
            Assert.That(pad.XRaw, Is.EqualTo(676).Within(1));
            input.Up();
        }

        [Test]
        public void ADoubleClickResetsBothToTheirDefaultsAndAPressOutsideIsNotTaken()
        {
            var pad = new Pad { XRaw = 900, YRaw = 800 };
            var input = new LearnPictureInput(pad, () => Plot);
            Assert.That(input.Down(0, 0, 1, false), Is.False, "outside the picture");
            Assert.That(pad.Begins, Is.EqualTo(0));
            Assert.That(input.Down(200, 100, 2, false), Is.True);
            Assert.That((pad.XRaw, pad.YRaw), Is.EqualTo((666, 150)));
            Assert.That((pad.Begins, pad.Ends), Is.EqualTo((1, 1)));
            Assert.That(input.Capturing, Is.False);
        }

        [Test]
        public void TheLadderCurveIsDownAtTheCutoffAndPeaksWithResonance()
        {
            Assert.That(LearnPictureGraph.LadderDb(10, 1000, 0), Is.EqualTo(0).Within(0.01), "flat in the pass band");
            Assert.That(LearnPictureGraph.LadderDb(1000, 1000, 0), Is.EqualTo(-12.04).Within(0.1), "4 poles: -12 dB at the corner");
            Assert.That(LearnPictureGraph.LadderDb(8000, 1000, 0), Is.LessThan(-70).Or.EqualTo(-120).Within(0.1));
            Assert.That(LearnPictureGraph.LadderDb(1000, 1000, 1), Is.GreaterThan(LearnPictureGraph.LadderDb(1000, 1000, 0.5)));
            Assert.That(LearnPictureGraph.LadderDb(1000, 1000, 1), Is.GreaterThan(10), "a resonant peak");
        }

        [Test]
        public void ThePictureDrawsOnTheRecordingCanvas()
        {
            var canvas = new RecordingCanvas();
            var scope = new float[128];
            for (int i = 0; i < scope.Length; i++) scope[i] = (float)System.Math.Sin(i * 0.2);
            LearnPictureGraph.Draw(canvas, Plot, scope, scope.Length, 1000, 0.3, new NeoPoint(0.5, 0.3), true, NeoColor.FromRgb(0x4F, 0xC3, 0xA1));
            Assert.That(canvas.Calls.Count, Is.GreaterThan(5));
        }
    }
}
