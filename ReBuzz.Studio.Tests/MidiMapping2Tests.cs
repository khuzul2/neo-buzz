// Fork (docs/specs/midi-mapping.md, MM2 phase 1): the engine's half of MIDI mapping 2: mixer targets, fader banks and map version 2, on the real
// engine with scripted devices (Core.SendMIDIInput / MidiMapping.OnMidiThread), as MidiMappingTests does for MM1.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class MidiMapping2Tests
    {
        static readonly MidiDevice Faders = new MidiDevice(0, "Faders");

        static int CC(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);

        static void Pump() => Dispatcher.UIThread.RunJobs();

        static IDisposable Setup(out SessionRig rig, out MidiMapping mm)
        {
            var setup = TrackInputEngineTests.Setup(out rig);
            mm = rig.Core.MidiMapping;
            mm.CancelListening();
            mm.Edit((c, s) => { c.Global.Clear(); c.Groups.Clear(); c.NoKeys.Clear(); s.Tracks.Clear(); s.Global = null; });
            mm.SetBank(0, 0);
            return setup;
        }

        static int AmpOf(MachineCore m) => m.Outputs.First().Amp;

        static MachineCore[] Tracks(SessionRig rig, int count)
        {
            rig.Layout.Ensure();
            return Enumerable.Range(1, count).Select(i => rig.Add("Neo Poly", "T" + i.ToString("00"))).ToArray();
        }

        // ------------------------------------------------------------------ fader banks

        [AvaloniaTest]
        public void MovingTheBankByEightMapsTheFadersOntoTracksNineToSixteenAndPickupHoldsUntilTheKnobCrossesTheValue()
        {
            using var setup = Setup(out var rig, out var mm);
            var t = Tracks(rig, 16);
            for (int n = 1; n <= 8; n++)
                Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 20 + n), KnobEncoding.Absolute, MapTarget.Mixer(MixerControl.Volume, "", n), MapScope.Global).Ok, Is.True);
            mm.Rebuild();
            Assert.That(mm.BankTrack(1).Name, Is.EqualTo("T01"));
            foreach (var m in t) m.Outputs.First().Amp = SessionMixer.AmpOf(0.5);
            int before1 = AmpOf(t[0]), before9 = AmpOf(t[8]);

            // Bank 1: fader 1 is track 1. It starts well below the value: nothing moves until it crosses.
            mm.OnMidiThread(Faders, CC(0, 21, 5));
            Assert.That(AmpOf(t[0]), Is.EqualTo(before1), "pickup holds");
            mm.OnMidiThread(Faders, CC(0, 21, 100));
            Assert.That(AmpOf(t[0]), Is.Not.EqualTo(before1), "crossed: track 1 follows");
            Assert.That(AmpOf(t[8]), Is.EqualTo(before9), "and track 9 is not touched");

            Assert.That(mm.MoveBank(8), Is.True);
            Assert.That(mm.BankOffset, Is.EqualTo(8));
            Assert.That(mm.BankTrack(1).Name, Is.EqualTo("T09"));
            int held1 = AmpOf(t[0]);
            mm.OnMidiThread(Faders, CC(0, 21, 100));
            Assert.That(AmpOf(t[8]), Is.EqualTo(before9), "after the move the knob has to meet track 9's value again (it is at 0.79, track 9 is at 0.5)");
            mm.OnMidiThread(Faders, CC(0, 21, 90));
            Assert.That(AmpOf(t[8]), Is.EqualTo(before9), "still above or below: held");
            mm.OnMidiThread(Faders, CC(0, 21, 60));
            Assert.That(AmpOf(t[8]), Is.Not.EqualTo(before9), "it crossed the value: track 9 follows");
            Assert.That(AmpOf(t[0]), Is.EqualTo(held1), "track 1 is out of the bank");

            // Faders 2 to 8 are tracks 10 to 16; the window cannot leave the song.
            mm.OnMidiThread(Faders, CC(0, 28, 5));
            mm.OnMidiThread(Faders, CC(0, 28, 120));
            Assert.That(AmpOf(t[15]), Is.Not.EqualTo(SessionMixer.AmpOf(0.5)));
            Assert.That(mm.MoveBank(8), Is.False, "16 tracks: the second bank is the last");
            Assert.That(mm.MoveBank(-1), Is.True);
            Assert.That(mm.BankOffset, Is.EqualTo(7));
            Assert.That(mm.IsInBank("track:T08") && mm.IsInBank("track:T15") && !mm.IsInBank("track:T16") && !mm.IsInBank("track:T07"), Is.True);
        }

        [AvaloniaTest]
        public void TheBankCommandsAreMappableAndMoveTheBankAndTheSceneWindow()
        {
            using var setup = Setup(out var rig, out var mm);
            Tracks(rig, 20);
            var pads = new MidiDevice(1, "Pads");
            var cmds = new[] { MapCommand.BankRight, MapCommand.BankRight8, MapCommand.BankLeft, MapCommand.BankLeft8 };
            for (int i = 0; i < cmds.Length; i++)
                Assert.That(mm.MapControl(new MapSource("Pads", MapKind.Note, 0, 40 + i), KnobEncoding.Absolute, MapTarget.Command(cmds[i]), MapScope.Global).Ok, Is.True);
            void Press(int note) { mm.OnMidiThread(pads, 0x90 | (note << 8) | (100 << 16)); Pump(); mm.OnMidiThread(pads, 0x80 | (note << 8)); }
            Press(40);
            Assert.That(mm.BankOffset, Is.EqualTo(1));
            Press(41);
            Assert.That(mm.BankOffset, Is.EqualTo(9));
            Press(42);
            Assert.That(mm.BankOffset, Is.EqualTo(8));
            Press(43);
            Assert.That(mm.BankOffset, Is.EqualTo(0));
            Press(43);
            Assert.That(mm.BankOffset, Is.EqualTo(0), "it stops at the first track");
            Assert.That(mm.MapControl(new MapSource("Pads", MapKind.Note, 0, 50), KnobEncoding.Absolute, MapTarget.Command(MapCommand.SceneDown), MapScope.Global).Ok, Is.True);
            Press(50);
            Assert.That(mm.SceneOffset, Is.EqualTo(1), "a song has at least 8 scenes");
            for (int i = 0; i < 6; i++) Press(50);
            Assert.That(mm.SceneOffset, Is.EqualTo(3), "the window of 5 stays on the 8 scenes");
        }

        // ------------------------------------------------------------------ mixer targets

        static MapTarget Fader(string strip) => MapTarget.Mixer(MixerControl.Volume, strip);

        [AvaloniaTest]
        public void MixerFaderPanSendOnSoloAndTheMastersFaderAreDrivenByControllers()
        {
            using var setup = Setup(out var rig, out var mm);
            var t = Tracks(rig, 2);
            var verb = rig.Add("Neo Verb", "Verb", track: false);
            rig.Layout.AddReturn(verb);
            var dev = new MidiDevice(3, "Mix");
            void Map(int cc, MapTarget target) => Assert.That(mm.MapControl(new MapSource("Mix", MapKind.ControlChange, 0, cc), KnobEncoding.Absolute, target, MapScope.Global, new MapDrive { Takeover = Takeover.Jump }).Ok, Is.True, target.Text);
            Map(1, Fader("track:T01"));
            Map(2, MapTarget.Mixer(MixerControl.Pan, "track:T01"));
            Map(3, MapTarget.Mixer(MixerControl.Send, "track:T01", 0, "Verb"));
            Map(4, MapTarget.Mixer(MixerControl.Mute, "track:T01"));
            Map(5, MapTarget.Mixer(MixerControl.Solo, "track:T01"));
            Map(6, MapTarget.Mixer(MixerControl.Master));
            mm.Rebuild();

            mm.OnMidiThread(dev, CC(0, 1, 0));
            Assert.That(AmpOf(t[0]), Is.EqualTo(0), "fader down: -inf");
            mm.OnMidiThread(dev, CC(0, 1, 127));
            Assert.That(AmpOf(t[0]), Is.EqualTo(SessionMixer.MaxAmp), "fader up: +12 dB");
            Assert.That(AmpOf(t[1]), Is.EqualTo(SessionMixer.UnityAmp), "the other track keeps its own");

            mm.OnMidiThread(dev, CC(0, 2, 0));
            Assert.That(t[0].Outputs.First().Pan, Is.EqualTo(0), "pan hard left");
            mm.OnMidiThread(dev, CC(0, 2, 127));
            Assert.That(t[0].Outputs.First().Pan, Is.EqualTo(SessionMixer.MaxPan), "hard right");

            Assert.That(SessionMixer.GetSend(t[0], verb), Is.EqualTo(0), "no send wire yet");
            mm.OnMidiThread(dev, CC(0, 3, 100));
            Pump();
            Assert.That(SessionMixer.GetSend(t[0], verb), Is.GreaterThan(0), "a send raised from nothing makes its wire");
            mm.OnMidiThread(dev, CC(0, 3, 127));
            Assert.That(SessionMixer.GetSend(t[0], verb), Is.EqualTo(SessionMixer.MaxAmp));

            mm.OnMidiThread(dev, CC(0, 4, 127));
            Pump();
            Assert.That(t[0].IsMuted, Is.True);
            mm.OnMidiThread(dev, CC(0, 4, 0));
            Pump();
            Assert.That(t[0].IsMuted, Is.False);
            mm.OnMidiThread(dev, CC(0, 5, 127));
            Pump();
            Assert.That(t[0].IsSoloed, Is.True);
            mm.OnMidiThread(dev, CC(0, 5, 0));
            Pump();
            Assert.That(t[0].IsSoloed, Is.False);

            mm.OnMidiThread(dev, CC(0, 6, 0));
            Pump();
            var mp = rig.Master.ParameterGroupsList[1].ParametersList[0];                      // the rig has no listener for the Master's Volume: the parameter is what the engine reads
            Assert.That(mp.GetValue(0), Is.EqualTo(mp.MaxValue), "the Master's fader down: its Volume parameter is at the quiet end");
            mm.OnMidiThread(dev, CC(0, 6, 127));
            Pump();
            Assert.That(mp.GetValue(0), Is.EqualTo(0), "and up: 0 dB");
        }

        [AvaloniaTest]
        public void TheMixerPathsOfTheRouterAllocateNothingPerMessage()
        {
            using var setup = Setup(out var rig, out var mm);
            var t = Tracks(rig, 10);
            var verb = rig.Add("Neo Verb", "Verb", track: false);
            rig.Layout.AddReturn(verb);
            var dev = new MidiDevice(3, "Mix");
            void Map(int cc, MapTarget target) => Assert.That(mm.MapControl(new MapSource("Mix", MapKind.ControlChange, 0, cc), KnobEncoding.Absolute, target, MapScope.Global, new MapDrive { Takeover = Takeover.Jump }).Ok, Is.True, target.Text);
            Map(1, Fader("track:T01"));
            Map(2, MapTarget.Mixer(MixerControl.Pan, "track:T01"));
            Map(3, MapTarget.Mixer(MixerControl.Send, "track:T01", 0, "Verb"));
            Map(4, MapTarget.Mixer(MixerControl.Volume, "", 2));
            Map(5, MapTarget.Mixer(MixerControl.Master));
            Map(6, MapTarget.Parameter(rig.Master.Name, MapTarget.InputPrefix + "Amp", 0));
            mm.Rebuild();
            mm.OnMidiThread(dev, CC(0, 3, 100));
            Pump();
            for (int i = 0; i < 400; i++)
                for (int cc = 1; cc <= 6; cc++) mm.OnMidiThread(dev, CC(0, cc, (i * 3 + cc) & 127));
            var report = new List<string>();
            for (int cc = 1; cc <= 6; cc++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2000; i++) mm.OnMidiThread(dev, CC(0, cc, (i * 5 + cc) & 127));
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes != 0) report.Add($"cc {cc}: {bytes} bytes");
            }
            Assert.That(report, Is.Empty, "bytes allocated by 2000 messages of each mixer kind");
        }

        [AvaloniaTest]
        public void AnInputGroupParameterIsMappableAndDrivesTheWire()
        {
            using var setup = Setup(out var rig, out var mm);
            var t = Tracks(rig, 1);
            var master = rig.Master;
            var target = MapTarget.Parameter(master.Name, MapTarget.InputPrefix + "Amp", 0);
            Assert.That(mm.Validate(target), Is.Null);
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 9), KnobEncoding.Absolute, target, MapScope.Global, new MapDrive { Takeover = Takeover.Jump }).Ok, Is.True);
            Assert.That(target.Text, Does.Contain("Input 1"));
            mm.OnMidiThread(Faders, CC(0, 9, 0));
            Assert.That(AmpOf(t[0]), Is.EqualTo(0), "the input's amp is the wire's");
            mm.OnMidiThread(Faders, CC(0, 9, 127));
            Assert.That(AmpOf(t[0]), Is.EqualTo(SessionMixer.MaxAmp).Within(8));
            Assert.That(mm.Validate(MapTarget.Parameter(master.Name, MapTarget.InputPrefix + "Amp", 9)), Does.Contain("input"));
        }

        [AvaloniaTest]
        public void MixerInputAndSendMapsSaveWithTheSongReopenTheSameAndUndo()
        {
            using var setup = Setup(out var rig, out var mm);
            var t = Tracks(rig, 2);
            var verb = rig.Add("Neo Verb", "Verb", track: false);
            rig.Layout.AddReturn(verb);
            var stack = rig.Core.SongCore.ActionStack;
            var fader = Fader("track:T01");
            var send = MapTarget.Mixer(MixerControl.Send, "track:T02", 0, "Verb");
            var input = MapTarget.Parameter(rig.Master.Name, MapTarget.InputPrefix + "Amp", 1);
            int steps = stack.Actions.Count();
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, fader, MapScope.Track, null, "track:T01").Ok, Is.True);
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 2), KnobEncoding.Absolute, send, MapScope.Track, null, "track:T01").Ok, Is.True);
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 3), KnobEncoding.Absolute, input, MapScope.Track, null, "track:T01").Ok, Is.True);
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 4), KnobEncoding.Absolute, MapTarget.Mixer(MixerControl.Pan, "", 3), MapScope.Track, null, "track:T01").Ok, Is.True);
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 4), "one undo step each");
            Assert.That(mm.PagesOf("track:T01").CurrentPage.Direct.Select(e => e.Target.Text), Is.EquivalentTo(new[] { fader.Text, send.Text, input.Text, "Mixer · Bank track 3 · Pan" }));
            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-mm2-{Environment.ProcessId}.nbl");
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                rig.Core.SaveSongCopy(path);
                rig.Core.Modified = false;
                rig.Core.ExecuteCommand(BuzzCommand.NewFile);
                Pump();
                Assert.That(mm.PagesOf("track:T01"), Is.Null.Or.Matches<TrackPages>(p => p.IsEmpty));
                rig.Core.OpenSongFile(path);
                Pump();
                var direct = mm.PagesOf("track:T01").CurrentPage.Direct;
                Assert.That(direct.Select(e => e.Target), Is.EquivalentTo(new[] { fader, send, input, MapTarget.Mixer(MixerControl.Pan, "", 3) }), "the same targets after reopening");
                Assert.That(mm.Validate(send), Is.Null);
                Assert.That(mm.Validate(input), Is.Null);
            }
            finally
            {
                Dialogs.Responder = null;
                rig.Core.Modified = false;
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void MixerMapsUndoOneStepAtATimeAndFollowARename()
        {
            using var setup = Setup(out var rig, out var mm);
            Tracks(rig, 2);
            var stack = rig.Core.SongCore.ActionStack;
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Fader("track:T01"), MapScope.Global).Ok, Is.True);
            Assert.That(mm.GlobalEntries.Count, Is.EqualTo(1));
            mm.RenameMachine("T01", "Kick");
            Assert.That(mm.GlobalEntries.Single().Target.Strip, Is.EqualTo("track:Kick"), "a strip's id follows its machine's name");
            stack.Undo();
            Assert.That(mm.GlobalEntries, Is.Empty);
            stack.Redo();
            Assert.That(mm.GlobalEntries.Count, Is.EqualTo(1));
            Assert.That(mm.Validate(MapTarget.Mixer(MixerControl.Volume, "track:Nowhere")), Does.Contain("no strip"));
            Assert.That(mm.Validate(MapTarget.Mixer(MixerControl.Send, "track:T02", 0, "Nowhere")), Does.Contain("return"));
        }

        // ------------------------------------------------------------------ map version 2

        [AvaloniaTest]
        public void AMapOnAParameterWhoseEnumGrowsKeepsItsRawTargetsAndAVersionOneMapLoadsAsBefore()
        {
            using var setup = Setup(out var rig, out var mm);
            rig.Layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            var p = lead.ParameterGroupsList[1].ParametersList.Cast<ParameterCore>().First(x => x.MaxValue - x.MinValue >= 2 && x.MaxValue - x.MinValue <= 12 && x.MaxValue < 0xFE);
            int oldMax = p.MaxValue;
            var target = MapTarget.Parameter(lead.Name, p.Name);
            Assert.That(mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 30), KnobEncoding.Absolute, target, MapScope.Global, new MapDrive { Takeover = Takeover.Jump }).Ok, Is.True);
            var saved = mm.GlobalEntries.Single();
            Assert.That((saved.RawMin, saved.RawMax), Is.EqualTo((p.MinValue, oldMax)), "the map keeps the range it was made with");
            Assert.That(mm.Song.ToXml().ToString() + mm.Computer.ToXml(), Does.Contain("rawmax=\"" + oldMax + "\""));

            // The parameter's enum gains three values at its end.
            p.MaxValue = oldMax + 3;
            mm.Rebuild();
            mm.OnMidiThread(Faders, CC(0, 30, 127));
            Assert.That(p.GetValue(0), Is.EqualTo(oldMax), "the top of the knob still lands on the value it landed on when the map was made");
            mm.OnMidiThread(Faders, CC(0, 30, 0));
            Assert.That(p.GetValue(0), Is.EqualTo(p.MinValue));

            // A version 1 map (no raw range) reads the range the parameter has now.
            mm.Edit((c, s) => { foreach (var e in c.Global) { e.RawMin = null; e.RawMax = null; } });
            Assert.That(mm.GlobalEntries.Single().RawMax, Is.Null);
            mm.OnMidiThread(Faders, CC(0, 30, 127));
            Assert.That(p.GetValue(0), Is.EqualTo(oldMax + 3), "as it did in version 1: the whole current range");
            p.MaxValue = oldMax;
        }
    }
}
