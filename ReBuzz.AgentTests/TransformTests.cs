using System.Text.Json.Nodes;
using ReBuzz.Agent.Mcp;
using ReBuzz.Agent.Model;

namespace ReBuzz.AgentTests
{
    /// <summary>Phase A3's note transforms and notes_transform's arguments (docs/specs/mcp-server.md §5.5).</summary>
    [TestFixture]
    public class TransformTests
    {
        const int Tpb = 4, PatternTicks = 64;   // a 16-beat pattern at 4 ticks per beat

        static int T(double beats) => (int)Math.Round(beats * Tpb * NoteList.TimeBase);

        static NoteEvent N(double start, double length, int pitch, int velocity = 100) => new(T(start), T(length), pitch, velocity);

        static TransformContext Ctx() => new(Tpb, PatternTicks);

        static string Problems(TransformContext ctx) => string.Join(" ", ctx.Problems);

        static List<int> Pitches(IEnumerable<NoteEvent> notes) => notes.Select(n => n.Pitch).ToList();

        static List<double> Starts(IEnumerable<NoteEvent> notes) => notes.Select(n => NoteList.Beats(n.Time, Tpb)).ToList();

        static List<double> Lengths(IEnumerable<NoteEvent> notes) => notes.Select(n => NoteList.Beats(n.Duration, Tpb)).ToList();

        [Test]
        public void TransposeBySemitonesWithSelectionAndClipping()
        {
            var notes = new[] { N(0, 1, 60), N(1, 1, 64), N(2, 1, 67) };
            Assert.That(Pitches(Transforms.Transpose(notes, Ctx(), 12)), Is.EqualTo(new[] { 72, 76, 79 }));
            Assert.That(Pitches(Transforms.Transpose(notes, Ctx(), -12, new NoteSelection(Low: 64))), Is.EqualTo(new[] { 60, 52, 55 }));

            var ctx = Ctx();
            var r = Transforms.Transpose(new[] { N(0, 1, 127), N(1, 1, 120) }, ctx, 5);
            Assert.That(Pitches(r), Is.EqualTo(new[] { 120, 125 }));
            Assert.That(Problems(ctx), Does.Contain("moved by octaves: C10→C9"));
            Assert.That(ctx.Problems.Count, Is.EqualTo(1));
        }

        [Test]
        public void TransposeByScaleSteps()
        {
            var key = MusicKey.Parse("C major");
            var ctx = Ctx();
            Assert.That(Pitches(Transforms.TransposeInKey(new[] { N(0, 1, 60), N(0, 1, 64), N(0, 1, 67) }, ctx, key, 2)), Is.EqualTo(new[] { 64, 67, 71 }));
            Assert.That(ctx.Problems, Is.Empty);
            Assert.That(Pitches(Transforms.TransposeInKey(new[] { N(0, 1, 72) }, ctx, key, -3)), Is.EqualTo(new[] { 67 }), "C5 down three steps: B4, A4, G4");
            Assert.That(Pitches(Transforms.TransposeInKey(new[] { N(0, 1, 61) }, ctx, key, 1)), Is.EqualTo(new[] { 63 }));
            Assert.That(Problems(ctx), Does.Contain("weren't in C major"));
            var minor = MusicKey.Parse("A minor");
            Assert.That(Pitches(Transforms.TransposeInKey(new[] { N(0, 1, 69), N(1, 1, 71) }, Ctx(), minor, 2)), Is.EqualTo(new[] { 72, 74 }));
        }

        [Test]
        public void QuantizeStrengthSwingAndEnds()
        {
            var notes = new[] { N(0.1, 0.5, 60), N(0.2, 0.5, 62), N(1.6, 0.25, 64) };
            var full = Transforms.Quantize(notes, Ctx(), 0.25);
            Assert.That(Starts(full), Is.EqualTo(new[] { 0, 0.25, 1.5 }));
            Assert.That(Lengths(full), Is.EqualTo(new[] { 0.5, 0.5, 0.25 }));

            Assert.That(Starts(Transforms.Quantize(notes, Ctx(), 0.25, strength: 0.5)), Is.EqualTo(new[] { 0.05, 0.225, 1.55 }));

            // Swing 1: the off-beat 16th moves to the triplet position, a third of a beat; on-beats stay.
            var swung = Transforms.Quantize(new[] { N(0.27, 0.1, 60), N(0.5, 0.1, 62), N(0.8, 0.1, 64) }, Ctx(), 0.25, swing: 1);
            Assert.That(swung.Select(n => n.Time), Is.EqualTo(new[] { 320, 480, 800 }));
            var half = Transforms.Quantize(new[] { N(0.25, 0.1, 60) }, Ctx(), 0.25, swing: 0.5);
            Assert.That(half[0].Time, Is.EqualTo(280));

            var ends = Transforms.Quantize(new[] { N(0.1, 0.5, 60), N(1.02, 0.05, 62) }, Ctx(), 0.25, ends: true);
            Assert.That(Starts(ends), Is.EqualTo(new[] { 0, 1 }));
            Assert.That(Lengths(ends), Is.EqualTo(new[] { 0.5, 0.25 }), "a note snapped to nothing keeps one grid step");
            var endsOnly = Transforms.Quantize(new[] { N(0.1, 0.5, 60) }, Ctx(), 0.25, starts: false, ends: true);
            Assert.That(endsOnly[0].Time, Is.EqualTo(T(0.1)));
            Assert.That(endsOnly[0].End, Is.EqualTo(T(0.5)));

            // A selection: only notes from beat 1 move.
            Assert.That(Starts(Transforms.Quantize(notes, Ctx(), 0.25, selection: new NoteSelection(From: T(1)))), Is.EqualTo(new[] { 0.1, 0.2, 1.5 }));
        }

        [Test]
        public void HumanizeIsSeededAndBounded()
        {
            var notes = Enumerable.Range(0, 8).Select(i => N(i, 0.5, 60 + i)).ToList();
            var a = Transforms.Humanize(notes, Ctx(), 0.05, 10, seed: 7);
            var b = Transforms.Humanize(notes, Ctx(), 0.05, 10, seed: 7);
            var c = Transforms.Humanize(notes, Ctx(), 0.05, 10, seed: 8);
            Assert.That(a, Is.EqualTo(b));
            Assert.That(a, Is.Not.EqualTo(c));
            Assert.That(a, Is.Not.EqualTo(notes));
            for (int i = 0; i < notes.Count; i++)
            {
                Assert.That(Math.Abs(a[i].Time - notes[i].Time), Is.AtMost(48), $"note {i} timing");
                Assert.That(Math.Abs(a[i].Velocity - 100), Is.AtMost(10), $"note {i} velocity");
                Assert.That(a[i].Duration, Is.EqualTo(notes[i].Duration));
                Assert.That(a[i].Time, Is.AtLeast(0));
            }
            var onlyVelocity = Transforms.Humanize(notes, Ctx(), 0, 5, seed: 3);
            Assert.That(onlyVelocity.Select(n => n.Time), Is.EqualTo(notes.Select(n => n.Time)));
        }

        [Test]
        public void VelocitySetScaleAddRampAccent()
        {
            var notes = Enumerable.Range(0, 5).Select(i => N(i, 0.5, 60)).ToList();
            Assert.That(Transforms.SetVelocity(notes, Ctx(), 90).Select(n => n.Velocity), Is.EqualTo(new[] { 90, 90, 90, 90, 90 }));

            var ctx = Ctx();
            Assert.That(Transforms.ScaleVelocity(notes, ctx, 1.5).Select(n => n.Velocity), Is.EqualTo(new[] { 127, 127, 127, 127, 127 }));
            Assert.That(Problems(ctx), Does.Contain("5 velocities went above 127 and were limited to 127"));
            Assert.That(Transforms.ScaleVelocity(notes, Ctx(), 0.8).Select(n => n.Velocity), Is.EqualTo(new[] { 80, 80, 80, 80, 80 }));

            ctx = Ctx();
            Assert.That(Transforms.AddVelocity(notes, ctx, -120, new NoteSelection(To: T(1))).Select(n => n.Velocity), Is.EqualTo(new[] { 1, 100, 100, 100, 100 }));
            Assert.That(Problems(ctx), Does.Contain("1 velocity went below 1"));

            Assert.That(Transforms.RampVelocity(notes.Take(4).ToList(), Ctx(), 40, 100).Select(n => n.Velocity), Is.EqualTo(new[] { 40, 60, 80, 100 }));
            Assert.That(Transforms.RampVelocity(notes, Ctx(), 100, 60, new NoteSelection(From: T(2))).Select(n => n.Velocity), Is.EqualTo(new[] { 100, 100, 100, 80, 60 }));

            Assert.That(Transforms.Accent(notes, Ctx(), "x...", 1, 20).Select(n => n.Velocity), Is.EqualTo(new[] { 120, 100, 100, 100, 120 }));
            var eighths = Enumerable.Range(0, 8).Select(i => N(i * 0.5, 0.25, 42, 80)).ToList();
            Assert.That(Transforms.Accent(eighths, Ctx(), "x..x..x.", 0.5, 30).Select(n => n.Velocity), Is.EqualTo(new[] { 110, 80, 80, 110, 80, 80, 110, 80 }));
            Assert.That(Assert.Throws<ToolError>(() => Transforms.Accent(notes, Ctx(), "x?x", 1, 20))!.Message, Does.Contain("only hold x"));
        }

        [Test]
        public void ShiftKeepsNotesInsideThePattern()
        {
            Assert.That(Starts(Transforms.Shift(new[] { N(0, 1, 60), N(1, 1, 62) }, Ctx(), 1)), Is.EqualTo(new[] { 1, 2 }));

            var ctx = Ctx();
            var r = Transforms.Shift(new[] { N(15.5, 0.5, 60), N(0, 1, 62) }, ctx, 1);
            Assert.That(Starts(r), Is.EqualTo(new[] { 1 }));
            Assert.That(Problems(ctx), Does.Contain("1 note ended up outside the pattern (beats 0–16) and was removed: C4 at beat 16.5"));

            ctx = Ctx();
            r = Transforms.Shift(new[] { N(14, 1.5, 60) }, ctx, 1.5);
            Assert.That(Starts(r), Is.EqualTo(new[] { 15.5 }));
            Assert.That(Lengths(r), Is.EqualTo(new[] { 0.5 }));
            Assert.That(Problems(ctx), Does.Contain("ran past the pattern's end (beat 16) and was shortened"));

            ctx = Ctx();
            r = Transforms.Shift(new[] { N(0, 2, 60) }, ctx, -1);
            Assert.That(Starts(r), Is.EqualTo(new[] { 0 }));
            Assert.That(Lengths(r), Is.EqualTo(new[] { 1 }));
            Assert.That(Problems(ctx), Does.Contain("started before the pattern"));
        }

        [Test]
        public void StretchAroundAnAnchor()
        {
            var r = Transforms.Stretch(new[] { N(0, 0.5, 60), N(1, 0.5, 62) }, Ctx(), 2);
            Assert.That(Starts(r), Is.EqualTo(new[] { 0, 2 }));
            Assert.That(Lengths(r), Is.EqualTo(new[] { 1, 1 }));
            Assert.That(Starts(Transforms.Stretch(new[] { N(1, 0.5, 60), N(2, 0.5, 62) }, Ctx(), 2, anchorBeats: 1)), Is.EqualTo(new[] { 1, 3 }));
            var half = Transforms.Stretch(new[] { N(4, 1, 60), N(6, 1, 62) }, Ctx(), 0.5);
            Assert.That(Starts(half), Is.EqualTo(new[] { 4, 5 }));
            Assert.That(Lengths(half), Is.EqualTo(new[] { 0.5, 0.5 }));
        }

        [Test]
        public void ReverseInThePatternOrTheRange()
        {
            var r = Transforms.Reverse(new[] { N(0, 1, 60), N(2, 0.5, 62) }, Ctx());
            Assert.That(r.Select(n => (NoteList.Beats(n.Time, Tpb), n.Pitch)), Is.EqualTo(new[] { (13.5, 62), (15.0, 60) }));

            var notes = new List<NoteEvent> { N(0, 1, 60), N(1, 1, 62), N(8, 1, 64) };
            var range = new NoteSelection(From: 0, To: T(4));
            var once = Transforms.Reverse(notes, Ctx(), range);
            Assert.That(once.Select(n => (NoteList.Beats(n.Time, Tpb), n.Pitch)), Is.EqualTo(new[] { (2.0, 62), (3.0, 60), (8.0, 64) }));
            Assert.That(Transforms.Reverse(once, Ctx(), range), Is.EqualTo(notes));
        }

        [Test]
        public void InvertAroundAPitchRoundTrips()
        {
            var notes = new List<NoteEvent> { N(0, 1, 60), N(1, 1, 67), N(2, 1, 71, 90) };
            var once = Transforms.Invert(notes, Ctx(), 64);
            Assert.That(Pitches(once), Is.EqualTo(new[] { 68, 61, 57 }));
            Assert.That(Transforms.Invert(once, Ctx(), 64), Is.EqualTo(notes));
            Assert.That(Pitches(Transforms.Invert(notes, Ctx())), Is.EqualTo(new[] { 60, 53, 49 }), "default axis: the first note");
            Assert.That(Transforms.Invert(Transforms.Invert(notes, Ctx()), Ctx()), Is.EqualTo(notes));

            var ctx = Ctx();
            Assert.That(Pitches(Transforms.Invert(new[] { N(0, 1, 60) }, ctx, 120)), Is.EqualTo(new[] { 120 }));
            Assert.That(Problems(ctx), Does.Contain("left MIDI's range"));
        }

        [Test]
        public void LegatoAndStaccato()
        {
            var notes = new[] { N(0, 0.25, 60), N(1, 0.25, 62), N(2, 0.25, 64), N(2, 0.25, 67), N(3, 0.5, 65) };
            Assert.That(Lengths(Transforms.Legato(notes, Ctx())), Is.EqualTo(new[] { 1, 1, 1, 1, 0.5 }));
            Assert.That(Lengths(Transforms.Legato(notes, Ctx(), 0.25)), Is.EqualTo(new[] { 0.75, 0.75, 0.75, 0.75, 0.5 }));
            Assert.That(Lengths(Transforms.Legato(notes, Ctx(), selection: new NoteSelection(Low: 64))), Is.EqualTo(new[] { 0.25, 0.25, 1, 1, 0.5 }));

            var long_ = new[] { N(0, 1, 60), N(1, 0.1, 62) };
            Assert.That(Lengths(Transforms.Staccato(long_, Ctx())), Is.EqualTo(new[] { 0.5, 0.05 }));
            Assert.That(Lengths(Transforms.Staccato(long_, Ctx(), lengthBeats: 0.25)), Is.EqualTo(new[] { 0.25, 0.1 }));
        }

        [Test]
        public void SelectionsCombineTimeAndPitch()
        {
            var notes = new[] { N(0, 1, 60), N(1, 1, 72), N(2, 1, 60), N(3, 1, 72) };
            var sel = new NoteSelection(From: T(1), To: T(3), Low: 70);
            Assert.That(Pitches(Transforms.Transpose(notes, Ctx(), 1, sel)), Is.EqualTo(new[] { 60, 73, 60, 72 }));
            Assert.That(sel.Contains(notes[1]), Is.True);
            Assert.That(sel.Contains(notes[3]), Is.False, "range_end is exclusive");
            Assert.That(sel.Describe(Ctx()), Is.EqualTo("notes starting at beats 1–3 with pitches A#4–G9"));
        }

        // ---- notes_transform ----

        static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

        static readonly List<NoteEvent> Riff = new() { N(0, 1, 60), N(1, 1, 62), N(2, 1, 64) };

        static string Fails(string json) =>
            Assert.Throws<ToolError>(() => NotesTransform.Apply(Args(json), Riff, Tpb, PatternTicks))!.Message;

        [Test]
        public void ApplyFollowsTheSelectionThroughEveryTransform()
        {
            var o = NotesTransform.Run(Args("""
                {"machine": "Keys", "pattern": "A", "range_start": 0, "range_end": 2,
                 "transforms": [{"op": "transpose", "semitones": 12}, {"op": "shift", "beats": 1}]}
                """), Riff, Tpb, PatternTicks);
            Assert.That(o.Notes.Select(n => (NoteList.Beats(n.Time, Tpb), n.Pitch)), Is.EqualTo(new[] { (1.0, 72), (2.0, 64), (2.0, 74) }));
            Assert.That(o.Selected, Is.EqualTo(2));
            Assert.That(o.Changed, Is.EqualTo(2));
            Assert.That(o.Removed, Is.EqualTo(0));
            Assert.That(o.Steps, Is.EqualTo(new[] { "transpose +12 semitones (2 notes)", "shift +1 beats (2 notes)" }));
            Assert.That(o.Problems, Is.Empty);

            // A transform's own selection picks again, from the notes as they are then.
            var (result, problems) = NotesTransform.Apply(Args("""
                {"transforms": [{"op": "transpose", "semitones": 2}, {"op": "velocity", "set": 50, "pitch_low": "F#4"}]}
                """), Riff, Tpb, PatternTicks);
            Assert.That(result.Select(n => (n.Pitch, n.Velocity)), Is.EqualTo(new[] { (62, 100), (64, 100), (66, 50) }));
            Assert.That(problems, Is.Empty);

            // "bar.beat" positions count bars of beatsPerBar.
            (result, _) = NotesTransform.Apply(Args("""{"range_start": "1.2", "transforms": [{"op": "transpose", "scale_steps": 1, "key": "C major"}]}"""), Riff, Tpb, PatternTicks);
            Assert.That(Pitches(result), Is.EqualTo(new[] { 60, 64, 65 }));
        }

        [Test]
        public void ApplyRoundTripsAndReportsClips()
        {
            var (twice, problems) = NotesTransform.Apply(Args("""{"transforms": [{"op": "invert", "axis": "C4"}, {"op": "invert", "axis": 60}]}"""), Riff, Tpb, PatternTicks);
            Assert.That(twice, Is.EqualTo(Riff));
            Assert.That(problems, Is.Empty);
            (twice, _) = NotesTransform.Apply(Args("""{"transforms": [{"op": "reverse"}, {"op": "reverse"}]}"""), Riff, Tpb, PatternTicks);
            Assert.That(twice, Is.EqualTo(Riff));

            var o = NotesTransform.Run(Args("""{"transforms": [{"op": "shift", "beats": 14.5}, {"op": "velocity", "add": 40}]}"""), Riff, Tpb, PatternTicks);
            Assert.That(o.Notes.Count, Is.EqualTo(2));
            Assert.That(o.Removed, Is.EqualTo(1));
            Assert.That(string.Join(" ", o.Problems), Does.Contain("transforms[0] (shift): 1 note ended up outside the pattern"));
            Assert.That(string.Join(" ", o.Problems), Does.Contain("transforms[0] (shift): 1 note ran past the pattern's end"));
            Assert.That(string.Join(" ", o.Problems), Does.Contain("transforms[1] (velocity): 2 velocities went above 127"));

            var none = NotesTransform.Run(Args("""{"pitch_low": 100, "transforms": [{"op": "legato"}]}"""), Riff, Tpb, PatternTicks);
            Assert.That(none.Changed, Is.EqualTo(0));
            Assert.That(string.Join(" ", none.Problems), Does.Contain("Nothing matched the selection (notes with pitches E7–G9)"));
        }

        [Test]
        public void EveryOpRunsFromArguments()
        {
            string[] calls =
            {
                """{"op": "transpose", "semitones": -12}""",
                """{"op": "transpose", "scale_steps": 2, "key": "A minor"}""",
                """{"op": "quantize", "grid": 0.75, "strength": 0.5, "swing": 0.3, "ends": true}""",
                """{"op": "humanize", "timing": 0.02, "velocity": 6, "seed": 4}""",
                """{"op": "velocity", "set": 70}""",
                """{"op": "velocity", "scale": 0.9}""",
                """{"op": "velocity", "add": -5}""",
                """{"op": "velocity", "ramp": [50, 110]}""",
                """{"op": "velocity", "accent": "x.x.", "grid": 0.5, "amount": 15}""",
                """{"op": "shift", "beats": 0.5}""",
                """{"op": "stretch", "factor": 1.5, "anchor": 1}""",
                """{"op": "reverse", "range_start": 0, "range_end": 4}""",
                """{"op": "invert"}""",
                """{"op": "legato", "gap": 0.1}""",
                """{"op": "staccato", "factor": 0.3}""",
                """{"op": "staccato", "length": 0.25}""",
            };
            foreach (var step in calls)
            {
                var args = Args($$"""{"machine": "Keys", "pattern": "A", "transforms": [{{step}}]}""");
                var errors = new List<string>();
                Schema.Validate(NotesTransform.Input, args, "", errors);
                Assert.That(errors, Is.Empty, step);
                var o = NotesTransform.Run(args, Riff, Tpb, PatternTicks);
                Assert.That(o.Notes.Count, Is.EqualTo(3), step);
                Assert.That(o.Changed, Is.GreaterThan(0), step);
                var result = NotesTransform.Result("Keys", "A", o, Tpb);
                Schema.Validate(NotesTransform.Output, result.Structured, "", errors);
                Assert.That(errors, Is.Empty, step);
                Assert.That(result.Summary, Does.StartWith("Keys \"A\": "));
            }
        }

        [Test]
        public void ArgumentErrorsSayWhatToDo()
        {
            Assert.That(Fails("""{"transforms": [{"op": "transpos", "semitones": 1}]}"""), Does.Contain("Did you mean \"transpose\""));
            Assert.That(Fails("""{"transforms": [{"op": "quantize", "grid": 0.25, "semitones": 1}]}"""), Does.Contain("\"semitones\" doesn't apply (\"semitones\" belongs to transpose); quantize takes grid"));
            Assert.That(Fails("""{"transforms": [{"op": "quantize"}]}"""), Does.Contain("needs grid, in beats"));
            Assert.That(Fails("""{"transforms": [{"op": "velocity", "set": 90, "add": 4}]}"""), Does.Contain("exactly one of set"));
            Assert.That(Fails("""{"transforms": [{"op": "velocity", "set": 90, "grid": 1}]}"""), Does.Contain("only go with accent"));
            Assert.That(Fails("""{"transforms": [{"op": "transpose"}]}"""), Does.Contain("give semitones"));
            Assert.That(Fails("""{"transforms": [{"op": "transpose", "scale_steps": 2}]}"""), Does.Contain("needs key"));
            Assert.That(Fails("""{"transforms": [{"op": "transpose", "scale_steps": 2, "key": "C dorain"}]}"""), Does.Contain("Did you mean \"C dorian\""));
            Assert.That(Fails("""{"transforms": [{"op": "humanize"}]}"""), Does.Contain("both are 0"));
            Assert.That(Fails("""{"transforms": [{"op": "stretch", "factor": 0}]}"""), Does.Contain("more than 0"));
            Assert.That(Fails("""{"transforms": [{"op": "velocity", "ramp": [10]}]}"""), Does.Contain("ramp is [from, to]"));
            Assert.That(Fails("""{"range_start": 4, "range_end": 2, "transforms": [{"op": "reverse"}]}"""), Does.Contain("range_end must be after range_start"));
            Assert.That(Fails("""{"pitch_low": "C5", "pitch_high": "C4", "transforms": [{"op": "reverse"}]}"""), Does.Contain("swap them"));
            Assert.That(Fails("""{"transforms": []}"""), Does.Contain("give a list of transforms"));
            Assert.That(Fails("""{"transforms": [{"op": "invert", "axis": "C-5"}]}"""), Does.Contain("Buzz pattern notation"));

            var errors = new List<string>();
            Schema.Validate(NotesTransform.Input, Args("""{"machine": "K", "pattern": "A", "transforms": [{"op": "shift", "beat": 1}], "range": 2}"""), "", errors);
            Assert.That(string.Join(" ", errors), Does.Contain("transforms[0].beat: unknown argument"));
            Assert.That(string.Join(" ", errors), Does.Contain("range: unknown argument"));
        }

        [Test]
        public void TheToolTakesTheHostsHandler()
        {
            var tools = new ToolRegistry();
            CompositionTools.Register(tools, c => Task.FromResult(ToolResult.Ok("done")));
            var tool = tools.Find(NotesTransform.Name)!;
            Assert.That(tool.Output, Is.Not.Null);
            Assert.That(tool.Annotations.ReadOnly, Is.False);
            Assert.That(tool.Description, Does.Contain("Example: notes_transform("));
            Assert.That(tools.Find("theory")!.Annotations.ReadOnly, Is.True);
            var registry = new ToolRegistry();
            CompositionTools.Register(registry);
            Assert.That(registry.Find(NotesTransform.Name), Is.Null, "without a handler only theory is registered");
        }
    }
}
