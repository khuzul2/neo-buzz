using NeoBuzz.Dsp;
using NeoBuzz.Editor;
using NeoBuzz.Fm6;

namespace NeoBuzz.MachineTests
{
    /// <summary>
    /// The Neo editor kit's toolkit-free drawing (docs/specs/cross-platform.md §8.3), run against
    /// the recording canvas: graphs, the FM algorithm diagram, knob input and hit-testing.
    /// </summary>
    [TestFixture]
    public class EditorCanvasTests
    {
        static readonly NeoColor Accent = NeoColor.FromRgb(0xFF, 0xC4, 0x3D);

        static bool Inside(NeoRect outer, NeoRect r, double slack = 1e-9) =>
            r.Left >= outer.Left - slack && r.Right <= outer.Right + slack && r.Top >= outer.Top - slack && r.Bottom <= outer.Bottom + slack;

        [Test]
        public void ColoursArePlainArgb()
        {
            var c = NeoColor.FromRgb(0x12, 0x34, 0x56);
            Assert.That(c.Argb, Is.EqualTo(0xFF123456u));
            Assert.That(c.WithAlpha(0x38).Argb, Is.EqualTo(0x38123456u));
            Assert.That(c.Tint(1).Argb, Is.EqualTo(0xFFFFFFFFu));
            Assert.That(NeoFill.None.IsNone && NeoStroke.None.IsNone, Is.True);
            NeoFill fill = c;
            Assert.That(fill.IsNone, Is.False);
            Assert.That(new NeoStroke(c, 1).Round, Is.True, "round ends unless asked");
        }

        [Test]
        public void PathsKeepTheirFiguresAndDropTheBackendCacheWhenChanged()
        {
            var p = new NeoPath().MoveTo(0, 0).LineTo(10, 0).LineTo(10, 10, smoothJoin: true).Close().MoveTo(20, 20).LineTo(30, 30);
            Assert.That(p.FigureCount, Is.EqualTo(2));
            Assert.That(p.GetFigure(0), Is.EqualTo(new NeoPath.Figure(0, 3, true)));
            Assert.That(p.GetFigure(1), Is.EqualTo(new NeoPath.Figure(3, 2, false)));
            Assert.That(p.SmoothJoin(2) && !p.SmoothJoin(1), Is.True);
            p.BackendCache = "built";
            p.LineTo(40, 40);
            Assert.That(p.BackendCache, Is.Null);
            var line = new NeoPath().LineTo(1, 2).LineTo(3, 4);
            Assert.That(line.FigureCount, Is.EqualTo(1), "a path's first LineTo starts its figure");
        }

        [Test]
        public void GraphFrameClipsTheBodyAndSurvivesAFailingOne()
        {
            var c = new RecordingCanvas();
            var r = new NeoRect(0, 0, 100, 50);
            GraphFrame.Draw(c, r, (g, rr) =>
            {
                g.Line(0, 0, 10, 10, new NeoStroke(Accent, 1));
                throw new InvalidOperationException("drawing bug");
            });
            Assert.That(c.Calls.Select(x => x.Kind).ToArray(),
                Is.EqualTo(new[] { NeoDrawKind.Rect, NeoDrawKind.PushClip, NeoDrawKind.Line, NeoDrawKind.PopClip }));
            Assert.That(c.Calls[0].Fill.Color, Is.EqualTo(Palette.Background));
            Assert.That(c.Calls[1].Bounds, Is.EqualTo(r));
            Assert.That(c.ClipDepth == 0 && !c.Unbalanced, Is.True);
        }

        [Test]
        public void AdsrEnvelopeIsAClosedShapeThatFollowsItsParameters()
        {
            var c = new RecordingCanvas();
            var r = new NeoRect(0, 0, 256, 46);
            EnvelopePainter.Adsr(c, r, 0.01, 0.3, 0.5, 1.0, Accent);
            var path = c.Of(NeoDrawKind.Path).Single();
            var pts = path.Points;
            Assert.That(pts.Length, Is.EqualTo(5));
            Assert.That(path.Path.GetFigure(0).Closed, Is.True);
            Assert.That(path.Fill.Color, Is.EqualTo(Accent.WithAlpha(0x30)));
            Assert.That(path.Stroke, Is.EqualTo(new NeoStroke(Accent, 1.5)));
            double top = r.Top + 6, bottom = r.Bottom - 6;
            Assert.That(pts[0], Is.EqualTo(new NeoPoint(r.Left + 6, bottom)), "starts at silence");
            Assert.That(pts[1].Y, Is.EqualTo(top), "attack reaches the top");
            Assert.That(pts[2].Y, Is.EqualTo(bottom - 0.5 * (bottom - top)).Within(1e-9), "decays to the sustain level");
            Assert.That(pts[3].Y, Is.EqualTo(pts[2].Y), "holds while the note is held");
            Assert.That(pts[4], Is.EqualTo(new NeoPoint(r.Right - 6, bottom)), "releases to silence at the right edge");
            for (int i = 1; i < pts.Length; i++) Assert.That(pts[i].X, Is.GreaterThanOrEqualTo(pts[i - 1].X));
            Assert.That(Inside(r, path.Bounds), Is.True);

            // A longer attack takes a wider segment.
            var c2 = new RecordingCanvas();
            EnvelopePainter.Adsr(c2, r, 1.0, 0.3, 0.5, 1.0, Accent);
            var pts2 = c2.Of(NeoDrawKind.Path).Single().Points;
            Assert.That(pts2[1].X - pts2[0].X, Is.GreaterThan(pts[1].X - pts[0].X));
        }

        [Test]
        public void MeterFillsToItsLevelAndTurnsRedAboveZero()
        {
            var r = new NeoRect(0, 0, 16, 120);
            var inner = new NeoRect(3, 3, 10, 114);
            double Y(double db) => inner.Bottom - (db + 60) / 66 * inner.Height;

            var c = new RecordingCanvas();
            MeterPainter.Draw(c, r, (float)DspMath.DbToGain(-12), Accent, false);
            var bar = c.Of(NeoDrawKind.Rect).Single();
            Assert.That(bar.Bounds.Top, Is.EqualTo(Y(-12)).Within(1e-3));
            Assert.That(bar.Bounds.Bottom, Is.EqualTo(inner.Bottom).Within(1e-9));
            Assert.That(bar.Fill.Color, Is.EqualTo(Accent));
            Assert.That(c.Of(NeoDrawKind.Line).Count(), Is.EqualTo(4), "0, −12, −24 and −48 dB ticks");

            c.Clear();
            MeterPainter.Draw(c, r, 2f, Accent, false);
            Assert.That(c.Of(NeoDrawKind.Rect).Single().Fill.Color, Is.EqualTo(Palette.Over));

            c.Clear();
            MeterPainter.Draw(c, r, 0f, Accent, false);
            Assert.That(c.Of(NeoDrawKind.Rect).Count(), Is.EqualTo(0), "silence draws no bar");

            c.Clear();
            MeterPainter.Draw(c, r, (float)DspMath.DbToGain(-15), Accent, true);
            var gr = c.Of(NeoDrawKind.Rect).Single();
            Assert.That(gr.Bounds.Top, Is.EqualTo(inner.Top));
            Assert.That(gr.Bounds.Height, Is.EqualTo(inner.Height / 2).Within(1e-3), "15 of 30 dB reduction fills half, from the top");
        }

        static OperatorDiagram Diagram(int number)
        {
            var alg = Algorithms.Get(number);
            return new OperatorDiagram(alg.Number, Algorithms.Layout(alg), alg.Carriers, alg.Modulators, alg.FeedbackFrom, alg.FeedbackTo);
        }

        /// <summary>The FM6 editor's algorithm view: 214 × 132 in the editor.</summary>
        static readonly NeoRect AlgorithmFrame = new NeoRect(0, 0, 214, 132);

        [Test]
        public void EveryFm6AlgorithmFitsItsViewWithoutOverlaps()
        {
            for (int n = 1; n <= 32; n++)
            {
                var d = Diagram(n);
                var alg = Algorithms.Get(n);
                var boxes = Enumerable.Range(0, 6).Select(op => d.Box(AlgorithmFrame, op)).ToArray();
                for (int op = 0; op < 6; op++)
                {
                    Assert.That(Inside(AlgorithmFrame, boxes[op]), Is.True, $"algorithm {n}: operator {op + 1} inside the view");
                    for (int other = op + 1; other < 6; other++)
                        Assert.That(boxes[op].Overlaps(boxes[other]), Is.False, $"algorithm {n}: operators {op + 1} and {other + 1} overlap");
                    if (alg.IsCarrier(op))
                        Assert.That(boxes[op].Bottom, Is.EqualTo(AlgorithmFrame.Bottom - OperatorDiagram.BottomMargin), $"algorithm {n}: carrier {op + 1} on the bottom row");
                    for (int m = 0; m < 6; m++)
                        if (d.Modulates(m, op) && m != op)
                            Assert.That(boxes[m].Bottom, Is.LessThanOrEqualTo(boxes[op].Top), $"algorithm {n}: {m + 1} sits above {op + 1}, which it modulates");
                }
            }
        }

        [Test]
        public void AlgorithmDiagramDrawsItsLinksBusFeedbackAndLitOperators()
        {
            foreach (int n in new[] { 1, 5, 32 })
            {
                var d = Diagram(n);
                var alg = Algorithms.Get(n);
                var c = new RecordingCanvas();
                float[] levels = { 2f, 0f, 1f, 0f, 0f, 0f };
                d.Draw(c, AlgorithmFrame, Accent, 7, op => op != 3, op => levels[op]);

                int links = 0;
                for (int to = 0; to < 6; to++)
                    for (int from = 0; from < 6; from++)
                        if ((alg.Modulators[to] & (1 << from)) != 0) links++;
                Assert.That(c.Of(NeoDrawKind.Line).Count(), Is.EqualTo(alg.CarrierCount + 1 + links), $"algorithm {n}: carrier drops, the bus and one line per modulation");
                Assert.That(c.Calls[0].Text, Is.EqualTo($"Algorithm {n}"));

                // The bus spans the carriers, under them.
                var bus = c.Of(NeoDrawKind.Line).ElementAt(alg.CarrierCount);
                Assert.That(bus.Points[0].Y, Is.EqualTo(OperatorDiagram.BusY(AlgorithmFrame)));
                Assert.That(bus.Points[1].X - bus.Points[0].X, Is.EqualTo((alg.CarrierCount - 1) * OperatorDiagram.ColumnStep).Within(1e-9));

                // Feedback ends on top of the operator fed back to, drawn strongly as feedback is on.
                var loop = c.Of(NeoDrawKind.Path).Single();
                var to6 = d.Box(AlgorithmFrame, alg.FeedbackTo);
                Assert.That(loop.Points[^1], Is.EqualTo(new NeoPoint(to6.X + OperatorDiagram.BoxWidth / 2, to6.Y)));
                Assert.That(loop.Stroke.Color.A, Is.EqualTo(0xC0));

                // Six boxes, in order; the switched-off one has a dim outline and label.
                var boxes = c.Of(NeoDrawKind.Rect).ToArray();
                var labels = c.Of(NeoDrawKind.Text).Skip(1).ToArray();
                Assert.That(boxes.Length, Is.EqualTo(6));
                Assert.That(labels.Select(l => l.Text).ToArray(), Is.EqualTo(new[] { "1", "2", "3", "4", "5", "6" }));
                Assert.That(boxes[3].Stroke.Color, Is.EqualTo(Palette.PanelEdge));
                Assert.That(labels[3].Color, Is.EqualTo(Palette.DimText));
                Assert.That(boxes[0].Stroke.Color, Is.EqualTo(Accent));
                // Full level lights a carrier to 0xD0, a modulator to 0xB0; silence leaves 0x40 / 0x10.
                Assert.That(boxes[0].Fill.Color.A, Is.EqualTo(alg.IsCarrier(0) ? 0xD0 : 0xB0));
                Assert.That(boxes[1].Fill.Color.A, Is.EqualTo(alg.IsCarrier(1) ? 0x40 : 0x10));
            }

            var off = new RecordingCanvas();
            Diagram(1).Draw(off, AlgorithmFrame, Accent, 0, op => true, op => 0f);
            Assert.That(off.Of(NeoDrawKind.Path).Single().Stroke, Is.EqualTo(new NeoStroke(Accent.WithAlpha(0x40), 1)), "no feedback: a faint loop");
        }

        [Test]
        public void KnobDragsWheelsAndResets()
        {
            var k = new KnobModel(46) { Default = 0.5 };
            Assert.That(k.Width, Is.EqualTo(64));
            Assert.That(k.Height, Is.EqualTo(80));

            k.BeginDrag(100);
            Assert.That(k.DragTo(50, fine: false), Is.True);
            Assert.That(k.Value, Is.EqualTo(0.25).Within(1e-12), "200 px for the whole range");
            k.EndDrag();
            k.BeginDrag(100);
            Assert.That(k.DragTo(100 - 1500 * 0.1, fine: true), Is.True);
            Assert.That(k.Value, Is.EqualTo(0.35).Within(1e-12), "Shift: 1500 px for the whole range");

            // Past the top it re-anchors, so turning back responds at once.
            Assert.That(k.DragTo(-2000, fine: false), Is.True);
            Assert.That(k.Value, Is.EqualTo(1));
            Assert.That(k.DragTo(-2020, fine: false), Is.False, "still at the top");
            Assert.That(k.DragTo(-2000, fine: false), Is.True);
            Assert.That(k.Value, Is.EqualTo(0.9).Within(1e-12));
            Assert.That(k.EndDrag(), Is.True);
            Assert.That(k.EndDrag(), Is.False);
            Assert.That(k.DragTo(0, false), Is.False, "no drag, no change");

            Assert.That(k.ResetToDefault(), Is.True);
            Assert.That(k.Value, Is.EqualTo(0.5));
            Assert.That(k.ResetToDefault(), Is.False);

            k.Steps = 1000;
            k.Wheel(120, fine: false);
            Assert.That(k.Value, Is.EqualTo(0.51).Within(1e-12));
            k.Wheel(-120, fine: true);
            Assert.That(k.Value, Is.EqualTo(0.509).Within(1e-12));
            k.Steps = 4;
            k.Wheel(-1, fine: false);
            Assert.That(k.Value, Is.EqualTo(0.259).Within(1e-12), "small ranges step one value");
        }

        [Test]
        public void KnobDrawsTrackValueBodyPointerAndTexts()
        {
            var k = new KnobModel(46) { Label = "Drive", ValueText = "18.0 dB" };
            k.Set(0.5);
            var c = new RecordingCanvas();
            k.Draw(c, 64, Accent);
            Assert.That(c.Calls.Select(x => x.Kind).ToArray(), Is.EqualTo(new[]
            {
                NeoDrawKind.Arc, NeoDrawKind.Arc, NeoDrawKind.Ellipse, NeoDrawKind.Line, NeoDrawKind.Text, NeoDrawKind.Text,
            }));
            var value = c.Calls[1];
            Assert.That(value.FromDegrees, Is.EqualTo(-KnobModel.Sweep));
            Assert.That(value.ToDegrees, Is.EqualTo(0).Within(1e-12));
            Assert.That(c.Calls[2].Fill.IsGradient, Is.True);
            var pointer = c.Calls[3];
            Assert.That(pointer.Points[1].X, Is.EqualTo(32).Within(1e-9), "at half the pointer is straight up");
            Assert.That(pointer.Points[1].Y, Is.LessThan(pointer.Points[0].Y));
            Assert.That(c.Calls[4].Text == "Drive" && c.Calls[4].Align == NeoAlign.Center, Is.True);
            Assert.That(c.Calls[5].Color, Is.EqualTo(Palette.DimText), "the value is dim until hovered");

            // At its origin (the middle, for a symmetric −/+ range): no value arc.
            var b = new KnobModel(46) { Origin = 0.5 };
            b.Set(0.5);
            var c2 = new RecordingCanvas();
            b.Draw(c2, 64, Accent);
            Assert.That(c2.Of(NeoDrawKind.Arc).Count(), Is.EqualTo(1));
        }

        [Test]
        public void AKnobIsAsWideAsItsLabel()
        {
            // mac-402, win-802: Neo Delay's "Duck threshold" label, wider than its knob, ran under the duck meter beside it.
            var measure = new RecordingCanvas();
            var k = new KnobModel(46) { Label = "Duck threshold", ValueText = "−30.0 dB" };
            Assert.That(k.Width, Is.EqualTo(64), "unmeasured: the knob's own width");
            k.FitLabel(measure);
            double label = measure.MeasureText("Duck threshold", KnobModel.LabelSize);
            Assert.That(label, Is.GreaterThan(64), "the label is wider than the knob");
            Assert.That(k.Width, Is.GreaterThanOrEqualTo(label + 6), "the knob takes its label's width");
            var c = new RecordingCanvas();
            k.Draw(c, k.Width, Accent);
            foreach (var t in c.Of(NeoDrawKind.Text))
                Assert.That(t.Bounds.Left >= 0 && t.Bounds.Right <= k.Width, Is.True, $"\"{t.Text}\" inside the knob's width");

            var mix = new KnobModel(46) { Label = "Mix" };
            mix.FitLabel(measure);
            Assert.That(mix.Width, Is.EqualTo(64), "a short label keeps the knob's own width");
        }

        [Test]
        public void ALongLabelWrapsOntoASecondLineWhereTheKnobHasAWidthToKeep()
        {
            // UI gallery review (mac-460, win-901, win-903, win-909): the manifest editor cut long labels with "…"
            // ("Pulse width / PWM d…"). With a width to keep, a label wraps at a space onto a second line, whole.
            var measure = new RecordingCanvas();
            var k = new KnobModel(46) { Label = "Pulse width / PWM depth", ValueText = "0%", MaxLabelWidth = 104 };
            k.FitLabel(measure);
            Assert.That(measure.MeasureText(k.Label, KnobModel.LabelSize), Is.GreaterThan(104), "too long for one line");
            Assert.That(k.LabelLines, Is.EqualTo(new[] { "Pulse width / PWM", "depth" }));
            Assert.That(k.Width, Is.LessThanOrEqualTo(104 + 6), "the knob keeps its width");
            Assert.That(k.Height, Is.EqualTo(46 + 34 + KnobModel.LineHeight), "one line taller");
            var c = new RecordingCanvas();
            k.Draw(c, k.Width, Accent);
            var texts = c.Of(NeoDrawKind.Text).ToList();
            Assert.That(texts.Select(t => t.Text), Is.EqualTo(new[] { "Pulse width / PWM", "depth", "0%" }), "both lines, then the value");
            Assert.That(texts.All(t => !t.Text.Contains('…')), Is.True);
            Assert.That(texts[1].Points[0].Y - texts[0].Points[0].Y, Is.EqualTo(KnobModel.LineHeight));
            Assert.That(texts[2].Bounds.Bottom, Is.LessThanOrEqualTo(k.Height + 1), "the value inside the knob's height");
            foreach (var t in texts) Assert.That(t.Bounds.Left >= 0 && t.Bounds.Right <= k.Width, Is.True, $"\"{t.Text}\" inside the knob");

            var shortOne = new KnobModel(46) { Label = "Cutoff", MaxLabelWidth = 104 };
            shortOne.FitLabel(measure);
            Assert.That(shortOne.LabelLines, Is.EqualTo(new[] { "Cutoff" }));
            Assert.That(shortOne.Height, Is.EqualTo(80), "a short label: one line");
            var noLimit = new KnobModel(46) { Label = "Pulse width / PWM depth" };
            noLimit.FitLabel(measure);
            Assert.That(noLimit.LabelLines, Has.Length.EqualTo(1), "without a width to keep (the Neo editors): one line, the knob as wide");
        }

        [Test]
        public void TheKnobsUnfilledTrackStandsOutFromThePanel()
        {
            // UI gallery review: the unfilled track was 1.39:1 against the panel; a control's parts need 3:1 (WCAG 1.4.11).
            Assert.That(Contrast.Ratio(Palette.Track, Palette.Panel), Is.LessThan(1.5), "the old track colour");
            Assert.That(Contrast.Ratio(NeoColor.FromRgb(0xFF, 0xFF, 0xFF), NeoColor.FromRgb(0, 0, 0)), Is.EqualTo(21).Within(1e-9));
            var k = new KnobModel(46);
            k.Set(0.3);
            var c = new RecordingCanvas();
            k.Draw(c, 64, Accent);
            var track = c.Of(NeoDrawKind.Arc).First();
            Assert.That(track.FromDegrees, Is.EqualTo(-KnobModel.Sweep).Within(1e-9), "the whole sweep: the track");
            double ratio = Contrast.Ratio(track.Stroke.Color, Palette.Panel);
            TestContext.Out.WriteLine($"track {track.Stroke.Color} on the panel {Palette.Panel}: {ratio:F2}:1");
            Assert.That(ratio, Is.GreaterThanOrEqualTo(3), "3:1 against the panel");
            Assert.That(Contrast.Ratio(track.Stroke.Color, Palette.Background), Is.GreaterThanOrEqualTo(3), "and the window's background");
        }

        [Test]
        public void AnOffOnSwitchIsOneToggleThatLightsWhenOn()
        {
            // UI gallery review (mac-401, mac-410, mac-414): switches were "Off | On" segments, the lit one often "Off",
            // so an accent-lit control could mean off. A switch is one toggle named for its function, lit only when on.
            var makeup = NeoBuzz.Comp.Params.Table[NeoBuzz.Comp.P.AutoMakeup];
            var dcRemove = NeoBuzz.Utility.Params.Table[NeoBuzz.Utility.P.DcRemove];
            Assert.That(ParamMapping.IsToggle(makeup) && ParamMapping.IsToggle(dcRemove), Is.True, "Off/On switches");
            Assert.That(ParamMapping.IsToggle(NeoBuzz.Utility.Params.Table[NeoBuzz.Utility.P.PanLaw]), Is.False, "a choice with names stays a row");
            var named = new ParamDef(0, "Phase", "Phase", 0, 1, 0, MapKind.Switch, 0, 1, names: new[] { "Linear", "Minimum" });
            Assert.That(ParamMapping.IsToggle(named), Is.False, "a two-state choice with names of its own stays a row");

            var measure = new RecordingCanvas();
            var toggle = new ToggleFace("Auto makeup", 72, measure);
            Assert.That(toggle.Height, Is.EqualTo(new ChoiceStrip("x", new[] { "Off", "On" }, 36, false, measure).Height), "where a row of buttons stood");
            Assert.That(toggle.Width, Is.GreaterThanOrEqualTo(72));
            Assert.That(toggle.Hit(toggle.Width / 2, ChoiceStrip.LabelHeight + 11), Is.True);
            Assert.That(toggle.Hit(toggle.Width / 2, 5), Is.False, "above the button");

            NeoDrawCall[] Drawn(bool on)
            {
                var c = new RecordingCanvas();
                toggle.Draw(c, on, false, Accent);
                return c.Calls.ToArray();
            }
            bool Lit(NeoDrawCall x) => x.Fill.Color.R == Accent.R && x.Fill.Color.G == Accent.G && x.Fill.Color.B == Accent.B && x.Fill.Color.IsVisible
                                       || x.Stroke.Color == Accent || x.Color == Accent;
            var off = Drawn(false);
            var on = Drawn(true);
            Assert.That(off.Where(x => x.Kind == NeoDrawKind.Text).Select(x => x.Text), Is.EqualTo(new[] { "Auto makeup" }), "named for its function: no Off, no On");
            Assert.That(off.Any(Lit), Is.False, "off: nothing in the accent");
            Assert.That(on.Single(x => x.Kind == NeoDrawKind.Rect).Fill.Color, Is.EqualTo(Accent.WithAlpha(0x38)), "on: lit");
            Assert.That(on.Single(x => x.Kind == NeoDrawKind.Text).Color, Is.EqualTo(Accent));
            Assert.That(on.Single(x => x.Kind == NeoDrawKind.Ellipse).Fill.Color, Is.EqualTo(Accent), "its lamp on");
            Assert.That(off.Single(x => x.Kind == NeoDrawKind.Ellipse).Fill.IsNone, Is.True, "and off");
        }

        [Test]
        public void KnobArcsStartAtTheValueZeroOrTheMinimum()
        {
            // mac-409: Neo Limiter's Input gain (−12 … +24 dB) at 0.0 dB lit an arc from +6 dB, the knob's middle.
            var gain = NeoBuzz.Limiter.Params.Table[NeoBuzz.Limiter.P.InputGain];
            Assert.That(gain.Value(gain.Default), Is.EqualTo(0).Within(1e-9), "its default is 0 dB");
            double origin = ParamMapping.ArcOrigin(gain);
            Assert.That(origin, Is.EqualTo(1 / 3.0).Within(1e-9), "0 dB is a third of the way round");

            KnobModel Knob(ParamDef def, int raw)
            {
                var k = new KnobModel(46) { Origin = ParamMapping.ArcOrigin(def) };
                k.Set(ParamMapping.Normalized(def, raw));
                return k;
            }
            List<NeoDrawCall> ValueArcs(KnobModel k)
            {
                var c = new RecordingCanvas();
                k.Draw(c, 64, Accent);
                return c.Of(NeoDrawKind.Arc).Where(a => a.Stroke.Color == Accent).ToList();
            }
            double Angle(double position) => -KnobModel.Sweep + 2 * KnobModel.Sweep * position;

            Assert.That(ValueArcs(Knob(gain, gain.Default)), Is.Empty, "at 0 dB: no arc");
            var up = ValueArcs(Knob(gain, gain.Raw(6))).Single();
            Assert.That(up.FromDegrees, Is.EqualTo(Angle(origin)).Within(1e-6), "+6 dB: from 0 dB …");
            Assert.That(up.ToDegrees, Is.EqualTo(Angle(ParamMapping.Normalized(gain, gain.Raw(6)))).Within(1e-6), "… to +6 dB");
            var down = ValueArcs(Knob(gain, gain.Raw(-6))).Single();
            Assert.That(down.ToDegrees, Is.EqualTo(Angle(origin)).Within(1e-6), "−6 dB: from −6 dB up to 0 dB");

            // A symmetric −/+ range starts in the middle; a one-sided one at its minimum.
            var pan = NeoBuzz.Utility.Params.Table[NeoBuzz.Utility.P.Pan];
            Assert.That(ParamMapping.IsBipolar(pan), Is.True);
            Assert.That(ParamMapping.ArcOrigin(pan), Is.EqualTo(0.5).Within(1e-3), "Pan: the middle");
            var ceiling = NeoBuzz.Limiter.Params.Table[NeoBuzz.Limiter.P.Ceiling];
            Assert.That(ParamMapping.ArcOrigin(ceiling), Is.EqualTo(0), "Ceiling, −12 … 0 dB: from its minimum");
            var lookahead = NeoBuzz.Limiter.Params.Table[NeoBuzz.Limiter.P.Lookahead];
            Assert.That(ParamMapping.ArcOrigin(lookahead), Is.EqualTo(0));
            var low = ValueArcs(Knob(ceiling, ceiling.Raw(-6))).Single();
            Assert.That(low.FromDegrees, Is.EqualTo(-KnobModel.Sweep).Within(1e-9));
            // Any range across 0, whatever its kind: a ±50 ct fine tune, ±99 % feedback, Neo Delay's −50 … +100 % offset.
            var fine = NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.AFine];
            Assert.That(ParamMapping.ArcOrigin(fine), Is.EqualTo(0.5).Within(1e-9), "A fine (±50 ct): the middle");
            Assert.That(ValueArcs(Knob(fine, fine.Default)), Is.Empty, "at 0 ct: no arc");
            var offset = NeoBuzz.Delay.Params.Table[NeoBuzz.Delay.P.Offset];
            Assert.That(ParamMapping.ArcOrigin(offset), Is.EqualTo(1 / 3.0).Within(1e-3), "Offset: from 0 %");
            // A level whose bottom is silence is one-sided: from the minimum.
            var volume = NeoBuzz.Wave.Params.Table[NeoBuzz.Wave.P.Volume];
            Assert.That(volume.Value(volume.Min), Is.EqualTo(double.NegativeInfinity));
            Assert.That(ParamMapping.ArcOrigin(volume), Is.EqualTo(0), "Volume (−∞ … +6 dB): from the minimum");

            // The crossing between two raw values, and ranges that fall.
            Assert.That(ParamMapping.ZeroPosition(r => r - 2.5, 0, 10), Is.EqualTo(0.25).Within(1e-12));
            Assert.That(ParamMapping.ZeroPosition(r => 5 - r, 0, 10), Is.EqualTo(0.5).Within(1e-12));
            Assert.That(ParamMapping.ZeroPosition(r => r + 1, 0, 10), Is.EqualTo(0), "never 0: the minimum");
        }

        [Test]
        public void ChoiceStripsAndThePresetBarHitTestTheirCells()
        {
            var names = new[] { "Off", "On" };
            var row = new ChoiceStrip("Auto gain", names, 32, false, new RecordingCanvas());
            Assert.That(row.Width, Is.EqualTo(64));
            Assert.That(row.Height, Is.EqualTo(42));
            Assert.That(row.CellAt(10, 10), Is.EqualTo(-1), "the label is not a button");
            Assert.That(row.CellAt(10, 30), Is.EqualTo(0));
            Assert.That(row.CellAt(40, 30), Is.EqualTo(1));
            Assert.That(row.CellAt(70, 30), Is.EqualTo(-1));

            var column = new ChoiceStrip("Mode", new[] { "Tape", "Tube", "Transistor" }, 78, true, new RecordingCanvas());
            Assert.That(column.Height, Is.EqualTo(18 + 3 * 22));
            Assert.That(column.CellAt(5, 18 + 2 * 22 + 1), Is.EqualTo(2));

            var auto = new ChoiceStrip("x", new[] { "a", "abcdefghij" }, 0, false, new RecordingCanvas());
            Assert.That(auto.CellWidth, Is.EqualTo(Math.Max(34, 10 * 11 * 0.55 + 14)).Within(1e-9), "sized to the longest name");

            var c = new RecordingCanvas();
            row.Draw(c, 1, -1, Accent);
            var cells = c.Of(NeoDrawKind.Rect).ToArray();
            Assert.That(cells[1].Fill.Color, Is.EqualTo(Accent.WithAlpha(0x38)), "the selected cell is lit");
            Assert.That(cells[0].Fill.Color, Is.EqualTo(Palette.Panel));
            Assert.That(c.Of(NeoDrawKind.Text).Last().Bold, Is.True);

            Assert.That(PresetBarFace.ZoneAt(5), Is.EqualTo(PresetBarFace.Zone.Previous));
            Assert.That(PresetBarFace.ZoneAt(100), Is.EqualTo(PresetBarFace.Zone.List));
            Assert.That(PresetBarFace.ZoneAt(200), Is.EqualTo(PresetBarFace.Zone.Next));
            Assert.That(PresetBarFace.ZoneAt(218), Is.EqualTo(PresetBarFace.Zone.None), "the gap before Save");
            Assert.That(PresetBarFace.ZoneAt(230), Is.EqualTo(PresetBarFace.Zone.Save));
        }
    }
}
