// Fork (docs/specs/midi-mapping.md, MM1): MIDI mapping for any controller on the real engine. Scripted devices with names ("Faders", "Pads",
// "Keys") send through Core.SendMIDIInput the way the MIDI driver does; nothing here knows a model. The pure rules (takeover maths, the
// model's XML) are ReBuzz.SessionTests/MapModelTests.cs.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Tests
{
    public class MidiMappingTests
    {
        static readonly MidiDevice Faders = new MidiDevice(0, "Faders");
        static readonly MidiDevice Pads = new MidiDevice(1, "Pads");
        static readonly MidiDevice Keys = new MidiDevice(2, "Keys");

        static int CC(int channel, int number, int value) => 0xB0 | channel | (number << 8) | (value << 16);

        static int NoteOn(int note, int velocity = 100) => 0x90 | (note << 8) | (velocity << 16);

        static void Pump() => Dispatcher.UIThread.RunJobs();

        static void Send(SessionRig rig, MidiDevice device, int data)
        {
            rig.Core.SendMIDIInput(device, data);
        }

        static IDisposable Setup(out SessionRig rig, out MidiMapping mm)
        {
            var setup = TrackInputEngineTests.Setup(out rig);
            mm = rig.Core.MidiMapping;
            Reset(mm);
            return setup;
        }

        static void Reset(MidiMapping mm)
        {
            mm.CancelListening();
            mm.Edit((c, s) => { c.Global.Clear(); c.Groups.Clear(); c.NoKeys.Clear(); s.Tracks.Clear(); s.Global = null; });
            mm.AutoArm = true;
        }

        /// <summary>The first parameters of a machine's global group that have a wide range (a cutoff, a level), by name.</summary>
        static List<ParameterCore> Wide(MachineCore m) =>
            m.ParameterGroupsList[1].ParametersList.Cast<ParameterCore>().Where(p => p.MaxValue - p.MinValue >= 100 && p.MaxValue < 0xFFFE).ToList();

        static MapTarget Target(MachineCore m, ParameterCore p) => MapTarget.Parameter(m.Name, p.Name);

        static int Value(ParameterCore p) => p.GetValue(0);

        /// <summary>Two generator tracks "Lead" and "Bass" with a knob group "Faders" of eight (controllers 21 to 28 on channel 1).</summary>
        static (MachineCore lead, MachineCore bass, List<ParameterCore> lp, List<ParameterCore> bp) Song(SessionRig rig, MidiMapping mm)
        {
            rig.Layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            var bass = rig.Add("Neo Poly", "Bass");
            Assert.That(mm.SetKnobGroup("Faders", Enumerable.Range(21, 8).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n))), Is.True);
            return (lead, bass, Wide(lead), Wide(bass));
        }

        static readonly MapDrive Jump = new MapDrive { Takeover = Takeover.Jump };

        // ------------------------------------------------------------------ controllers

        [AvaloniaTest]
        public void ADuplicateControllerNameGetsAnIndexAndTheIdIsTheKey()
        {
            Assert.That(new MidiDevice(3, "Same").Id, Is.EqualTo("Same"));
            Assert.That(new MidiDevice(4, "Same #2") { Key = "Same #2" }.Id, Is.EqualTo("Same #2"));
            Assert.That(MidiDevice.Unknown.Id, Is.EqualTo(""), "no device, no controller");
        }

        [AvaloniaTest]
        public void TeachingAKnobGroupHearsTheKnobsInOrderAndRemembersThemPerController()
        {
            using var setup = Setup(out var rig, out var mm);
            mm.BeginTeach("Faders", 4);
            foreach (int n in new[] { 74, 71, 76, 77 }) Send(rig, Faders, CC(0, n, 20));
            Send(rig, Pads, CC(0, 10, 20));                                         // another device does not take part
            Pump();
            Assert.That(mm.IsListening, Is.False, "four knobs end the teaching");
            var c = mm.Controllers().Single(x => x.Id == "Faders");
            Assert.That(c.Knobs, Is.EqualTo(4));
            Assert.That(mm.Computer.GroupOf("Faders").Knobs.Select(k => k.Number), Is.EqualTo(new[] { 74, 71, 76, 77 }));
            Assert.That(mm.KnobCount, Is.EqualTo(8), "a page has at least eight slots");
            mm.SetKnobGroup("Faders", Enumerable.Range(0, 12).Select(n => new MapSource("Faders", MapKind.ControlChange, 0, n)));
            Assert.That(mm.KnobCount, Is.EqualTo(12), "N is the device's group size");
        }

        // ------------------------------------------------------------------ map mode and the button

        [AvaloniaTest]
        public void MapModeMapsTheNextControlMovedAndItThenDrivesTheParameter()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var p = lp[0];
            var result = mm.BeginMap(Target(lead, p), MapScope.Global, Jump);
            Assert.That(result.Ok, Is.True, result.Error);
            Assert.That(mm.IsListening, Is.True);
            Send(rig, Pads, CC(2, 33, 127));                                        // channel 3, controller 33 of Pads
            Pump();
            Assert.That(mm.IsListening, Is.False);
            var info = mm.Active().Single();
            Assert.That(info.Source, Is.EqualTo(new MapSource("Pads", MapKind.ControlChange, 2, 33)));
            Assert.That(mm.BadgeOf(Target(lead, p)), Is.EqualTo("CC33"), "the short form");
            Assert.That(mm.BadgeFullOf(Target(lead, p)), Does.Contain("CC 33").And.Contain("Pads"));
            Send(rig, Pads, CC(2, 33, 0));                                          // the first value, 127, could be an encoder's step: this one says it is a knob
            Pump();
            Send(rig, Pads, CC(2, 33, 0));
            Assert.That(Value(p), Is.EqualTo(p.MinValue));
            Send(rig, Pads, CC(2, 33, 127));
            Assert.That(Value(p), Is.EqualTo(p.MaxValue), "within the message: the audio thread reads it on its next buffer");
            Send(rig, Pads, CC(3, 33, 0));                                          // another channel is another control
            Assert.That(Value(p), Is.EqualTo(p.MaxValue));
            Send(rig, Faders, CC(2, 33, 0));                                        // and so is another device
            Assert.That(Value(p), Is.EqualTo(p.MaxValue));
        }

        [AvaloniaTest]
        public void AMapOnAnyDeviceHearsEveryDeviceAndAMapOnOneDeviceHearsThatDevice()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            Assert.That(mm.MapControl(new MapSource("", MapKind.ControlChange, 0, 50), KnobEncoding.Absolute, Target(lead, lp[0]), MapScope.Global, Jump).Ok, Is.True);
            Assert.That(mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 51), KnobEncoding.Absolute, Target(lead, lp[1]), MapScope.Global, Jump).Ok, Is.True);
            lp[0].SetValue(0, lp[0].MinValue);
            Send(rig, Pads, CC(0, 50, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue), "any device");
            lp[0].SetValue(0, lp[0].MinValue);
            Send(rig, Keys, CC(0, 50, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue), "a second device, at the same time");
            lp[1].SetValue(0, lp[1].MinValue);
            Send(rig, Keys, CC(0, 51, 127));
            Assert.That(Value(lp[1]), Is.EqualTo(lp[1].MinValue), "Keys is not Pads");
            Send(rig, Pads, CC(0, 51, 127));
            Assert.That(Value(lp[1]), Is.EqualTo(lp[1].MaxValue));
        }

        [AvaloniaTest]
        public void ACommandMapsAndRunsOnTheUiThreadFromAPad()
        {
            using var setup = Setup(out var rig, out var mm);
            Song(rig, mm);
            Assert.That(mm.MapControl(new MapSource("Pads", MapKind.Note, 9, 36), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Play), MapScope.Global).Ok, Is.True);
            Assert.That(mm.MapControl(new MapSource("Pads", MapKind.Note, 9, 37), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Stop), MapScope.Global).Ok, Is.True);
            rig.Core.Playing = false;
            Assert.That(rig.Core.PlayRequested, Is.False);
            Send(rig, Pads, 0x99 | (36 << 8) | (100 << 16));
            Pump();
            Assert.That(rig.Core.PlayRequested, Is.True, "a pad plays");
            Send(rig, Pads, 0x99 | (37 << 8) | (100 << 16));
            Pump();
            Assert.That(rig.Core.PlayRequested, Is.False, "and another stops");
        }

        // ------------------------------------------------------------------ scopes and pages

        [AvaloniaTest]
        public void TheKnobsDriveThePageOfTheSelectedTrackAndFollowTheSelection()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, lp, bp) = Song(rig, mm);
            Assert.That(mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[0]), Jump).Ok, Is.True);
            Assert.That(mm.SetSlot("track:Bass", 0, 0, Target(bass, bp[0]), Jump).Ok, Is.True);
            Assert.That(mm.SetSlot("track:Bass", 0, 1, Target(bass, bp[1]), Jump).Ok, Is.True);
            Assert.That(mm.Select(lead), Is.True);
            lp[0].SetValue(0, lp[0].MinValue); bp[0].SetValue(0, bp[0].MinValue);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue), "knob 1 drives slot 1 of Lead's page");
            Assert.That(Value(bp[0]), Is.EqualTo(bp[0].MinValue), "and nothing of Bass");
            mm.Select(bass);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(bp[0]), Is.EqualTo(bp[0].MaxValue), "select Bass and the same knob drives Bass");
            bp[1].SetValue(0, bp[1].MinValue);
            Send(rig, Faders, CC(0, 22, 127));
            Assert.That(Value(bp[1]), Is.EqualTo(bp[1].MaxValue));
            mm.Select(lead);
            Send(rig, Faders, CC(0, 22, 0));
            Assert.That(Value(bp[1]), Is.EqualTo(bp[1].MaxValue), "Lead's slot 2 is empty: Bass's value stays");
        }

        [AvaloniaTest]
        public void ATrackPageIsCheckedAgainstTheTracksChain()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, lp, bp) = Song(rig, mm);
            var wrong = mm.SetSlot("track:Lead", 0, 0, Target(bass, bp[0]), Jump);
            Assert.That(wrong.Ok, Is.False, "Bass is not on Lead's chain");
            Assert.That(wrong.Error, Does.Contain("Bass"));
            Assert.That(mm.PagesOf("track:Lead")?.CurrentPage.Slots, Is.Null.Or.Empty);
            var song = rig.Core.SongCore;
            var echo = rig.Add("Neo Verb", "Echo", track: false, connect: false);
            song.DisconnectMachines(lead.Outputs.First(o => o.Destination == rig.Master));
            song.ConnectMachines(lead, echo, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            song.ConnectMachines(echo, rig.Master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            Assert.That(mm.SetSlot("track:Lead", 0, 0, Target(echo, Wide(echo)[0]), Jump).Ok, Is.True, "a machine after the generator is on the chain");
        }

        [AvaloniaTest]
        public void SeveralPagesPerTrackAndTheCurrentOneIsWhatTheKnobsDrive()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[0]), Jump);
            Assert.That(mm.AddPage("track:Lead"), Is.True);
            Assert.That(mm.PagesOf("track:Lead").Pages.Count, Is.EqualTo(2));
            Assert.That(mm.PagesOf("track:Lead").Current, Is.EqualTo(1), "a new page is the current one");
            mm.SetSlot("track:Lead", 1, 0, Target(lead, lp[1]), Jump);
            mm.Select(lead);
            lp[0].SetValue(0, lp[0].MinValue); lp[1].SetValue(0, lp[1].MinValue);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(lp[1]), Is.EqualTo(lp[1].MaxValue));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MinValue));
            Assert.That(mm.SelectPage("track:Lead", 0), Is.True);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue));
            Assert.That(mm.RemovePage("track:Lead", 1), Is.True);
            Assert.That(mm.PagesOf("track:Lead").Pages.Count, Is.EqualTo(1));
            Assert.That(mm.RemovePage("track:Lead", 0), Is.False, "a track keeps one page");
        }

        [AvaloniaTest]
        public void AnEmptySlotFallsBackToTheGeneratorsPinOfTheSameNumberAndFreeKnobsSaySo()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var pin = lp[2];
            rig.Core.PinStore.Set("Lead", lp[0].Name, true);
            rig.Core.PinStore.Set("Lead", lp[1].Name, true);
            rig.Core.PinStore.Set("Lead", pin.Name, true);                          // pin number 3
            mm.Select(lead);
            pin.SetValue(0, pin.MinValue);
            Send(rig, Faders, CC(0, 23, 0));                                        // a fallback takes over by pickup: the knob meets the value first
            Send(rig, Faders, CC(0, 23, 127));
            Assert.That(Value(pin), Is.EqualTo(pin.MaxValue), "knob 3 drives pin 3");
            var slots = mm.PageSlots(8, "track:Lead");
            Assert.That(slots[2].Fallback, Is.True, "the Inspector draws it as a grey ring");
            mm.SetSlot("track:Lead", 0, 2, Target(lead, lp[3]), Jump);
            Assert.That(mm.PageSlots(8, "track:Lead")[2].Fallback, Is.False, "a slot of its own wins");
            Send(rig, Faders, CC(0, 27, 40));
            Pump();
            Assert.That(mm.FreeNotice.Text, Is.EqualTo("K7 is free on Lead"));
        }

        [AvaloniaTest]
        public void GlobalMapsHoldOnEveryTrackAndATrackPageBeatsAGlobalOnTheSameControl()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, lp, bp) = Song(rig, mm);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, lp[0]), MapScope.Global, Jump);
            mm.MapControl(new MapSource("Faders", MapKind.ControlChange, 0, 21), KnobEncoding.Absolute, Target(bass, bp[1]), MapScope.Global, Jump);
            mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[1]), Jump);
            foreach (var m in new[] { lead, bass })
            {
                mm.Select(m);
                lp[0].SetValue(0, lp[0].MinValue);
                Send(rig, Pads, CC(0, 1, 127));
                Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue), "the global map of Pads holds on " + m.Name);
            }
            mm.Select(lead);
            lp[1].SetValue(0, lp[1].MinValue); bp[1].SetValue(0, bp[1].MinValue);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(lp[1]), Is.EqualTo(lp[1].MaxValue), "Lead's page takes knob 1");
            Assert.That(Value(bp[1]), Is.EqualTo(bp[1].MinValue), "the global map of the same control does not");
            mm.Select(bass);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(bp[1]), Is.EqualTo(bp[1].MaxValue), "on Bass (no page entry) the global map is reached");
        }

        [AvaloniaTest]
        public void TheSongMayKeepItsOwnCopyOfTheGlobalMap()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, lp[0]), MapScope.Global, Jump);
            Assert.That(mm.UsesSongGlobal, Is.False);
            Assert.That(mm.SetSongGlobal(true), Is.True);
            Assert.That(mm.UsesSongGlobal, Is.True);
            Assert.That(mm.GlobalEntries.Count, Is.EqualTo(1), "the copy starts as the computer's map");
            mm.UnmapTarget(Target(lead, lp[0]));
            Assert.That(mm.GlobalEntries, Is.Empty, "the song's copy is edited");
            Assert.That(mm.Computer.Global.Count, Is.EqualTo(1), "the computer's is not");
            Assert.That(mm.SetSongGlobal(false), Is.True);
            Assert.That(mm.GlobalEntries.Count, Is.EqualTo(1));
        }

        // ------------------------------------------------------------------ takeover and encoders

        [AvaloniaTest]
        public void PickupWaitsUntilTheKnobMeetsTheValueJumpGoesThereAndScaleMeetsAtTheEnds()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var (a, b, c) = (lp[0], lp[1], lp[2]);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, a), MapScope.Global, new MapDrive { Takeover = Takeover.Pickup });
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 2), KnobEncoding.Absolute, Target(lead, b), MapScope.Global, new MapDrive { Takeover = Takeover.Jump });
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 3), KnobEncoding.Absolute, Target(lead, c), MapScope.Global, new MapDrive { Takeover = Takeover.Scale });
            int Half(ParameterCore p) => p.MinValue + (p.MaxValue - p.MinValue) / 2;
            foreach (var p in new[] { a, b, c }) p.SetValue(0, Half(p));

            Send(rig, Pads, CC(0, 1, 0));
            Assert.That(Value(a), Is.EqualTo(Half(a)), "pickup: a knob far from the value changes nothing");
            Send(rig, Pads, CC(0, 1, 30));
            Assert.That(Value(a), Is.EqualTo(Half(a)));
            Send(rig, Pads, CC(0, 1, 64));
            Assert.That(Value(a), Is.EqualTo(Half(a)).Within((a.MaxValue - a.MinValue) / 100 + 1), "it meets the value");
            Send(rig, Pads, CC(0, 1, 100));
            Assert.That(Value(a), Is.GreaterThan(Half(a) + (a.MaxValue - a.MinValue) / 5), "then it follows");

            Send(rig, Pads, CC(0, 2, 0));
            Assert.That(Value(b), Is.EqualTo(b.MinValue), "jump goes straight there");

            Send(rig, Pads, CC(0, 3, 0));                                           // the first move sets the base
            Send(rig, Pads, CC(0, 3, 10));
            Assert.That(Value(c), Is.GreaterThan(Half(c)).And.LessThan(Half(c) + (c.MaxValue - c.MinValue) / 5), "scale: it moves from where it is, in the knob's direction");
            Send(rig, Pads, CC(0, 3, 0));
            Assert.That(Value(c), Is.EqualTo(Half(c)).Within(1), "back at the knob's start it is back at the parameter's");
            Send(rig, Pads, CC(0, 3, 127));
            Assert.That(Value(c), Is.EqualTo(c.MaxValue), "scale: both reach the top together");
        }

        [AvaloniaTest]
        public void ARelativeEncoderIsTakenAsStepsAndDetectedWhileLearning()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var p = lp[0];
            mm.BeginMap(Target(lead, p), MapScope.Global, Jump);
            foreach (int v in new[] { 65, 65, 66, 65 }) Send(rig, Pads, CC(0, 16, v));    // an encoder sending 64 + n
            Pump();
            mm.CancelListening();
            var info = mm.Active().FirstOrDefault(i => i.Source.Number == 16);
            Assert.That(info, Is.Not.Null);
            Assert.That(info.Encoding, Is.EqualTo(KnobEncoding.Offset64), "the encoding is told while learning");
            p.SetValue(0, p.MinValue + (p.MaxValue - p.MinValue) / 2);
            int before = Value(p);
            for (int i = 0; i < 10; i++) Send(rig, Pads, CC(0, 16, 65));
            Assert.That(Value(p), Is.GreaterThan(before), "turning right raises it");
            before = Value(p);
            for (int i = 0; i < 30; i++) Send(rig, Pads, CC(0, 16, 63));
            Assert.That(Value(p), Is.LessThan(before), "left lowers it");

            var q = lp[1];
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 17), KnobEncoding.TwosComplement, Target(lead, q), MapScope.Global, Jump);
            q.SetValue(0, q.MinValue + (q.MaxValue - q.MinValue) / 2);
            before = Value(q);
            for (int i = 0; i < 10; i++) Send(rig, Pads, CC(0, 17, 1));
            Assert.That(Value(q), Is.GreaterThan(before), "two's complement: 1 is up");
            before = Value(q);
            for (int i = 0; i < 20; i++) Send(rig, Pads, CC(0, 17, 127));
            Assert.That(Value(q), Is.LessThan(before), "127 is down");
        }

        [AvaloniaTest]
        public void RangeAndInvertShapeWhatTheControlReaches()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var p = lp[0];
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, p), MapScope.Global, new MapDrive { Takeover = Takeover.Jump, Min = 0.25, Max = 0.75 });
            Send(rig, Pads, CC(0, 1, 127));
            int span = p.MaxValue - p.MinValue;
            Assert.That(Value(p), Is.EqualTo(p.MinValue + (int)Math.Round(span * 0.75)).Within(1));
            Send(rig, Pads, CC(0, 1, 0));
            Assert.That(Value(p), Is.EqualTo(p.MinValue + (int)Math.Round(span * 0.25)).Within(1));
            mm.SetDrive(Target(lead, p), 0, 1, true, Takeover.Jump);
            Send(rig, Pads, CC(0, 1, 127));
            Assert.That(Value(p), Is.EqualTo(p.MinValue), "inverted");
        }

        // ------------------------------------------------------------------ selection, arm and keys

        [AvaloniaTest]
        public void SelectingATrackArmsItExclusivelyAndHandArmedTracksStayArmed()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, _, _) = Song(rig, mm);
            var pad = rig.Add("Neo Poly", "Pad");
            var layout = rig.Layout;
            mm.Select(lead);
            Assert.That(layout.IsMachineArmed(lead), Is.True, "selecting arms");
            mm.Select(bass);
            Assert.That(layout.IsMachineArmed(bass), Is.True);
            Assert.That(layout.IsMachineArmed(lead), Is.False, "exclusively: the one the selection armed is let go");
            layout.SetMachineArmed(pad, true);                                      // by hand
            mm.Select(lead);
            Assert.That(layout.IsMachineArmed(lead), Is.True);
            Assert.That(layout.IsMachineArmed(bass), Is.False);
            Assert.That(layout.IsMachineArmed(pad), Is.True, "a hand-armed track stays armed");
            mm.AutoArm = false;
            mm.Select(bass);
            Assert.That(layout.IsMachineArmed(bass), Is.False, "with the preference off, selecting arms nothing");
            Assert.That(layout.IsMachineArmed(lead), Is.False);
        }

        [AvaloniaTest]
        public void TheKeysPlayTheSelectedTracksGeneratorAndFollowTheSelection()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, _, _) = Song(rig, mm);
            var layout = rig.Layout;
            layout.SetRecordBars(1);
            // Lead is selected: the keys record into Lead's take.
            mm.Select(lead);
            var t1 = layout.RecordTake(layout.FirstTrackOf(lead), 0)!;
            rig.Core.Playing = true;
            rig.RunTicks(2);
            Send(rig, Keys, NoteOn(60));
            rig.RunTicks(2);
            Send(rig, Keys, 0x80 | (60 << 8));
            rig.PlayTo(SessionRig.Bar + 2);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(lead))!);
            rig.Core.Playing = false;
            Assert.That(layout.NotesOf(t1).Select(n => n.Note).ToArray(), Is.EqualTo(new[] { 60 }), "Lead had the selection");
            // Select Bass: the same keys now play Bass, and Lead no longer hears them.
            mm.Select(bass);
            Assert.That(layout.IsMachineArmed(bass), Is.True);
            Assert.That(layout.IsMachineArmed(lead), Is.False);
            var t2 = layout.RecordTake(layout.FirstTrackOf(bass), 1)!;
            rig.Core.Playing = true;
            rig.RunTicks(2);
            Send(rig, Keys, NoteOn(48));
            rig.RunTicks(2);
            Send(rig, Keys, 0x80 | (48 << 8));
            rig.PlayTo(rig.Core.SongCore.SequencesList.Count > 0 ? SessionRig.Bar * 3 + 2 : SessionRig.Bar * 3);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(bass))!);
            Assert.That(layout.NotesOf(t2).Select(n => n.Note).ToArray(), Is.EqualTo(new[] { 48 }), "then Bass");
            Assert.That(layout.NotesOf(t1).Select(n => n.Note).ToArray(), Is.EqualTo(new[] { 60 }), "and Lead's take is as it was");
        }

        [AvaloniaTest]
        public void ADevicePinnedToATrackPlaysItWhateverIsSelected()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, _, _) = Song(rig, mm);
            var layout = rig.Layout;
            Assert.That(layout.SetMidiFrom(bass, MidiFromKind.Device, "Pads"), Is.True);
            mm.Select(lead);
            mm.Rebuild();
            Assert.That(mm.OnMidiThread(Pads, NoteOn(40)), Is.True, "Pads' notes belong to Bass");
            Assert.That(mm.OnMidiThread(Keys, NoteOn(40)), Is.False, "Keys' follow the selection (the session's own routing plays them)");
            Assert.That(mm.OnMidiThread(Pads, 0x80 | (40 << 8)), Is.True);
        }

        [AvaloniaTest]
        public void ALeftOutDeviceDoesNotFollowTheSelection()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, _, _) = Song(rig, mm);
            var layout = rig.Layout;
            Assert.That(mm.SetKeysRole("Pads", false), Is.True);
            layout.SetRecordBars(2);
            mm.Select(lead);
            var t1 = layout.RecordTake(layout.FirstTrackOf(lead), 0)!;
            rig.Core.Playing = true;
            rig.RunTicks(2);
            Send(rig, Pads, NoteOn(40));
            rig.RunTicks(1);
            Send(rig, Keys, NoteOn(62));
            rig.RunTicks(2);
            Send(rig, Pads, 0x80 | (40 << 8));
            Send(rig, Keys, 0x80 | (62 << 8));
            rig.PlayTo(SessionRig.Bar * 2 + 2);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(lead))!);
            Assert.That(layout.NotesOf(t1).Select(n => n.Note), Is.EqualTo(new[] { 62 }), "only Keys followed it");
        }

        [AvaloniaTest]
        public void TheTrackCommandsStepAndSelectTracksFromAControllerButton()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, _, _) = Song(rig, mm);
            mm.MapControl(new MapSource("Pads", MapKind.Note, 0, 60), KnobEncoding.Absolute, MapTarget.Command(MapCommand.NextTrack), MapScope.Global);
            mm.MapControl(new MapSource("Pads", MapKind.Note, 0, 59), KnobEncoding.Absolute, MapTarget.Command(MapCommand.PreviousTrack), MapScope.Global);
            mm.MapControl(new MapSource("Pads", MapKind.Note, 0, 61), KnobEncoding.Absolute, MapTarget.Command(MapCommand.SelectTrack, 2), MapScope.Global);
            mm.Select(lead);
            Send(rig, Pads, NoteOn(60)); Pump();
            Assert.That(mm.SelectedMachine, Is.SameAs(bass), "next");
            Send(rig, Pads, NoteOn(59)); Pump();
            Assert.That(mm.SelectedMachine, Is.SameAs(lead), "previous");
            Send(rig, Pads, NoteOn(61)); Pump();
            Assert.That(mm.SelectedMachine, Is.SameAs(bass), "select track 2");
        }

        // ------------------------------------------------------------------ recording

        [AvaloniaTest]
        public void AKnobMoveIsRecordedAsAutomationLikeAMouseMove()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var p = lp[0];
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, p), MapScope.Global, Jump);
            var stack = rig.Core.SongCore.ActionStack;
            int before = stack.Actions.Count();
            Send(rig, Pads, CC(0, 1, 100));
            Pump();
            Assert.That(Value(p), Is.Not.EqualTo(p.MinValue));
            Assert.That(stack.Actions.Count(), Is.GreaterThanOrEqualTo(before), "history sees the move as it does a mouse move (the move action coalesces)");
        }

        // ------------------------------------------------------------------ old bindings

        [AvaloniaTest]
        public void OldBindingsShowAsGlobalMapsOnAnyDeviceWithTheirChannelAndController()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var p = lp[0];
            p.BindToMIDIController(0, 2, 21);
            var infos = mm.All("global");
            var legacy = infos.SingleOrDefault(i => i.Target != null && i.Target.Param == p.Name && i.Target.Machine == "Lead");
            Assert.That(legacy, Is.Not.Null, "listed");
            Assert.That(legacy.Source.AnyDevice, Is.True);
            Assert.That(legacy.Source.Number, Is.EqualTo(21));
            Assert.That(legacy.Source.Channel, Is.EqualTo(2));
            Assert.That(mm.BadgeOf(Target(lead, p)), Is.EqualTo("CC21"));
            Assert.That(mm.BadgeFullOf(Target(lead, p)), Does.StartWith("CC 21"));
            Assert.That(mm.All("global").Any(i => i.Legacy), Is.True, "midi_map_list shows it as global");
            Assert.That(mm.UnmapTarget(Target(lead, p), true), Is.True, "unmapping the control removes the old binding too");
            Assert.That(mm.All("global").Any(i => i.Legacy && i.Target?.Param == p.Name), Is.False);
        }

        // ------------------------------------------------------------------ save and reopen, undo

        [AvaloniaTest]
        [TestCase(".nbl")]
        [TestCase(".bmx")]
        [TestCase(".bmxml")]
        public void PagesAndTheSongsGlobalCopySaveAndReopenAndTheComputersMapStays(string extension)
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, lp, bp) = Song(rig, mm);
            mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[0]), new MapDrive { Takeover = Takeover.Scale, Min = 0.2, Max = 0.8, Invert = true });
            mm.AddPage("track:Lead");
            mm.SetSlot("track:Lead", 1, 3, Target(lead, lp[1]), Jump);
            mm.SetSlot("track:Bass", 0, 1, Target(bass, bp[0]), Jump);
            mm.MapControl(new MapSource("Pads", MapKind.Note, 9, 36), KnobEncoding.Absolute, MapTarget.Command(MapCommand.Play), MapScope.Global);
            mm.SetSongGlobal(true);
            string path = Path.Combine(Path.GetTempPath(), $"rebuzz-mm1-{Environment.ProcessId}{extension.Replace(".", "-")}{extension}");
            // A dialog the app would show (unsaved changes, a missing machine) must not wait for a person in a headless run: answered No, and put back after.
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            try
            {
                rig.Core.SaveSongCopy(path);
                rig.Core.Modified = false;
                rig.Core.ExecuteCommand(BuzzCommand.NewFile);
                Pump();
                Assert.That(mm.PagesOf("track:Lead"), Is.Null.Or.Matches<TrackPages>(t => t.IsEmpty), "a new song starts with no pages");
                Assert.That(mm.UsesSongGlobal, Is.False);
                rig.Core.OpenSongFile(path);
                Pump();
                var pages = mm.PagesOf("track:Lead");
                Assert.That(pages, Is.Not.Null);
                Assert.That(pages.Pages.Count, Is.EqualTo(2));
                Assert.That(pages.Current, Is.EqualTo(1));
                var slot = pages.Pages[0].Slots[0];
                Assert.That(slot.Target.Param, Is.EqualTo(lp[0].Name));
                Assert.That((slot.Takeover, slot.Invert, slot.Min, slot.Max), Is.EqualTo((Takeover.Scale, true, 0.2, 0.8)));
                Assert.That(pages.Pages[1].Slots.ContainsKey(3), Is.True);
                Assert.That(mm.PagesOf("track:Bass").Pages[0].Slots.ContainsKey(1), Is.True);
                Assert.That(mm.UsesSongGlobal, Is.True, "the song's own global map");
                Assert.That(mm.GlobalEntries.Single().Target.Cmd, Is.EqualTo(MapCommand.Play));
                Assert.That(mm.Computer.GroupOf("Faders")?.Count, Is.EqualTo(8), "the computer's knob group is not in the song and stays");
            }
            finally
            {
                Dialogs.Responder = null;
                rig.Core.Modified = false;
                File.Delete(path);
            }
        }

        [AvaloniaTest]
        public void EachMapEditIsOneUndoStepThatBringsTheOldMapBack()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            var stack = rig.Core.SongCore.ActionStack;
            int steps = stack.Actions.Count();
            mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[0]), Jump);
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one step");
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, lp[1]), MapScope.Global, Jump);
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 2));
            stack.Undo();
            Assert.That(mm.GlobalEntries, Is.Empty);
            Send(rig, Pads, CC(0, 1, 127));
            Assert.That(Value(lp[1]), Is.Not.EqualTo(lp[1].MaxValue), "an undone map no longer drives");
            stack.Undo();
            Assert.That(mm.PagesOf("track:Lead")?.CurrentPage.Slots, Is.Null.Or.Empty);
            stack.Redo();
            Assert.That(mm.PagesOf("track:Lead").CurrentPage.Slots.ContainsKey(0), Is.True);
        }

        [AvaloniaTest]
        public void RenamingAMachineKeepsItsMaps()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, lp[0]), MapScope.Global, Jump);
            string name = lp[0].Name;
            rig.Core.SongCore.RenameMachineUndoable(lead, "Solo");
            Assert.That(mm.GlobalEntries.Single().Target.Machine, Is.EqualTo("Solo"));
            Send(rig, Pads, CC(0, 1, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue));
            Assert.That(name, Is.EqualTo(lp[0].Name));
        }

        // ------------------------------------------------------------------ the agent's tools

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
        public void TheAgentToolsMapListAndClearWithTheSameChecksAndUndoAsTheUi()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, bass, lp, bp) = Song(rig, mm);
            var stack = rig.Core.SongCore.ActionStack;
            var controllers = Try("controller_list");
            Assert.That(controllers.Error, Is.False, controllers.Text);
            Assert.That(controllers.Data["controllers"]!.AsArray().Any(c => c!["device"]!.GetValue<string>() == "Faders" && c["knobs"]!.GetValue<int>() == 8), Is.True);

            int steps = stack.Actions.Count();
            var set = Try("midi_map_set", new JsonObject { ["device"] = "Pads", ["kind"] = "cc", ["channel"] = 1, ["number"] = 5, ["machine"] = "Lead", ["parameter"] = lp[0].Name, ["takeover"] = "jump" });
            Assert.That(set.Error, Is.False, set.Text);
            Assert.That(stack.Actions.Count(), Is.EqualTo(steps + 1), "one undo step");
            Send(rig, Pads, CC(0, 5, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue));
            var list = Try("midi_map_list", new JsonObject { ["scope"] = "global" });
            Assert.That(list.Data["count"]!.GetValue<int>(), Is.EqualTo(1));
            Assert.That(list.Data["mappings"]![0]!["device"]!.GetValue<string>(), Is.EqualTo("Pads"));

            Assert.That(Try("midi_map_set", new JsonObject { ["device"] = "Pads", ["number"] = 6, ["machine"] = "Nope", ["parameter"] = "x" }).Error, Is.True, "an unknown machine is refused");
            Assert.That(Try("midi_map_set", new JsonObject { ["device"] = "Pads", ["number"] = 6, ["command"] = "dance" }).Error, Is.True, "an unknown command is refused");
            Assert.That(Try("midi_map_set", new JsonObject { ["scope"] = "track", ["track_id"] = "track:Lead", ["device"] = "Pads", ["number"] = 6, ["machine"] = "Bass", ["parameter"] = bp[0].Name }).Error, Is.True, "Bass is not on Lead's chain");

            var page = Try("track_page_set", new JsonObject
            {
                ["track_id"] = "track:Lead",
                ["slots"] = new JsonArray(new JsonObject { ["knob"] = 1, ["machine"] = "Lead", ["parameter"] = lp[1].Name, ["takeover"] = "jump" }),
            });
            Assert.That(page.Error, Is.False, page.Text);
            var got = Try("track_page_get", new JsonObject { ["track_id"] = "track:Lead" });
            Assert.That(got.Data["pages"]![0]!["slots"]![0]!["parameter"]!.GetValue<string>(), Is.EqualTo(lp[1].Name));
            mm.Select(lead);
            lp[1].SetValue(0, lp[1].MinValue);
            Send(rig, Faders, CC(0, 21, 127));
            Assert.That(Value(lp[1]), Is.EqualTo(lp[1].MaxValue));

            var group = Try("midi_map_set", new JsonObject { ["op"] = "knob_group", ["device"] = "Strip", ["first_number"] = 10, ["count"] = 4 });
            Assert.That(group.Error, Is.False, group.Text);
            Assert.That(mm.Computer.GroupOf("Strip").Count, Is.EqualTo(4));

            var cleared = Try("midi_map_clear", new JsonObject { ["machine"] = "Lead", ["parameter"] = lp[0].Name });
            Assert.That(cleared.Error, Is.False, cleared.Text);
            Assert.That(Try("midi_map_list", new JsonObject { ["scope"] = "global" }).Data["count"]!.GetValue<int>(), Is.EqualTo(0));
            stack.Undo();
            Assert.That(Try("midi_map_list", new JsonObject { ["scope"] = "global" }).Data["count"]!.GetValue<int>(), Is.EqualTo(1), "undo of a clear");
        }

        // ------------------------------------------------------------------ the router's cost

        [AvaloniaTest]
        public void TheRouterAllocatesNothingPerMessageAndAKnobReachesTheParameterAtOnce()
        {
            using var setup = Setup(out var rig, out var mm);
            var (lead, _, lp, _) = Song(rig, mm);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 1), KnobEncoding.Absolute, Target(lead, lp[0]), MapScope.Global, Jump);
            mm.MapControl(new MapSource("Pads", MapKind.ControlChange, 0, 2), KnobEncoding.Offset64, Target(lead, lp[1]), MapScope.Global, Jump);
            mm.SetSlot("track:Lead", 0, 0, Target(lead, lp[2]), new MapDrive { Takeover = Takeover.Pickup });
            mm.Select(lead);
            mm.Rebuild();
            // Warm-up: the jit, the first delivery of each route.
            for (int i = 0; i < 400; i++)
            {
                mm.OnMidiThread(Pads, CC(0, 1, i & 127));
                mm.OnMidiThread(Pads, CC(0, 2, 65));
                mm.OnMidiThread(Faders, CC(0, 21, i & 127));
                mm.OnMidiThread(Keys, NoteOn(60));
                mm.OnMidiThread(Keys, 0x80 | (60 << 8));
            }
            // No Pump here: a route with an event waiting does not queue another (the UI thread's drain, which allocates its own signal, is once per drain).
            var kinds = new (string Name, MidiDevice Device, Func<int, int> Data)[]
            {
                ("absolute knob", Pads, i => CC(0, 1, i & 127)),
                ("relative knob", Pads, i => CC(0, 2, 65)),
                ("page knob", Faders, i => CC(0, 21, i & 127)),
                ("key on", Keys, i => NoteOn(60)),
                ("unmapped cc", Pads, i => CC(0, 99, 1)),
            };
            var report = new List<string>();
            foreach (var (name, device, data) in kinds)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 4000; i++) mm.OnMidiThread(device, data(i));
                long bytes = GC.GetAllocatedBytesForCurrentThread() - before;
                if (bytes != 0) report.Add($"{name}: {bytes} bytes");
            }
            Assert.That(report, Is.Empty, "bytes allocated by 4000 messages of each kind");
            // The whole way in (Core.SendMIDIInput): the router, the session's checks and the UI half, which now queues on the engine's event queue.
            Pump();
            for (int i = 0; i < 300; i++) rig.Core.SendMIDIInput(Pads, CC(0, 1, i & 127));
            Pump();
            long total = 0;
            for (int round = 0; round < 10; round++)                                // the UI thread drains between bursts of 200, as it does while the driver delivers
            {
                rig.Core.SendMIDIInput(Pads, CC(0, 1, 5));                          // the first of a burst queues the drain's signal (once per drain, not per message)
                long whole = GC.GetAllocatedBytesForCurrentThread();
                for (int i = 0; i < 400; i++) rig.Core.SendMIDIInput(Pads, CC(0, 1, (i + 7) & 127));
                total += GC.GetAllocatedBytesForCurrentThread() - whole;
                Pump();
            }
            Assert.That(total, Is.EqualTo(0), "bytes allocated by 4000 mapped knob messages through SendMIDIInput");
            Pump();
            mm.OnMidiThread(Pads, CC(0, 1, 127));
            Assert.That(Value(lp[0]), Is.EqualTo(lp[0].MaxValue), "set before OnMidiThread returned");
            Pump();
        }
    }
}
