using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using static ReBuzzTests.SessionClipTests;
using FollowSettings = ReBuzz.Core.Session.SessionDocument.FollowSettings;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: performance (docs/specs/session-view.md 0.6) through the real audio loop:
    /// clip and scene follow actions (timing, precedence, the song's switch), scenes stopping
    /// other clips, launch undo, the crossfader, tempo nudge and tap tempo, their MIDI targets,
    /// the clip panel's follow and scene sections, and a 10-minute follow-action chain checked
    /// statistically. Scheduling details are unit tested in ReBuzz.SessionTests.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionPerformanceTests
    {
        const int Bar = 16;

        static float Tone(ClipRig rig) => rig.Driver.ReadStereoSamples(1).Values[0] * 32768f;   // the output is scaled to ±1

        static void Follow(ClipRig rig, IPattern pattern, Action<FollowSettings> change) =>
            rig.Set(pattern, c => { c.Follow.On = true; change(c.Follow); });

        // ------------------------------------------------------------------ clip follow actions

        [Test]
        public void ClipFollowActionsChainThroughTheGroupOnTheBeat()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            var c = rig.Pattern(rig.Machines[0], "C", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            foreach (var p in new[] { a, b, c })
                Follow(rig, p, f => f.A = "Next");               // linked: at the end of each pass

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);                     // next bar: 16
            rig.PlayTo(16);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(31);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(32);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b), "B follows on A's last tick, not a bar later");
            Assert.That(TickOf(b), Is.EqualTo(0));
            Assert.That(Tone(rig), Is.EqualTo(2000f).Within(0.5f), "the machine plays B from its first row");
            rig.PlayTo(48);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(c));
            rig.PlayTo(64);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a), "Next wraps to the first clip of the group");

            // The song's switch: skipped while off, and the chain goes on a pass later.
            rig.Layout.SetFollowActionsEnabled(false);
            rig.PlayTo(80);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.Layout.SetFollowActionsEnabled(true);
            rig.PlayTo(96);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b));
            Assert.That(rig.Layout.Document!.FollowActionsEnabled, Is.True);
        }

        [Test]
        public void UnlinkedFollowTimeAndATargetsOwnQuantization()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            Follow(rig, a, f => { f.A = "Next"; f.Linked = false; f.Time = "0.1.2"; });   // a beat and two ticks: 6
            rig.Set(b, s => s.Quantization = "Beat");            // B waits for its own quantization

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);                     // 16; follow due at 22
            rig.PlayTo(22);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a), "B has its own quantization: the next beat");
            rig.PlayTo(23);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(24);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b));
        }

        // ------------------------------------------------------------------ scenes

        [Test]
        public void SceneFollowActionLaunchesTheNextSceneOnTimeAndWinsOverClips()
        {
            using var rig = new ClipRig(2);
            var a0 = rig.Pattern(rig.Machines[0], "A0", Bar);
            var a1 = rig.Pattern(rig.Machines[0], "A1", Bar);
            var b0 = rig.Pattern(rig.Machines[1], "B0", Bar);
            var b1 = rig.Pattern(rig.Machines[1], "B1", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var t0 = rig.Track(0);
            var t1 = rig.Track(1);
            layout.SetSceneFollow(0, new FollowSettings { On = true, A = "Next", Time = "1.2.0" });    // 24 ticks
            layout.SetSceneTempo(1, 140);
            Follow(rig, a0, f => { f.A = "Stop"; f.Linked = false; f.Time = "1.2.0"; });            // due with the scene's

            rig.PlayTo(1);
            layout.LaunchScene(0);                               // 16; scene follow due at 40
            rig.PlayTo(39);
            Assert.That(rig.Session.GetPlaying(t0), Is.SameAs(a0));
            Assert.That(rig.Session.GetPlaying(t1), Is.SameAs(b0));
            rig.PlayTo(40);
            Assert.That(rig.Session.GetPlaying(t0), Is.SameAs(a1), "the scene's follow action wins over the clip's Stop");
            Assert.That(rig.Session.GetPlaying(t1), Is.SameAs(b1));
            Assert.That(rig.Core.BPM, Is.EqualTo(140), "the scene it launches sets its tempo, on time");
            rig.PlayTo(80);
            Assert.That(rig.Session.GetPlaying(t0), Is.SameAs(a1), "scene 2 has no follow action: it plays on");
        }

        [Test]
        public void ScenesStopTheClipsOfTracksTheyHaveNoClipFor()
        {
            using var rig = new ClipRig(3);
            var a0 = rig.Pattern(rig.Machines[0], "A0", Bar);
            var b0 = rig.Pattern(rig.Machines[1], "B0", Bar);
            var b1 = rig.Pattern(rig.Machines[1], "B1", Bar);
            var c0 = rig.Pattern(rig.Machines[2], "C0", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var tracks = Enumerable.Range(0, 3).Select(rig.Track).ToArray();

            rig.PlayTo(1);
            layout.LaunchScene(0);
            rig.PlayTo(16);
            layout.LaunchScene(1);                               // off (the default): the others play on
            rig.PlayTo(32);
            Assert.That(tracks.Select(t => rig.Session.GetPlaying(t)), Is.EqualTo(new IPattern[] { a0, b1, c0 }));

            layout.SetScenesStopOtherClips(true);
            layout.LaunchScene(0);
            rig.PlayTo(48);
            layout.LaunchScene(1);                               // on: tracks 1 and 3 stop on the same bar line
            rig.PlayTo(63);
            Assert.That(tracks.Select(t => rig.Session.GetPlaying(t)), Is.EqualTo(new IPattern[] { a0, b0, c0 }));
            rig.PlayTo(64);
            Assert.That(tracks.Select(t => rig.Session.GetPlaying(t)), Is.EqualTo(new IPattern[] { null, b1, null }));

            ((SessionCore)rig.Session).UndoLaunch();             // undone: all three come back, in step
            rig.PlayTo(80);
            Assert.That(tracks.Select(t => rig.Session.GetPlaying(t)), Is.EqualTo(new IPattern[] { a0, b0, c0 }));
            Assert.That(rig.Session.GetClipStartClock(tracks[0]), Is.EqualTo(48));
        }

        // ------------------------------------------------------------------ launch undo

        [Test]
        public void UndoLaunchBringsThePreviousClipBackInStep()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.SetLaunchQuantization(LaunchQuantization.Beat);
            var track = rig.Track(0);
            var core = (SessionCore)rig.Session;
            Assert.That(core.LastLaunch, Is.Null);

            rig.PlayTo(1);
            layout.LaunchClip(track, a);                         // 4
            rig.PlayTo(9);
            layout.LaunchClip(track, b);                         // 12
            rig.PlayTo(14);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b));
            Assert.That(core.LastLaunch, Is.EqualTo("B on Tone0"));

            Assert.That(core.UndoLaunch(), Is.EqualTo("B on Tone0"));     // A again from the next beat, where it would be
            rig.PlayTo(16);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            Assert.That(TickOf(a), Is.EqualTo(12), "in step with its launch at 4");
            Assert.That(Tone(rig), Is.EqualTo(1120f).Within(0.5f));

            Assert.That(core.UndoLaunch(), Is.EqualTo("A on Tone0"));     // nothing before it: the track stops
            rig.PlayTo(20);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
            Assert.That(core.LastLaunch, Is.Null);

            layout.LaunchClip(track, b);                         // still waiting: cancelled
            rig.PlayTo(22);
            core.UndoLaunch();
            rig.PlayTo(28);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
        }

        // ------------------------------------------------------------------ crossfader

        [Test]
        public void CrossfaderFadesBetweenTheTracksOfEachSide()
        {
            using var rig = new ClipRig(3, hum: true);
            var layout = rig.Layout;
            var p = rig.Machines.Select((m, i) => rig.Pattern(m, "P" + i, Bar)).ToArray();
            layout.Ensure();
            layout.SetCrossfade(rig.Machines[0], "A");
            layout.SetCrossfade(rig.Machines[1], "B");           // the third is on neither side
            for (int i = 0; i < 3; i++)
                rig.Set(p[i], c => c.Quantization = "None");

            rig.PlayTo(1);
            layout.LaunchScene(0);
            rig.Ticks(2);
            double each = rig.Level() / 3;                       // centre, dipless: all three at full volume
            Assert.That(each, Is.GreaterThan(0));

            double LevelAt(double position, string curve = "Dipless")
            {
                layout.SetCrossfaderCurve(curve);
                layout.SetCrossfaderPosition(position);
                rig.Ticks(2);                                    // a block to ramp
                return rig.Level() / each;
            }

            Assert.That(LevelAt(0), Is.EqualTo(2).Within(0.01), "all A: B silent");
            Assert.That(LevelAt(1), Is.EqualTo(2).Within(0.01), "all B: A silent");
            Assert.That(LevelAt(0.75), Is.EqualTo(2.5).Within(0.01), "dipless: A at half");
            Assert.That(LevelAt(0.5, "Constant power"), Is.EqualTo(1 + 2 * Math.Sqrt(0.5)).Within(0.01));
            Assert.That(LevelAt(0.5, "Fast cut"), Is.EqualTo(3).Within(0.01));
            Assert.That(LevelAt(0.98, "Fast cut"), Is.EqualTo(2.4).Within(0.01));

            // From MIDI: a CC across its range, and the song keeps the position.
            layout.SetCrossfaderCurve("Dipless");
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Crossfader, 0, 0);
            rig.Core.SendMIDIInput(ControlChange(14, 64));       // learnt
            rig.Core.SendMIDIInput(ControlChange(14, 0));
            rig.Ticks(2);
            Assert.That(rig.Level() / each, Is.EqualTo(2).Within(0.01));
            Assert.That(layout.Document!.CrossfaderPosition, Is.EqualTo(0));

            // Unassigned: back to full volume.
            layout.SetCrossfade(rig.Machines[1], null);
            rig.Ticks(2);
            Assert.That(rig.Level() / each, Is.EqualTo(3).Within(0.01));
            Assert.That(rig.Machines[1].CrossfadeGain, Is.EqualTo(1f));
        }

        // ------------------------------------------------------------------ tempo

        /// <summary>
        /// Length in samples of the next whole tick: finishes the current one (whose length was
        /// set when it started), then counts the next sample by sample.
        /// </summary>
        static int MeasureTick(ClipRig rig)
        {
            static void ToNextTick(ClipRig rig, ref int samples)
            {
                long clock = rig.Session.Clock;
                while (rig.Session.Clock == clock && samples < 1000000)
                {
                    rig.Driver.ReadStereoSamples(1);
                    samples++;
                }
            }
            int skipped = 0, samples = 0;
            ToNextTick(rig, ref skipped);
            ToNextTick(rig, ref samples);
            return samples;
        }

        [Test]
        public void NudgeStretchesTicksWhileHeldAndKeepsTheTempo()
        {
            using var rig = new ClipRig(1);
            rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var core = (SessionCore)rig.Session;
            int bpm = rig.Core.BPM;
            rig.PlayTo(4);
            int normal = MeasureTick(rig);
            Assert.That(normal, Is.EqualTo(rig.SamplesPerTick).Within(1));

            core.Nudge(1);
            Assert.That(MeasureTick(rig), Is.EqualTo(normal / (1 + SessionCore.NudgeAmount)).Within(2), "faster: shorter ticks");
            core.Nudge(-1);
            Assert.That(MeasureTick(rig), Is.EqualTo(normal / (1 - SessionCore.NudgeAmount)).Within(2), "slower: longer ticks");
            core.Nudge(0);
            Assert.That(MeasureTick(rig), Is.EqualTo(normal).Within(1));
            Assert.That(rig.Core.BPM, Is.EqualTo(bpm), "the song's tempo doesn't change");

            // From MIDI: while the note is held.
            rig.Core.SessionMidi.BeginLearn(SessionTarget.NudgeUp, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(40, 100));             // learnt
            rig.Core.SendMIDIInput(NoteOn(40, 100));
            Assert.That(rig.Core.TempoNudge, Is.EqualTo(1 + (float)SessionCore.NudgeAmount));
            rig.Core.SendMIDIInput(NoteOff(40));
            Assert.That(rig.Core.TempoNudge, Is.EqualTo(1f));
        }

        [Test]
        public void TapTempoFollowsTheTaps()
        {
            using var rig = new ClipRig(1);
            rig.Layout.Ensure();
            rig.Core.BPM = 100;
            long f = Stopwatch.Frequency;
            long t = Stopwatch.GetTimestamp();
            Assert.That(rig.Layout.TapTempo(t), Is.Null);
            Assert.That(rig.Layout.TapTempo(t + f / 2), Is.Null);
            Assert.That(rig.Layout.TapTempo(t + f), Is.EqualTo(120), "from the third tap");
            Assert.That(rig.Core.BPM, Is.EqualTo(120));
            Assert.That(rig.Layout.TapTempo(t + f + f * 4 / 10), Is.EqualTo(129), "the average interval: 1.4 s / 3");
            Assert.That(rig.Layout.TapTempo(t + f * 5), Is.Null, "a pause starts again");
            Assert.That(rig.Core.BPM, Is.EqualTo(129));

            // A bouncing pad (MIDI, taps at once) can't send the tempo flying.
            rig.Core.SessionMidi.BeginLearn(SessionTarget.TapTempo, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(41, 100));             // learnt
            for (int i = 0; i < 4; i++)
                rig.Core.SendMIDIInput(NoteOn(41, 100));
            Assert.That(rig.Core.BPM, Is.EqualTo(129));
        }

        // ------------------------------------------------------------------ MIDI

        [Test]
        public void UndoLaunchAndTheFollowSwitchFromMidi()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var track = rig.Track(0);
            var midi = rig.Core.SessionMidi;

            midi.BeginLearn(SessionTarget.UndoLaunch, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(50, 100));
            midi.BeginLearn(SessionTarget.FollowActions, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(51, 100));
            Assert.That(midi.Describe(SessionTarget.UndoLaunch, 0, 0), Is.Not.Null);

            rig.PlayTo(1);
            layout.LaunchClip(track, a);
            rig.PlayTo(16);
            layout.LaunchClip(track, b);
            rig.PlayTo(20);
            rig.Core.SendMIDIInput(NoteOn(50, 100));             // undo: B never starts
            rig.PlayTo(33);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));

            rig.Core.SendMIDIInput(NoteOn(51, 100));
            Assert.That(layout.Document!.FollowActionsEnabled, Is.False);
            Assert.That(((SessionCore)rig.Session).FollowActionsEnabled, Is.False);
            rig.Core.SendMIDIInput(NoteOn(51, 100));
            Assert.That(layout.Document!.FollowActionsEnabled, Is.True);
        }

        // ------------------------------------------------------------------ the view

        [Test]
        public void ClipPanelEditsFollowActionsAndTheScene()
        {
            using var rig = new ClipRig(3);
            var beat = rig.Pattern(rig.Machines[0], "Beat", Bar);
            rig.Pattern(rig.Machines[0], "Fill", Bar);
            rig.Pattern(rig.Machines[1], "Bass", 2 * Bar);
            rig.Pattern(rig.Machines[2], "Hook", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.SetShowClipPanel(true);
            layout.SetShowMixer(true);
            layout.SetCrossfade(rig.Machines[0], "A");
            layout.SetCrossfade(rig.Machines[1], "B");
            layout.SetCrossfaderPosition(0.3);

            var view = new SessionView(rig.Core);
            view.Rebuild();
            view.Select(0, 0);
            var panel = view.ClipPanelView;
            var follow = panel.ClipFollow;

            Click(follow.OnBox, true);
            Assert.That(layout.ClipOf(beat).Follow.On, Is.True);
            view.Rebuild();
            follow.ABox.SelectedIndex = Array.FindIndex(SessionView.ClipPanel.FollowEditor.Actions, x => x.Value == "Other");
            Assert.That(layout.ClipOf(beat).Follow.A, Is.EqualTo("Other"));
            view.Rebuild();
            follow.ChanceBar.SetByUser(0.7);
            Assert.That(layout.ClipOf(beat).Follow.ChanceA, Is.EqualTo(70));
            view.Rebuild();
            Click(follow.LinkedBox, false);
            view.Rebuild();
            follow.TimeBox.Text = "0.2.0";
            follow.CommitTypedFields();
            Assert.That(layout.ClipOf(beat).Follow.Linked, Is.False);
            Assert.That(layout.ClipOf(beat).Follow.Time, Is.EqualTo("0.2.0"));
            view.Rebuild();
            follow.TimeBox.Text = "two bars";
            follow.CommitTypedFields();
            Assert.That(layout.ClipOf(beat).Follow.Time, Is.EqualTo("0.2.0"), "not a time: kept");

            // The scene of the selected slot.
            view.Rebuild();
            panel.SceneTempoBox.Text = "128";
            panel.CommitSceneFields();
            Assert.That(layout.Document!.Scenes[0].Tempo, Is.EqualTo(128));
            view.Rebuild();
            Click(panel.SceneFollow.OnBox, true);
            Assert.That(layout.Document!.Scenes[0].Follow.On, Is.True);
            view.Rebuild();
            Assert.That(panel.SceneFollow.LinkedBox.Parent, Is.Null, "scene follow actions aren't linked to anything");

            layout.Undo();
            view.Rebuild();
            Assert.That(layout.Document!.Scenes[0].Follow.On, Is.False, "panel edits undo");

            layout.LaunchScene(0);
            rig.Ticks(3);
            view.RefreshState();
            SessionViewTests.Screenshot(view, "session-view-performance", 1180, 760);
        }

        static void Click(System.Windows.Controls.CheckBox box, bool check)
        {
            box.IsChecked = check;
            box.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        }

        // ------------------------------------------------------------------ acceptance

        /// <summary>
        /// Acceptance (spec 0.6): a 4-clip chain of "Other" follow actions plays for 10 minutes
        /// at 120 BPM with a seeded generator. Every switch lands exactly at the end of a clip,
        /// the track never falls silent, the machine always plays the clip the session says, and
        /// the transitions are uniform (chi-square, 95%).
        /// </summary>
        [Test]
        public void TenMinuteFollowActionChainIsOnTimeAndUniform()
        {
            const int ClipTicks = 4;
            using var rig = new ClipRig(1);
            var clips = Enumerable.Range(0, 4).Select(i => rig.Pattern(rig.Machines[0], "C" + i, ClipTicks)).ToArray();
            var layout = rig.Layout;
            layout.Ensure();
            var track = rig.Track(0);
            foreach (var c in clips)
                Follow(rig, c, f => f.A = "Other");
            rig.Core.BPM = 120;
            var core = (SessionCore)rig.Session;
            core.SeedFollowActions(2026);

            int totalTicks = 120 * rig.Core.TPB * 10;            // 10 minutes
            rig.PlayTo(1);
            layout.LaunchClip(track, clips[0]);                  // the next bar: 16
            rig.PlayTo(Bar);
            var counts = new int[4, 4];
            int transitions = 0;
            var previous = clips[0];
            for (long t = Bar + 1; t <= totalTicks; t++)
            {
                rig.PlayTo(t);
                var playing = rig.Session.GetPlaying(track) as PatternCore;
                Assert.That(playing, Is.Not.Null, $"silent at {t}");
                if (!ReferenceEquals(playing, previous))
                {
                    Assert.That((t - Bar) % ClipTicks, Is.Zero, $"switched off the beat at {t}");
                    int from = Array.IndexOf(clips, previous), to = Array.IndexOf(clips, playing);
                    Assert.That(to, Is.Not.EqualTo(from));
                    counts[from, to]++;
                    transitions++;
                    previous = playing;
                }
                else
                {
                    Assert.That((t - Bar) % ClipTicks, Is.Not.Zero, $"no switch at {t}");
                }
                int index = Array.IndexOf(clips, playing);
                Assert.That(Tone(rig), Is.EqualTo((index + 1) * 1000 + 10 * TickOf(playing)!.Value).Within(0.5f), $"the machine plays {playing!.Name} at {t}");
            }

            // Chi-square over the 12 transitions (each row: 3 equally likely targets; 8 degrees of freedom).
            double chi = 0;
            for (int from = 0; from < 4; from++)
            {
                int row = Enumerable.Range(0, 4).Sum(to => counts[from, to]);
                for (int to = 0; to < 4; to++)
                {
                    if (to == from) continue;
                    double expected = row / 3.0;
                    chi += (counts[from, to] - expected) * (counts[from, to] - expected) / expected;
                }
            }
            TestContext.Out.WriteLine($"[notice] 10 minutes at 120 BPM, {transitions} transitions: chi-square {chi:F2} (95% critical 15.51)");
            Assert.That(transitions, Is.EqualTo((totalTicks - Bar) / ClipTicks));
            Assert.That(chi, Is.LessThan(15.507));
        }
    }
}
