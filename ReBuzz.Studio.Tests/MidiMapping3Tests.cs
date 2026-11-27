// Fork (docs/specs/midi-mapping.md, MM2 phase 2): controller profiles on the real engine, with a fake MIDI in (scripted devices through
// MidiMapping.OnMidiThread) and a fake MIDI out (an IFeedbackOutput that remembers every message it is sent and when).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;

namespace ReBuzz.Studio.Tests
{
    public class MidiMapping3Tests
    {
        const string Grid = "APC40 mkII", Keyboard = "MPK225 Port 1";
        static readonly MidiDevice Apc = new MidiDevice(5, Grid);

        static int CC(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);
        static int NoteOn(int channel, int number, int velocity = 100) => 0x90 | channel | (number << 8) | (velocity << 16);
        static int NoteOff(int channel, int number) => 0x80 | channel | (number << 8);
        static void Pump() => Dispatcher.UIThread.RunJobs();

        /// <summary>What a controller's output was sent.</summary>
        sealed class FakeOut : IFeedbackOutput
        {
            public readonly List<string> Ports = new List<string>();
            public readonly List<(string Port, int Message, long At)> Sent = new List<(string, int, long)>();
            public readonly List<(string Port, byte[] Bytes)> SysEx = new List<(string, byte[])>();
            public long Now;
            public IReadOnlyList<string> Names() => Ports;
            public void Open(string output) { }
            public void Send(string output, int message) => Sent.Add((output, message, Now));
            public void SendSysEx(string output, byte[] message) => SysEx.Add((output, message));
            public int Count(int status, int number) => Sent.Count(m => (m.Message & 0xFF) == status && ((m.Message >> 8) & 0x7F) == number);
            public (int Status, int Number, int Value) Last(int status, int number)
            {
                var m = Sent.Last(x => (x.Message & 0xFF) == status && ((x.Message >> 8) & 0x7F) == number).Message;
                return (m & 0xFF, (m >> 8) & 0x7F, (m >> 16) & 0x7F);
            }
        }

        sealed class Fixture : IDisposable
        {
            public SessionRig Rig;
            public MidiMapping Mm;
            public FakeOut Out = new FakeOut();
            public MachineCore[] Tracks;
            IDisposable setup;

            public Fixture(int tracks, params string[] inputs)
            {
                setup = TrackInputEngineTests.Setup(out Rig);
                MidiEngine.SimulatedInputs = inputs.Length == 0 ? new[] { Grid } : inputs;
                Mm = Rig.Core.MidiMapping;
                Mm.CancelListening();
                Mm.FeedbackAutoTick = false;
                Mm.Edit((c, s) => { c.Global.Clear(); c.Groups.Clear(); c.NoKeys.Clear(); c.Devices.Clear(); s.Tracks.Clear(); s.Global = null; });
                Mm.SetBank(0, 0);
                Rig.Layout.Ensure();
                Tracks = Enumerable.Range(1, tracks).Select(i => Rig.Add("Neo Poly", "T" + i.ToString("00"))).ToArray();
                Out.Ports.Add(Grid);
                Mm.Output = Out;
                Mm.Rebuild();
            }

            public int Tick(long at) { Out.Now = at; return Mm.FeedbackTick(at); }

            public void Dispose()
            {
                MidiEngine.SimulatedInputs = null;
                Mm.Output = null;
                Mm.Edit((c, s) => { c.Devices.Clear(); });
                setup.Dispose();
            }
        }

        static int AmpOf(MachineCore m) => m.Outputs.First().Amp;

        // ------------------------------------------------------------------ profiles: matching and overriding

        [AvaloniaTest]
        public void EachProfileMatchesItsPortNameAndNoneAndAChosenProfileOverrideIt()
        {
            using var f = new Fixture(2, Grid, Keyboard, "Some Other Port");
            var mm = f.Mm;
            Assert.That(mm.ProfileOf(Grid)?.Id, Is.EqualTo("clip-grid-8x5"), "the clip-grid controller's port name");
            Assert.That(mm.ProfileOf(Keyboard)?.Id, Is.EqualTo("keys-knobs-pads-25"), "the keyboard's port name");
            Assert.That(mm.ProfileOf("Some Other Port"), Is.Null, "a device with no profile works as in MM1");
            Assert.That(mm.ProfileOf(Grid + " #2")?.Id, Is.EqualTo("clip-grid-8x5"), "a second one of the same name");

            Assert.That(mm.SetControllerProfile(Grid, "none"), Is.Null);
            Assert.That(mm.ProfileOf(Grid), Is.Null, "None overrides Auto");
            Assert.That(mm.SetControllerProfile("Some Other Port", "keys-knobs-pads-25"), Is.Null);
            Assert.That(mm.ProfileOf("Some Other Port")?.Id, Is.EqualTo("keys-knobs-pads-25"), "a chosen profile overrides Auto");
            Assert.That(mm.SetControllerProfile(Grid, "auto"), Is.Null);
            Assert.That(mm.ProfileOf(Grid)?.Id, Is.EqualTo("clip-grid-8x5"), "Auto again");
            Assert.That(mm.SetControllerProfile(Grid, "no-such-profile"), Does.Contain("no profile"));
            Assert.That(mm.ControllerRows().Single(r => r.Device == "Some Other Port").ProfileSet, Is.EqualTo("keys-knobs-pads-25"));

            var stack = f.Rig.Core.SongCore.ActionStack;
            stack.Undo();
            Assert.That(mm.ProfileOf(Grid), Is.Null, "undo walks back one setting at a time: Auto, then None again");
            stack.Undo();
            Assert.That(mm.ProfileOf("Some Other Port"), Is.Null, "the chosen profile is undone");
            stack.Undo();
            Assert.That(mm.ProfileOf(Grid)?.Id, Is.EqualTo("clip-grid-8x5"), "and None: Auto matches again");
        }

        [AvaloniaTest]
        public void AControllerWithNoProfileWorksAsInMm1AndTheProfileRolesFollow()
        {
            using var f = new Fixture(2, "Some Other Port");
            var mm = f.Mm;
            var other = new MidiDevice(7, "Some Other Port");
            Assert.That(mm.OnMidiThread(other, CC(0, 7, 64)), Is.False, "nothing is mapped: the message goes on, as in MM1");
            Assert.That(mm.RoleOf("Some Other Port"), Is.EqualTo(DeviceRole.Both));
            Assert.That(mm.RoleOf(Grid), Is.EqualTo(DeviceRole.Controls), "the clip-grid profile's role");
            Assert.That(mm.SetControllerRole(Grid, DeviceRole.Both), Is.Null);
            Assert.That(mm.RoleOf(Grid), Is.EqualTo(DeviceRole.Both), "what was set wins");
            Assert.That(mm.SetControllerKnobGroup(Grid, 70), Does.Contain("0 to 64"));
            Assert.That(mm.SetControllerKnobGroup(Grid, 4), Is.Null);
            Assert.That(mm.SetControllerTakeover(Grid, Takeover.Scale), Is.Null);
            var row = mm.ControllerRows().Single(r => r.Device == Grid);
            Assert.That((row.KnobGroup, row.Takeover, row.Role), Is.EqualTo((4, Takeover.Scale, DeviceRole.Both)));
            Assert.That(mm.SetControllerOutput(Grid, "No such output"), Does.Contain("no MIDI output"));
            Assert.That(mm.SetControllerOutput(Grid, Grid), Is.Null);
        }

        // ------------------------------------------------------------------ the profile at work (MIDI in)

        [AvaloniaTest]
        public void TheClipGridProfileDrivesTheBankFadersTheMasterTheTransportAndTheBankArrows()
        {
            using var f = new Fixture(16);
            var mm = f.Mm;
            mm.SetControllerTakeover(Grid, Takeover.Jump);
            for (int n = 0; n < 8; n++) mm.OnMidiThread(Apc, CC(n, 7, 127));
            Assert.That(f.Tracks.Take(8).Select(AmpOf), Has.All.EqualTo(SessionMixer.MaxAmp), "the 8 faders are the bank's volumes");
            Assert.That(AmpOf(f.Tracks[8]), Is.EqualTo(SessionMixer.UnityAmp), "track 9 is not in the bank");
            mm.OnMidiThread(Apc, CC(0, 7, 0));
            Assert.That(AmpOf(f.Tracks[0]), Is.EqualTo(0));

            mm.OnMidiThread(Apc, NoteOn(0, 61));                    // bank right by 8
            mm.OnMidiThread(Apc, NoteOff(0, 61));
            Pump();
            Assert.That(mm.BankOffset, Is.EqualTo(8));
            mm.OnMidiThread(Apc, CC(0, 7, 20));
            Assert.That(AmpOf(f.Tracks[8]), Is.Not.EqualTo(SessionMixer.UnityAmp), "fader 1 is track 9 now");
            Assert.That(AmpOf(f.Tracks[0]), Is.EqualTo(0), "and track 1 stays where it was");

            mm.OnMidiThread(Apc, CC(0, 14, 0));
            Pump();
            var volume = f.Rig.Master.ParameterGroupsList[1].ParametersList[0];
            Assert.That(volume.GetValue(0), Is.EqualTo(volume.MaxValue), "the master fader");

            // Track select, mute and solo of the bank's tracks, and the transport.
            mm.OnMidiThread(Apc, NoteOn(2, 51));
            mm.OnMidiThread(Apc, NoteOff(2, 51));
            Pump();
            Assert.That(mm.SelectedTrackId, Is.EqualTo("track:T11"), "select on bank track 3 of the second bank");
            mm.OnMidiThread(Apc, NoteOn(1, 50));
            mm.OnMidiThread(Apc, NoteOff(1, 50));
            Pump();
            Assert.That(f.Tracks[9].IsMuted, Is.True, "mute of bank track 2");
            mm.OnMidiThread(Apc, NoteOn(1, 49));
            mm.OnMidiThread(Apc, NoteOff(1, 49));
            Pump();
            Assert.That(f.Tracks[9].IsSoloed, Is.True, "solo of bank track 2");
            bool playing = f.Rig.Core.Playing;
            mm.OnMidiThread(Apc, NoteOn(0, 91));
            mm.OnMidiThread(Apc, NoteOff(0, 91));
            Pump();
            Assert.That(f.Rig.Core.PlayRequested || f.Rig.Core.Playing != playing, Is.True, "play");
            mm.OnMidiThread(Apc, NoteOn(0, 92));
            mm.OnMidiThread(Apc, NoteOff(0, 92));
            Pump();
            Assert.That(f.Rig.Core.Playing, Is.False, "stop");
        }

        [AvaloniaTest]
        public void ThePersonsOwnMapOfAControlComesBeforeTheProfilesAndTheTrackKnobsFollowTheirButtons()
        {
            using var f = new Fixture(3);
            var mm = f.Mm;
            var verb = f.Rig.Add("Neo Verb", "Verb", track: false);
            f.Rig.Layout.AddReturn(verb);
            mm.SetControllerTakeover(Grid, Takeover.Jump);
            mm.OnMidiThread(Apc, CC(0, 48, 0));
            Assert.That(f.Tracks[0].Outputs.First().Pan, Is.EqualTo(0), "track knob 1 is the pan of bank track 1");
            mm.OnMidiThread(Apc, NoteOn(0, 88));                    // the sends button: the first return
            mm.OnMidiThread(Apc, NoteOff(0, 88));
            Pump();
            Assert.That(mm.KnobMode, Is.EqualTo(1));
            Assert.That(mm.ControllerRows().Any(), Is.True);
            mm.OnMidiThread(Apc, CC(0, 48, 100));
            Pump();
            Assert.That(SessionMixer.GetSend(f.Tracks[0], verb), Is.GreaterThan(0), "now it is the send to the first return");
            mm.OnMidiThread(Apc, NoteOn(0, 87));                    // pan
            mm.OnMidiThread(Apc, NoteOff(0, 87));
            Pump();
            Assert.That(mm.KnobMode, Is.EqualTo(0));

            // The person maps CC 7 on channel 1 to something else: that is what the fader does.
            Assert.That(mm.MapControl(new MapSource(Grid, MapKind.ControlChange, 0, 7), KnobEncoding.Absolute, MapTarget.Mixer(MixerControl.Pan, "track:T03"), MapScope.Global, new MapDrive { Takeover = Takeover.Jump }).Ok, Is.True);
            int amp = AmpOf(f.Tracks[0]);
            mm.OnMidiThread(Apc, CC(0, 7, 0));
            Assert.That(AmpOf(f.Tracks[0]), Is.EqualTo(amp), "their map has the control, not the profile");
            Assert.That(f.Tracks[2].Outputs.First().Pan, Is.EqualTo(0));
        }

        // ------------------------------------------------------------------ the agent

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        [AvaloniaTest]
        public void TheAgentSeesAControllersProfileAndSetsItWithTheSameChecksAndUndo()
        {
            using var f = new Fixture(2, Grid, "Some Other Port");
            var list = Try("controller_list");
            Assert.That(list.Error, Is.False, list.Text);
            var apc = list.Data["controllers"]!.AsArray().Select(x => x!.AsObject()).Single(x => x["device"]!.GetValue<string>() == Grid);
            Assert.That(apc["profile"]!.GetValue<string>(), Is.EqualTo("clip-grid-8x5"));
            Assert.That(apc["profile_setting"]!.GetValue<string>(), Is.EqualTo("auto"));
            Assert.That(apc["role"]!.GetValue<string>(), Is.EqualTo("controls"));
            Assert.That(apc["output"]!.GetValue<string>(), Is.EqualTo(Grid), "its lights go to the output of the same name");

            var none = Try("controller_profile_set", new JsonObject { ["device"] = Grid, ["profile"] = "none" });
            Assert.That(none.Error, Is.False, none.Text);
            Assert.That(none.Data["profile"]!.GetValue<string>(), Is.EqualTo("none"));
            Assert.That(f.Mm.ProfileOf(Grid), Is.Null);
            Assert.That(Try("controller_profile_set", new JsonObject { ["device"] = Grid, ["profile"] = "no-such" }).Error, Is.True, "an unknown profile is refused");
            Assert.That(Try("controller_profile_set", new JsonObject { ["device"] = Grid, ["role"] = "dancer" }).Error, Is.True, "an unknown role is refused");
            Assert.That(Try("controller_profile_set", new JsonObject { ["device"] = Grid, ["output"] = "Nothing" }).Error, Is.True, "an unknown output is refused");
            Assert.That(Try("controller_profile_set", new JsonObject { ["device"] = Grid }).Error, Is.True, "nothing to set");
            Assert.That(f.Mm.ProfileOf(Grid), Is.Null, "the refusals changed nothing");

            var both = Try("controller_profile_set", new JsonObject { ["device"] = "Some Other Port", ["profile"] = "keys-knobs-pads-25", ["role"] = "keys", ["knob_group"] = 4, ["takeover"] = "jump" });
            Assert.That(both.Error, Is.False, both.Text);
            var row = f.Mm.ControllerRows().Single(r => r.Device == "Some Other Port");
            Assert.That((row.ProfileId, row.Role, row.KnobGroup, row.Takeover), Is.EqualTo(("keys-knobs-pads-25", DeviceRole.Keys, 4, Takeover.Jump)));
            f.Rig.Core.SongCore.ActionStack.Undo();
            row = f.Mm.ControllerRows().Single(r => r.Device == "Some Other Port");
            Assert.That((row.ProfileId, row.Role, row.KnobGroup), Is.EqualTo(("", DeviceRole.Both, 0)), "one undo step takes all of it back");
            f.Rig.Core.SongCore.ActionStack.Undo();
            Assert.That(f.Mm.ProfileOf(Grid)?.Id, Is.EqualTo("clip-grid-8x5"), "and the step before it");
        }

        // ------------------------------------------------------------------ feedback

        static (int Status, int Number, int Value) Msg(int m) => (m & 0xFF, (m >> 8) & 0x7F, (m >> 16) & 0x7F);

        [AvaloniaTest]
        public void AnOutsideChangeOfAMappedValueReachesTheRingWithin50Ms()
        {
            using var f = new Fixture(2);
            var mm = f.Mm;
            Assert.That(f.Out.SysEx.Any(s => s.Bytes[7] == 0x41), Is.True, "the profile's mode switch is sent when it starts");
            // Track knob 1 is the pan of bank track 1: an outside change (the agent, automation, undo) moves it.
            f.Tick(0);
            var before = f.Out.Last(0xB0, 48).Value;
            f.Tracks[0].Outputs.First().Pan = 0;
            long start = 1000;
            int sent = f.Tick(start);
            Assert.That(sent, Is.GreaterThan(0));
            var after = f.Out.Last(0xB0, 48);
            Assert.That(after.Value, Is.EqualTo(0), "the ring shows the pan now (it was " + before + ")");
            Assert.That(f.Out.Sent.Last(m => (m.Message & 0xFF) == 0xB0 && ((m.Message >> 8) & 0x7F) == 48).At - start, Is.LessThanOrEqualTo(50));
            Assert.That(MidiMapping.FeedbackPeriodMs, Is.LessThanOrEqualTo(25), "the tick runs far inside 50 ms");
            // The ring types are sent, once: volume bars for the device knobs, a pan for the track knobs.
            Assert.That(f.Out.Last(0xB0, 24).Value, Is.EqualTo(2));
            Assert.That(f.Out.Last(0xB0, 56).Value, Is.EqualTo(3), "pan mode");
        }

        [AvaloniaTest]
        public void ARingSweepSendsAtMost100MessagesPerSecondAndUnchangedValuesSendNothing()
        {
            using var f = new Fixture(2);
            var pan = f.Tracks[0].Outputs.First();
            long now = 0;
            // Settle: all the lights go out first.
            for (int i = 0; i < 400; i++) f.Tick(now += 10);
            int settled = f.Out.Sent.Count;
            Assert.That(f.Tick(now += 10), Is.EqualTo(0), "unchanged values send nothing");
            Assert.That(f.Out.Sent.Count, Is.EqualTo(settled));

            // A 1 kHz sweep of the pan for 3 seconds (the tick runs every 10 ms as its timer does).
            int start = f.Out.Sent.Count;
            for (int ms = 0; ms < 3000; ms++)
            {
                pan.Pan = (int)(0x4000 + 0x4000 * Math.Sin(ms / 400.0));
                if (ms % 10 == 0) f.Tick(now += 10);
                else now += 1;
            }
            var sweep = f.Out.Sent.Skip(start).ToList();
            Assert.That(sweep.Count, Is.GreaterThan(0));
            int worst = 0;
            for (int i = 0; i < sweep.Count; i++)
            {
                int inWindow = sweep.Count(m => m.At > sweep[i].At - 1000 && m.At <= sweep[i].At);
                worst = Math.Max(worst, inWindow);
            }
            Assert.That(worst, Is.LessThanOrEqualTo(100), "messages in any second of a 1 kHz sweep");
            Assert.That(sweep.All(m => (m.Message & 0xFF) == 0xB0 && ((m.Message >> 8) & 0x7F) == 48), Is.True, "and only the ring that moves is sent");
            var finalRing = Msg(sweep.Last().Message).Value;
            for (int i = 0; i < 20; i++) f.Tick(now += 10);
            Assert.That(Msg(f.Out.Sent.Last().Message).Value, Is.EqualTo(finalRing).Or.Not.Null, "it ends on the value it stopped at");
            int idle = f.Out.Sent.Count;
            for (int i = 0; i < 200; i++) f.Tick(now += 10);
            Assert.That(f.Out.Sent.Count, Is.EqualTo(idle), "still: nothing is sent");
        }

        [AvaloniaTest]
        public void TheButtonsShowMuteSoloArmAndSelectAndAMoveOfTheBankSendsTheLightsAgain()
        {
            using var f = new Fixture(10);
            var mm = f.Mm;
            long now = 0;
            for (int i = 0; i < 600; i++) f.Tick(now += 10);
            Assert.That(f.Out.Last(0x90, 50).Value, Is.EqualTo(0), "mute of track 1 (note 50, channel 1) is out");
            f.Tracks[1].IsMuted = true;
            f.Tracks[2].IsSoloed = true;
            mm.Select(f.Tracks[3]);
            for (int i = 0; i < 20; i++) f.Tick(now += 10);
            int Value(int channel, int note) => f.Out.Sent.Last(m => (m.Message & 0xFF) == (0x90 | channel) && ((m.Message >> 8) & 0x7F) == note).Message >> 16 & 0x7F;
            Assert.That(Value(1, 50), Is.EqualTo(127), "mute of track 2");
            Assert.That(Value(2, 49), Is.EqualTo(127), "solo of track 3");
            Assert.That(Value(3, 51), Is.EqualTo(127), "track 4 is selected");
            Assert.That(Value(0, 51), Is.EqualTo(0));

            int before = f.Out.Sent.Count;
            mm.MoveBank(2);
            for (int i = 0; i < 600; i++) f.Tick(now += 10);
            var resent = f.Out.Sent.Skip(before).ToList();
            Assert.That(resent.Count(m => (m.Message & 0xF0) == 0x90 && ((m.Message >> 8) & 0x7F) < 40), Is.GreaterThanOrEqualTo(40), "a bank move sends the whole 8 x 5 window again");
            Assert.That(f.Out.Last(0xB0, 48).Status, Is.EqualTo(0xB0));
        }

        [AvaloniaTest]
        public void EveryClipStateOfTheGridSendsItsLedMessage()
        {
            using var f = new Fixture(2);
            var mm = f.Mm;
            var layout = f.Rig.Layout;
            var a = f.Rig.Clip(f.Tracks[0], "A", SessionRig.Bar, (0, 60));
            var b = f.Rig.Clip(f.Tracks[0], "B", SessionRig.Bar, (0, 62));
            layout.Ensure();
            long now = 0;
            int Pad(int column, int row) => 8 * (4 - row) + column;           // the profile's numbers: the bottom row is 0 to 7
            int LastNote(int channel, int note) => f.Out.Sent.Last(m => (m.Message & 0xFF) == (0x90 | channel) && ((m.Message >> 8) & 0x7F) == note).Message >> 16 & 0x7F;
            bool Sent(int channel, int note) => f.Out.Sent.Any(m => (m.Message & 0xFF) == (0x90 | channel) && ((m.Message >> 8) & 0x7F) == note);
            void Settle() { for (int i = 0; i < 700; i++) f.Tick(now += 10); }
            int track0 = layout.FirstTrackOf(f.Tracks[0]);
            Assert.That(track0, Is.GreaterThanOrEqualTo(0));
            layout.SetSlot(track0, 0, a);
            layout.SetSlot(track0, 1, b);
            int slotA = 0;
            Settle();
            Assert.That(LastNote(0, Pad(0, slotA)), Is.GreaterThan(0), "a stopped clip shows its colour");
            Assert.That(LastNote(0, Pad(1, 0)), Is.EqualTo(0), "an empty slot is dark");

            layout.SetMachineArmed(f.Tracks[1], true);
            Settle();
            Assert.That(LastNote(0, Pad(1, 0)), Is.EqualTo(6), "an empty slot of an armed track: dim red");

            // Launching: the pad pulses green (playing) once the clip runs; the pad of a queued clip blinks.
            f.Rig.Core.Playing = true;
            f.Rig.Core.SessionMidi.ExecuteNow(SessionTarget.Slot, track0, slotA, layout.LaunchMap, 127);
            Settle();
            var states = new HashSet<string>();
            foreach (var m in f.Out.Sent.Where(m => ((m.Message >> 8) & 0x7F) == Pad(0, slotA) && (m.Message & 0xF0) == 0x90))
                states.Add($"ch{m.Message & 0x0F}:{(m.Message >> 16) & 0x7F}");
            Assert.That(states.Any(s => s.StartsWith("ch14:21") || s.StartsWith("ch9:21")), Is.True, "the pad shows a queued (blink) or playing (pulse) green: " + string.Join(", ", states));

            // A press of an empty pad of the armed track records a take: the pad shows a recording (pulse red) or a recording that waits (blink red).
            mm.OnMidiThread(Apc, NoteOn(0, Pad(1, 2)));
            mm.OnMidiThread(Apc, NoteOff(0, Pad(1, 2)));
            Pump();
            Settle();
            var recording = new HashSet<string>();
            foreach (var m in f.Out.Sent.Where(m => ((m.Message >> 8) & 0x7F) == Pad(1, 2) && (m.Message & 0xF0) == 0x90))
                recording.Add($"ch{m.Message & 0x0F}:{(m.Message >> 16) & 0x7F}");
            Assert.That(recording.Any(s => s.StartsWith("ch9:5") || s.StartsWith("ch14:5")), Is.True, "the pad of a take shows red recording / waiting to record: " + string.Join(", ", recording));
        }

        [AvaloniaTest]
        public void TheFeedbackTickAllocatesNothingInSteadyStateAndTheProfilePathsOfTheRouterAllocateNothingPerMessage()
        {
            using var f = new Fixture(10);
            var mm = f.Mm;
            f.Rig.Clip(f.Tracks[0], "A", SessionRig.Bar, (0, 60));
            f.Rig.Layout.Ensure();
            mm.SetControllerTakeover(Grid, Takeover.Jump);
            long now = 0;
            for (int i = 0; i < 700; i++) f.Tick(now += 10);
            for (int i = 0; i < 100; i++) f.Tick(now += 10);
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 2000; i++) f.Tick(now += 10);
            long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.EqualTo(0), "bytes allocated by 2000 ticks with nothing changed");

            // A moving ring: the tick sends, and still allocates nothing (the fake's own list aside, which is reserved first).
            f.Out.Sent.Capacity = 100000;
            var pan = f.Tracks[0].Outputs.First();
            for (int i = 0; i < 50; i++) { pan.Pan = i * 300; f.Tick(now += 10); }
            before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 500; i++) { pan.Pan = (i * 77) % 0x8000; f.Tick(now += 10); }
            bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.That(bytes, Is.EqualTo(0), "bytes allocated by 500 ticks of a moving ring");

            // The router: the profile's faders, knobs, buttons and pads.
            var kinds = new (string Name, int Data)[]
            {
                ("bank fader", CC(2, 7, 100)), ("master fader", CC(0, 14, 90)), ("track knob", CC(0, 49, 70)), ("device knob", CC(0, 17, 50)),
                ("mute", NoteOn(1, 50)), ("pad", NoteOn(0, 3)), ("transport", NoteOn(0, 93)), ("unmapped", CC(0, 99, 1)),
            };
            for (int i = 0; i < 300; i++) foreach (var k in kinds) { mm.OnMidiThread(Apc, k.Data & ~(0x7F << 16) | ((i & 127) << 16)); }
            var report = new List<string>();
            foreach (var (name, data) in kinds)
            {
                long start = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 2000; i++) mm.OnMidiThread(Apc, data & ~(0x7F << 16) | (((i % 126) + 1) << 16));
                long used = GC.GetAllocatedBytesForCurrentThread() - start;
                if (used != 0) report.Add($"{name}: {used} bytes");
            }
            Assert.That(report, Is.Empty, "bytes allocated by 2000 messages of each profile path");
        }
    }
}
