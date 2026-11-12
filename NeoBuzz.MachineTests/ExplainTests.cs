using NeoBuzz.Dsp;
using NeoBuzz.Editor;

namespace NeoBuzz.MachineTests
{
    /// <summary>Explain (docs/specs/neo-learn.md §5.3): what the strip shows for hover, drag, the picture, the agent and automation.</summary>
    [TestFixture]
    public class ExplainTests
    {
        static readonly ParamDef[] Table =
        {
            new ParamDef(0, "filter_cutoff", "Cutoff", 0, 1000, 750, MapKind.Freq, a: 20, b: 20000, how: "Sets the corner of the filter.", sound: "Darker when lower."),
            new ParamDef(1, "filter_resonance", "Resonance", 0, 1000, 150, MapKind.Percent, how: "Feeds the cutoff back.", sound: "A whistling edge."),
            new ParamDef(2, "plain", "Plain", 0, 10, 0, MapKind.Int),
        };

        long now;
        readonly int[] raw = { 750, 150, 0 };

        ExplainState Make()
        {
            now = 10_000;
            return new ExplainState(Table, i => raw[i], () => now);
        }

        [Test]
        public void HoverShowsTheControlWithItsValueAndManualBadge()
        {
            var e = Make();
            e.Hover(0);
            var line = e.Current;
            Assert.That(line.Name, Is.EqualTo("Cutoff"));
            Assert.That(line.Value, Is.EqualTo(Table[0].Describe(750)));
            Assert.That(line.Source, Is.EqualTo(ExplainSource.Manual));
            Assert.That(line.How, Is.EqualTo("Sets the corner of the filter."));
            Assert.That(line.Sound, Is.EqualTo("Darker when lower."));
            Assert.That(line.Title, Does.StartWith("Cutoff · "));
        }

        [Test]
        public void ADragShowsTheValueAsItMoves()
        {
            var e = Make();
            e.BeginDrag(1);
            raw[1] = 500;
            Assert.That(e.Current.Value, Is.EqualTo("50%"));
            raw[1] = 800;
            Assert.That(e.Current.Value, Is.EqualTo("80%"));
            e.EndDrag(1);
        }

        [Test]
        public void KeyboardFocusShowsTheControl()
        {
            var e = Make();
            e.KeyboardFocus(1);
            Assert.That(e.Current.Name, Is.EqualTo("Resonance"));
        }

        [Test]
        public void ADragOnThePictureNamesBothControls()
        {
            var e = Make();
            e.PictureDrag(0, 1);
            var line = e.Current;
            Assert.That(line.Name, Is.EqualTo("Cutoff + Resonance"));
            Assert.That(line.Value, Does.Contain("15%"));
            Assert.That(line.How, Does.StartWith("Cutoff: "));
            Assert.That(line.Sound, Does.StartWith("Resonance: "));
        }

        [Test]
        public void TheHandEditsOfAPictureDragKeepNamingBothControls()
        {
            var e = Make();
            e.PictureDrag(0, 1);
            e.Written(0, ExplainSource.Manual);
            e.Written(1, ExplainSource.Manual);
            Assert.That(e.Current.Name, Is.EqualTo("Cutoff + Resonance"), "the writes the drag makes do not narrow the strip to one");
            e.PictureDragEnd();
            e.Written(1, ExplainSource.Manual);
            Assert.That(e.Current.Name, Is.EqualTo("Resonance"), "after the drag a write names its control");
        }

        [Test]
        public void AnAgentWriteShowsAtOnceWithTheAgentBadge()
        {
            var e = Make();
            e.Hover(0);
            now += 100;
            raw[1] = 900;
            e.Written(1, ExplainSource.Agent);
            Assert.That(e.Current.Name, Is.EqualTo("Resonance"));
            Assert.That(e.Current.Source, Is.EqualTo(ExplainSource.Agent));
            Assert.That(e.Current.Badge, Is.EqualTo("Agent"));
            // The control keeps its badge when you hover it again.
            e.Hover(0);
            e.Hover(1);
            Assert.That(e.Current.Source, Is.EqualTo(ExplainSource.Agent));
        }

        [Test]
        public void AnAgentWriteDoesNotInterruptADragOnAnotherControl()
        {
            var e = Make();
            e.BeginDrag(0);
            now += 50;
            e.Written(1, ExplainSource.Agent);
            Assert.That(e.Current.Name, Is.EqualTo("Cutoff"));
            Assert.That(e.SourceOf(1), Is.EqualTo(ExplainSource.Agent), "the badge is remembered");
        }

        [Test]
        public void AutomationWaitsTwoSecondsAfterATouch()
        {
            var e = Make();
            e.Hover(0);
            now += 1900;
            e.Written(1, ExplainSource.Automation);
            Assert.That(e.Current.Name, Is.EqualTo("Cutoff"), "touched 1.9 s ago: automation stays out");
            now += 200;
            e.Written(1, ExplainSource.Automation);
            Assert.That(e.Current.Name, Is.EqualTo("Resonance"));
            Assert.That(e.Current.Source, Is.EqualTo(ExplainSource.Automation));
        }

        [Test]
        public void AutomationUpdatesAtMostTenTimesASecond()
        {
            var e = Make();
            now += 10_000;
            int changes = 0;
            e.Changed += () => changes++;
            for (int ms = 0; ms < 1000; ms += 5)
            {
                now += 5;
                e.Written(ms / 100 % 2, ExplainSource.Automation);   // the control changes every 100 ms
            }
            Assert.That(changes, Is.InRange(9, 10), "a second of automation changes the strip about ten times");
        }

        [Test]
        public void AHandEditShowsAtOnceEvenAfterAutomation()
        {
            var e = Make();
            now += 10_000;
            e.Written(1, ExplainSource.Automation);
            e.Written(0, ExplainSource.Manual);
            Assert.That(e.Current.Name, Is.EqualTo("Cutoff"));
            Assert.That(e.Current.Source, Is.EqualTo(ExplainSource.Manual));
            now += 50;
            e.Written(1, ExplainSource.Automation);
            Assert.That(e.Current.Name, Is.EqualTo("Cutoff"), "just touched");
        }

        [Test]
        public void ParamDefCarriesHowSoundAndCurve()
        {
            Assert.That(Table[0].HasExplain, Is.True);
            Assert.That(Table[2].HasExplain, Is.False);
            Assert.That(Table[0].Curve, Is.EqualTo("logarithmic"));
            Assert.That(Table[1].Curve, Is.EqualTo("linear"));
            Assert.That(new ExplainState(Table, i => raw[i]).Any, Is.True);
        }

        [Test]
        public void TheStripDrawsTheThreeLinesAndTheBadgeInsideItsFrame()
        {
            var e = Make();
            e.Written(1, ExplainSource.Agent);
            var c = new RecordingCanvas();
            var r = new NeoRect(0, 0, 900, ExplainPainter.Height);
            ExplainPainter.Draw(c, r, e.Current, Tokens.Identity(MachineCategory.Synth, NeoColor.FromRgb(0xFF, 0x5C, 0x8A)));
            var texts = c.Of(NeoDrawKind.Text).Select(t => t.Text).ToList();
            Assert.That(texts, Has.Some.EqualTo("Resonance"));
            Assert.That(texts, Has.Some.EqualTo("Agent"));
            Assert.That(texts, Has.Some.StartsWith("How  Feeds"));
            Assert.That(texts, Has.Some.StartsWith("Sound  A whistling"));
            foreach (var t in c.Of(NeoDrawKind.Text))
            {
                Assert.That(t.Points[0].Y + t.Size, Is.LessThanOrEqualTo(r.Bottom), t.Text);
                Assert.That(t.Size, Is.GreaterThanOrEqualTo(10));
            }
            c.Clear();
            ExplainPainter.Draw(c, r, null, NeoColor.FromRgb(1, 2, 3));
            Assert.That(c.Of(NeoDrawKind.Text).Single().Text, Does.Contain("Touch a control"));
        }

        [Test]
        public void ParamSourceMarksAreUsedUpAndExpire()
        {
            var p = new object();
            long t = 1000;
            var was = BuzzGUI.Interfaces.ParamSource.Clock;
            BuzzGUI.Interfaces.ParamSource.Clock = () => t;
            try
            {
                BuzzGUI.Interfaces.ParamSource.MarkAgent(p, 0);
                Assert.That(BuzzGUI.Interfaces.ParamSource.TakeAgent(p, 0), Is.True);
                Assert.That(BuzzGUI.Interfaces.ParamSource.TakeAgent(p, 0), Is.False, "used up");
                BuzzGUI.Interfaces.ParamSource.MarkAgent(p, 0);
                t += BuzzGUI.Interfaces.ParamSource.MarkMs + 1;
                Assert.That(BuzzGUI.Interfaces.ParamSource.TakeAgent(p, 0), Is.False, "expired");
            }
            finally
            {
                BuzzGUI.Interfaces.ParamSource.Clock = was;
            }
        }
    }
}
