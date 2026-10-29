using BuzzGUI.Interfaces;
using ReBuzz.AppViews.Session;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzzTests.Automation;
using ReBuzzTests.Automation.TestMachinesControllers;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ClipSettings = ReBuzz.Core.Session.SessionDocument.ClipSettings;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: clip and scene properties (docs/specs/session-view.md 0.5) through the real
    /// audio loop: launch modes (with mouse, keys and MIDI note on/off), per-clip quantization,
    /// legato, loop region and start marker, one-shot clips, velocity, scene tempo and time
    /// signature, and the clip panel. Scheduling details are unit tested in ReBuzz.SessionTests.
    /// </summary>
    [Apartment(ApartmentState.STA)]
    public class SessionClipTests
    {
        const int Bar = 16;
        const int TimeBase = PatternEvent.TimeBase;

        static int NoteOn(int note, int velocity) => 0x90 | (note << 8) | (velocity << 16);
        static int NoteOff(int note) => 0x80 | (note << 8);

        /// <summary>Tone generators (output shows which pattern plays and where) or Hum (constant), each with a track.</summary>
        sealed class ClipRig : IDisposable
        {
            public Driver Driver { get; } = new Driver();
            public ReBuzzCore Core => Driver.Core;
            public ISession Session => Core.Session;
            public SessionLayout Layout => Core.SessionLayout;
            public List<MachineCore> Machines { get; } = new List<MachineCore>();

            public ClipRig(int count, bool hum = false)
            {
                var controllers = Enumerable.Range(0, count)
                    .Select(i => hum ? (DynamicMachineController)HumController.NewInstance("Hum" + i) : ToneController.NewInstance("Tone" + i))
                    .ToList();
                Driver.Gear.AddDynamicGenerator(hum ? HumController.Info : ToneController.Info);
                Driver.Start();
                foreach (var c in controllers)
                {
                    Driver.MachineGraph.InsertMachineInstanceConnectedToMasterFor(c);
                    Machines.Add(Driver.MachineFor(c));
                }
                var song = Core.SongCore;
                foreach (var m in Machines)
                    if (!song.SequencesList.Any(s => s.Machine == m))
                        song.AddSequence(m, song.SequencesList.Count);
                Core.Looping = true;
            }

            public PatternCore Pattern(MachineCore machine, string name, int length)
            {
                machine.CreatePattern(name, length);
                return machine.PatternsList.Single(p => p.Name == name);
            }

            public ISequence Track(int i) => Layout.SequenceOf(i)!;

            public int SamplesPerTick => ReBuzzCore.masterInfo.SamplesPerTick;

            public void Align()
            {
                var mi = ReBuzzCore.masterInfo;
                Driver.ReadStereoSamples(mi.SamplesPerTick - mi.PosInTick);
            }

            public void Ticks(int count)
            {
                for (int i = 0; i < count; i++)
                    Driver.ReadStereoSamples(SamplesPerTick);
            }

            /// <summary>
            /// Starts the transport if needed and plays until the session clock is
            /// <paramref name="clock"/>: that tick is processed (launches, positions) and not yet
            /// rendered. Reads block by block, so a tempo change that shortens a tick can't carry
            /// it past.
            /// </summary>
            public void PlayTo(long clock)
            {
                if (!Core.PlayRequested)
                {
                    Align();
                    Core.Playing = true;
                }
                int guard = 0;
                while (Session.Clock < clock && guard++ < 1000000)
                {
                    var mi = ReBuzzCore.masterInfo;
                    Driver.ReadStereoSamples(Math.Clamp(mi.SamplesPerTick - mi.PosInTick, 1, 256));
                }
                Assert.That(Session.Clock, Is.EqualTo(clock));
            }

            /// <summary>Mean level of the next tick of output.</summary>
            public double Level()
            {
                var values = Driver.ReadStereoSamples(SamplesPerTick).Values;
                return values.Length == 0 ? 0 : values.Average(v => Math.Abs(v));
            }

            public void Set(IPattern pattern, Action<ClipSettings> change)
            {
                var c = Layout.ClipOf(pattern);
                change(c);
                Layout.SetClipSettings(pattern, c);
            }

            public void Dispose()
            {
                Driver.Dispatcher.Deferred = false;
                Core.Playing = false;
                Driver.Dispose();
            }
        }

        /// <summary>Position of a pattern in whole ticks, or null when it isn't playing.</summary>
        static int? TickOf(PatternCore p) => p.PlayPosition == int.MinValue ? null : p.PlayPosition / TimeBase;

        // ------------------------------------------------------------------ launch modes

        [Test]
        public void GateClipPlaysWhileHeldAndStopsOnItsQuantizationAfterRelease()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Mode = "Gate"; c.Quantization = "Beat"; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);                     // next beat: 4
            rig.PlayTo(4);
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Playing));
            rig.PlayTo(6);
            rig.Layout.ReleaseClip(track, a);                    // stops on the next beat: 8
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Stopping));
            rig.PlayTo(7);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);

            // From MIDI: note on launches, note off stops.
            rig.Core.SessionMidi.BeginLearn(SessionTarget.Slot, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(36, 100));             // learnt
            rig.Core.SendMIDIInput(NoteOn(36, 100));             // next beat: 12
            rig.PlayTo(12);
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Playing));
            rig.PlayTo(13);
            rig.Core.SendMIDIInput(NoteOff(36));                 // stops at 16
            rig.PlayTo(15);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(16);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);

            // Released before it started: it plays one quantization step.
            rig.Layout.LaunchClip(track, a);                     // 20
            rig.Layout.ReleaseClip(track, a);
            rig.PlayTo(20);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(23);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(24);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
        }

        [Test]
        public void ToggleClipStopsOnTheNextPressAndIgnoresReleases()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Mode = "Toggle"; c.Quantization = "Beat"; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);
            rig.Layout.ReleaseClip(track, a);
            rig.PlayTo(4);
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Playing));
            rig.PlayTo(5);
            rig.Layout.LaunchClip(track, a);                     // second press: stop at 8
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Stopping));
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
            rig.Layout.LaunchClip(track, a);                     // third press: launch at 12
            rig.PlayTo(12);
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Playing));
        }

        [Test]
        public void RepeatClipRestartsWhileHeldAndPlaysOnAfterRelease()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Mode = "Repeat"; c.Quantization = "Beat"; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);                     // starts at 4, restarts every beat while held
            rig.PlayTo(7);
            Assert.That(TickOf(a), Is.EqualTo(3));
            rig.PlayTo(8);
            Assert.That(TickOf(a), Is.EqualTo(0), "restarted");
            Assert.That(((SessionCore)rig.Session).IsRepeating(track), Is.True);
            rig.PlayTo(12);
            Assert.That(TickOf(a), Is.EqualTo(0), "restarted again");
            rig.PlayTo(13);
            rig.Layout.ReleaseClip(track, a);
            Assert.That(((SessionCore)rig.Session).IsRepeating(track), Is.False);
            rig.PlayTo(16);
            Assert.That(TickOf(a), Is.EqualTo(4), "plays on after the release");
            rig.PlayTo(20);
            Assert.That(TickOf(a), Is.EqualTo(8));

            // Scenes launch clips as Trigger: no repeating.
            rig.Layout.LaunchScene(0);                           // next bar: 32
            rig.PlayTo(32);
            Assert.That(TickOf(a), Is.EqualTo(0));
            Assert.That(((SessionCore)rig.Session).IsRepeating(track), Is.False);
            rig.PlayTo(36);
            Assert.That(TickOf(a), Is.EqualTo(4));
        }

        [Test]
        public void ClipQuantizationOverridesTheSessionsAndNoneStartsOnTheNextTick()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => c.Quantization = "None");
            rig.Set(b, c => c.Quantization = "HalfNote");        // two beats: 8 ticks

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);
            rig.PlayTo(2);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.Layout.LaunchClip(track, b);
            rig.PlayTo(7);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(b));
        }

        [Test]
        public void EnterAndTheLaunchButtonPressAndRelease()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Mode = "Gate"; c.Quantization = "Beat"; });
            var view = new SessionView(rig.Core);
            view.Rebuild();
            view.Select(0, 0);

            rig.PlayTo(1);
            Assert.That(view.HandleKey(Key.Enter, ModifierKeys.None), Is.True);
            rig.PlayTo(4);
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(ClipState.Playing));
            Assert.That(view.HandleKeyUp(Key.Enter), Is.True);
            rig.PlayTo(8);
            Assert.That(rig.Session.GetPlaying(track), Is.Null, "Enter released: the gate closed");
            Assert.That(view.HandleKeyUp(Key.Enter), Is.False, "nothing held");
        }

        // ------------------------------------------------------------------ legato, region, one shot

        [Test]
        public void LegatoTakesOverThePositionOfTheClipItReplaces()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => c.Quantization = "None");
            rig.Set(b, c => { c.Legato = true; c.Quantization = "Beat"; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);                     // starts at 2
            rig.PlayTo(5);
            Assert.That(TickOf(a), Is.EqualTo(3));
            rig.Layout.LaunchClip(track, b);                     // at 8, where A has got to: 6
            rig.PlayTo(8);
            Assert.That(TickOf(b), Is.EqualTo(6));
            Assert.That(TickOf(a), Is.Null);
            float tone = rig.Driver.ReadStereoSamples(1).Values[0] * 32768f;      // the output is scaled to ±1
            Assert.That(tone, Is.EqualTo(2060f).Within(0.5f), "the machine plays B from row 6 (Tone: 2000 + 10 per row)");

            rig.Layout.LaunchClip(track, a);                     // A has no legato: from the top, on the next tick
            rig.PlayTo(9);
            Assert.That(TickOf(a), Is.EqualTo(0));
        }

        [Test]
        public void StartMarkerAndLoopRegionSetWhereTheClipPlays()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Quantization = "None"; c.Start = 2; c.LoopStart = 4; c.LoopEnd = 12; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);
            var seen = new List<int?>();
            for (long clock = 2; clock < 2 + 26; clock++)
            {
                rig.PlayTo(clock);
                seen.Add(TickOf(a));
            }
            var expected = Enumerable.Range(2, 10).Concat(Enumerable.Range(4, 8)).Concat(Enumerable.Range(4, 8)).Select(i => (int?)i).ToList();
            Assert.That(seen, Is.EqualTo(expected));

            // Moving the loop end while it plays takes effect at once (the clip is at row 11 now: it wraps at 8).
            rig.Set(a, c => c.LoopEnd = 8);
            rig.PlayTo(28);
            Assert.That(TickOf(a), Is.InRange(4, 7));

            // Undo puts the loop end back, also for the playing clip: at 33 (31 ticks in) it is at
            // row 9 with the loop 4-11 (row 5 with 4-7).
            rig.Layout.Undo();
            Assert.That(rig.Layout.ClipOf(a).LoopEnd, Is.EqualTo(12));
            rig.PlayTo(33);
            Assert.That(TickOf(a), Is.EqualTo(9));
        }

        [Test]
        public void OneShotClipPlaysItsRegionOnceAndTheTrackStops()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Set(a, c => { c.Quantization = "None"; c.Loop = false; c.Start = 2; c.LoopEnd = 6; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(track, a);
            var seen = new List<int?>();
            for (long clock = 2; clock < 8; clock++)
            {
                rig.PlayTo(clock);
                seen.Add(TickOf(a));
            }
            Assert.That(seen, Is.EqualTo(new int?[] { 2, 3, 4, 5, null, null }));
            Assert.That(rig.Session.GetPlaying(track), Is.Null);
            Assert.That(rig.Core.Playing, Is.True, "the transport plays on");
        }

        // ------------------------------------------------------------------ velocity

        [Test]
        public void LaunchVelocityScalesTheClipsVolume()
        {
            using var rig = new ClipRig(1, hum: true);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            var machine = rig.Machines[0];
            rig.Set(a, c => { c.Quantization = "None"; c.Velocity = 100; });

            rig.PlayTo(2);
            double full = rig.Level();
            Assert.That(full, Is.GreaterThan(0));

            rig.Core.SessionMidi.BeginLearn(SessionTarget.Slot, 0, 0);
            rig.Core.SendMIDIInput(NoteOn(36, 100));             // learnt
            rig.Core.SendMIDIInput(NoteOn(36, 64));              // velocity 64 at 100%: 64/127
            rig.Ticks(2);                                        // starts, ramps over a block
            double soft = rig.Level();
            Assert.That(soft / full, Is.EqualTo(64 / 127.0).Within(0.01));
            Assert.That(machine.ClipGain, Is.EqualTo(64 / 127f).Within(1e-4));

            rig.Set(a, c => c.Velocity = 50);                    // 50%: halfway to full
            rig.Core.SendMIDIInput(NoteOn(36, 1));
            rig.Ticks(2);
            Assert.That(rig.Level() / full, Is.EqualTo(1 - 0.5 * (1 - 1 / 127.0)).Within(0.01));

            rig.Layout.LaunchClip(track, a);                     // mouse and keys: full velocity
            rig.Ticks(2);
            Assert.That(rig.Level() / full, Is.EqualTo(1.0).Within(0.01));

            rig.Core.SendMIDIInput(NoteOn(36, 32));
            rig.Ticks(2);
            rig.Session.Stop(track, immediate: true);
            rig.Ticks(2);
            Assert.That(rig.Level() / full, Is.EqualTo(1.0).Within(0.01), "back to full once the clip stops");
            Assert.That(machine.ClipGain, Is.EqualTo(1f));

            rig.Core.SendMIDIInput(NoteOn(36, 32));
            rig.Ticks(2);
            rig.Core.Playing = false;
            Assert.That(machine.ClipGain, Is.EqualTo(1f), "and when the transport stops");
        }

        [Test]
        public void TwoTracksOfOneMachinePlayAtTheNewestClipsVelocity()
        {
            // One machine is one signal: its volume follows the clip that started last, and goes
            // back to the other track's when that one stops (0.5.1; it used to reset to full).
            using var rig = new ClipRig(1, hum: true);
            var machine = rig.Machines[0];
            rig.Core.SongCore.AddSequence(machine, 1);
            var p = rig.Pattern(machine, "P", Bar);
            var q = rig.Pattern(machine, "Q", Bar);
            rig.Layout.Ensure();
            var first = rig.Layout.SequenceOf(0)!;
            var second = rig.Layout.SequenceOf(1)!;
            rig.Set(p, c => { c.Quantization = "None"; c.Velocity = 100; });
            rig.Set(q, c => { c.Quantization = "None"; c.Velocity = 100; });

            rig.PlayTo(1);
            rig.Layout.LaunchClip(first, p, velocity: 64);
            rig.PlayTo(3);
            Assert.That(machine.ClipGain, Is.EqualTo(64 / 127f).Within(1e-4));
            rig.Layout.LaunchClip(second, q, velocity: 32);
            rig.PlayTo(5);
            Assert.That(machine.ClipGain, Is.EqualTo(32 / 127f).Within(1e-4), "the newest clip");
            rig.Session.Stop(second, immediate: true);
            rig.PlayTo(7);
            Assert.That(machine.ClipGain, Is.EqualTo(64 / 127f).Within(1e-4), "back to the clip still playing");
            rig.Session.Stop(first, immediate: true);
            rig.PlayTo(9);
            Assert.That(machine.ClipGain, Is.EqualTo(1f));
        }

        [Test]
        public void AnArrangementEventPlaysAtItsVolume()
        {
            using var rig = new ClipRig(1, hum: true);
            var machine = rig.Machines[0];
            var p = rig.Pattern(machine, "P", 4);
            var track = rig.Core.SongCore.SequencesList[0];
            track.SetEvent(4, new SequenceEvent(SequenceEventType.PlayPattern, p) { Gain = 0.5 });
            track.SetEvent(8, new SequenceEvent(SequenceEventType.PlayPattern, p));

            rig.PlayTo(2);
            double full = rig.Level();                           // tick 2: nothing plays, full volume
            rig.PlayTo(5);
            Assert.That(machine.ClipGain, Is.EqualTo(0.5f));
            Assert.That(rig.Level() / full, Is.EqualTo(0.5).Within(0.01), "tick 5: the event's volume");
            rig.PlayTo(9);
            Assert.That(machine.ClipGain, Is.EqualTo(1f), "the next event is plain");
        }

        // ------------------------------------------------------------------ scenes

        [Test]
        public void SceneTempoAndBeatsPerBarChangeOnTheLaunchTick()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            var track = rig.Track(0);
            var core = (SessionCore)rig.Session;
            layout.SetSceneTempo(0, 140);
            layout.SetSceneBeatsPerBar(0, 3);
            layout.SetSceneTempo(1, 90);                         // a scene with a tempo and no clips
            int before = rig.Core.BPM;
            Assert.That(before, Is.Not.EqualTo(140));

            rig.PlayTo(1);
            layout.LaunchScene(0);                               // next bar: 16
            rig.PlayTo(15);
            Assert.That(rig.Core.BPM, Is.EqualTo(before));
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(4));
            rig.PlayTo(16);
            Assert.That(rig.Core.BPM, Is.EqualTo(140));
            Assert.That(rig.Session.BeatsPerBar, Is.EqualTo(3));
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a));
            Assert.That(core.GridOrigin, Is.EqualTo(16), "bars count from the change");
            Assert.That(core.GridOriginBar, Is.EqualTo(1));
            rig.Ticks(2);
            Assert.That(ReBuzzCore.masterInfo.BeatsPerMin, Is.EqualTo(140), "the engine plays at the new tempo");

            layout.LaunchScene(1);                               // next 3-beat bar after 16: 28
            rig.PlayTo(27);
            Assert.That(rig.Core.BPM, Is.EqualTo(140));
            rig.PlayTo(28);
            Assert.That(rig.Core.BPM, Is.EqualTo(90));
            Assert.That(rig.Session.GetPlaying(track), Is.SameAs(a), "a scene without clips leaves the tracks playing");

            // A scene launch replaces one still waiting to start, settings included.
            layout.LaunchScene(0);                               // 40
            layout.LaunchScene(2);                               // replaces it: no tempo
            rig.PlayTo(40);
            Assert.That(rig.Core.BPM, Is.EqualTo(90));
        }

        // ------------------------------------------------------------------ recording

        [Test]
        public void ALateLaunchIsRecordedWhereItIsHeardToStart()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var b = rig.Pattern(rig.Machines[0], "B", Bar);
            rig.Layout.Ensure();
            var track = rig.Track(0);
            rig.Core.Looping = false;

            rig.Align();
            rig.Core.Recording = true;                           // starts the transport
            rig.Session.Launch(track, a);
            rig.PlayTo(17);
            rig.Driver.ReadStereoSamples(rig.SamplesPerTick / 2); // rendered: about 17.5 ticks
            rig.Core.OutputLatencySamples = 3 * rig.SamplesPerTick;   // heard: about 14.5
            rig.Session.Launch(track, b);                        // the bar line at 16 is rendered: B starts at 18, in step with 16
            rig.PlayTo(18);
            Assert.That(rig.Session.GetClipStartClock(track), Is.EqualTo(Bar));
            rig.Core.OutputLatencySamples = 0;
            rig.PlayTo(20);
            rig.Core.Recording = false;
            rig.Ticks(1);
            rig.Core.Playing = false;

            var events = track.Events.ToDictionary(e => e.Key, e => e.Value.Pattern?.Name ?? e.Value.Type.ToString());
            Assert.That(events[0], Is.EqualTo("A"));
            Assert.That(events[Bar], Is.EqualTo("B"), "written on the bar line, not at 18 or at its next loop");
            Assert.That(events.ContainsKey(18), Is.False);
        }

        // ------------------------------------------------------------------ settings follow their pattern

        [Test]
        public void ClipSettingsFollowTheirPatternThroughRenameDuplicateAndDelete()
        {
            using var rig = new ClipRig(1);
            var a = rig.Pattern(rig.Machines[0], "A", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            rig.Set(a, c => { c.Mode = "Gate"; c.Velocity = 30; });

            a.Name = "Verse";
            Assert.That(layout.ClipOf(a).Mode, Is.EqualTo("Gate"));
            var copy = layout.DuplicateClip(0, 0, out _);
            Assert.That(copy, Is.Not.Null);
            Assert.That(layout.ClipOf(copy).Velocity, Is.EqualTo(30), "a duplicate launches the same way");

            layout.DeleteClip(a);
            Assert.That(layout.Document!.Clips.Keys.Select(k => k.Pattern), Is.EqualTo(new[] { copy.Name }));
            layout.Undo();
            Assert.That(layout.ClipOf(rig.Machines[0].PatternsList.Single(p => p.Name == "Verse")).Mode, Is.EqualTo("Gate"), "undo brings them back");
        }

        // ------------------------------------------------------------------ clip panel

        [Test]
        public void ClipPanelEditsTheSelectedClip()
        {
            using var rig = new ClipRig(3);
            var a = rig.Pattern(rig.Machines[0], "Beat", Bar);
            rig.Pattern(rig.Machines[0], "Fill", Bar);
            rig.Pattern(rig.Machines[1], "Bass", 2 * Bar);
            var hook = rig.Pattern(rig.Machines[2], "Hook", Bar);
            var layout = rig.Layout;
            layout.Ensure();
            layout.RenameScene(0, "Intro");
            layout.RenameScene(1, "Drop");
            layout.SetSceneTempo(1, 140);
            layout.SetSceneBeatsPerBar(1, 3);
            rig.Set(hook, c => { c.Loop = false; c.Velocity = 40; });

            var view = new SessionView(rig.Core);
            view.Rebuild();
            view.Select(0, 0);
            var panel = view.ClipPanelView;
            Assert.That(panel.Pattern, Is.SameAs(a));

            panel.ModeBox.SelectedIndex = 1;                                    // Gate
            Assert.That(layout.ClipOf(a).Mode, Is.EqualTo("Gate"));
            view.Rebuild();
            panel.QuantizeBox.SelectedIndex = Array.FindIndex(SessionView.ClipPanel.Quantizations, q => q.Value == "Beat");
            Assert.That(layout.ClipOf(a).Quantization, Is.EqualTo("Beat"));
            view.Rebuild();
            panel.StartBox.Text = "4";
            panel.LoopEndBox.Text = "12";
            panel.CommitTypedMarkers();
            Assert.That((layout.ClipOf(a).Start, layout.ClipOf(a).LoopEnd), Is.EqualTo((4, 12)));
            view.Rebuild();
            Assert.That(panel.RegionText, Does.Contain("loops 0–12"));
            panel.StartBox.Text = "abc";
            panel.CommitTypedMarkers();
            Assert.That(panel.StartBox.Text, Is.EqualTo("4"), "not a number: back to what it was");
            panel.VelocityBar.SetByUser(0.5);
            Assert.That(layout.ClipOf(a).Velocity, Is.EqualTo(50));
            view.Rebuild();
            panel.LegatoBox.IsChecked = true;
            panel.LegatoBox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            Assert.That(layout.ClipOf(a).Legato, Is.True);

            layout.Undo();
            view.Rebuild();
            Assert.That(layout.ClipOf(a).Legato, Is.False, "panel edits undo like grid edits");
            Assert.That(panel.LegatoBox.IsChecked, Is.False);

            layout.LaunchScene(0);
            rig.Ticks(3);
            view.RefreshState();
            Assert.That(panel.StatusText, Does.Contain("playing"));
            SessionViewTests.Screenshot(view, "session-view-clip", 1100, 640);

            view.Select(1, 3);                                                  // an empty slot
            Assert.That(panel.Pattern, Is.Null);
        }
    }
}
