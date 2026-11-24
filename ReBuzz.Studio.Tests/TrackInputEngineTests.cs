// Fork (docs/specs/neo-audio.md "Inputs and arm"): track inputs on the real engine. A fake input device (the engine's input ring, fed with
// sines and clicks on chosen channels) and fake MIDI devices (SendMIDIInput with an identity) stand in for the drivers.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Headless.NUnit;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Control;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using ReBuzz.Studio.Shell;
using ReBuzzTests.Automation;

namespace ReBuzz.Studio.Tests
{
    public class TrackInputEngineTests
    {
        const int Rate = 44100;

        /// <summary>The input device: opened with some channels, fed in step with the rendering.</summary>
        internal sealed class FakeInput
        {
            readonly ReBuzzCore core;
            long fed;

            public FakeInput(ReBuzzCore core, int channels, int latency = 0)
            {
                this.core = core;
                core.EngineInput.Configure(channels, null, Rate, latency);
            }

            public long Fed => fed;

            /// <summary>Frames of the device: <paramref name="signal"/> gives frame (from 0, since the device opened) and channel.</summary>
            public void Feed(int frames, Func<long, int, float> signal)
            {
                int ch = core.EngineInput.Channels;
                var data = new float[frames * ch];
                for (int i = 0; i < frames; i++)
                    for (int c = 0; c < ch; c++)
                        data[i * ch + c] = signal(fed + i, c);
                core.EngineInput.Push(data, frames, ch);
                fed += frames;
            }

            public float[] Run(SessionRig rig, int frames, Func<long, int, float> signal)
            {
                Feed(frames, signal);
                return rig.RunFrames(frames);
            }
        }

        static float Sine(long frame, double hz = 440, float amp = 0.5f) => amp * (float)Math.Sin(2 * Math.PI * hz * frame / Rate);

        static float Peak(float[] interleaved, int side, int skip = 2000) =>
            Enumerable.Range(skip, interleaved.Length / 2 - skip).Max(i => Math.Abs(interleaved[i * 2 + side]));

        internal static IDisposable Setup(out SessionRig rig)
        {
            var untitled = SnapshotTests.Untitled(out _);
            rig = new SessionRig();
            var r = rig;
            var core = r.Core;
            var controllersWere = core.SessionControllers;      // the shared core keeps its controllers for the tests after this one (the Session menu test lists them)
            core.MasterVolume = 1;                          // levels as the machines make them (other tests leave it lower; the rig puts it back)
            core.SafeMaster.LimiterOn = true;               // a test of the Safety page before may have left a ceiling below 0 dB
            core.SafeMaster.CeilingDb = 0;
            core.SafeMaster.Reset();                        // the master's limiter keeps no gain reduction from a test before
            return new Cleanup(() =>
            {
                r.Dispose();
                core.EngineInput.Close();
                core.OutputLatencySamples = 0;
                core.SessionControllers = controllersWere;
                core.SafeMaster.Reset();
                untitled.Dispose();
            });
        }

        sealed class Cleanup : IDisposable
        {
            readonly Action action;
            public Cleanup(Action action) => this.action = action;
            public void Dispose() => action();
        }

        internal static MachineCore NewTrack(SessionRig rig, string name, AudioFrom? from = null)
        {
            rig.Layout.Ensure();
            var m = (MachineCore)rig.Layout.NewAudioTrack(name)!;
            if (from != null) rig.Layout.SetAudioFrom(m, from.Value);
            return m;
        }

        // ------------------------------------------------------------------ audio from

        [AvaloniaTest]
        public void ANewAudioTrackStartsOnTheDevicesFirstInputsOrOnNothing()
        {
            using var setup = Setup(out var rig);
            Assert.That(NewTrack(rig, "A").Name, Is.EqualTo("A"));
            Assert.That(rig.Layout.TrackInputOf(rig.Machine("A")).Audio, Is.EqualTo(AudioFrom.None), "no input device is open");
            new FakeInput(rig.Core, 4);
            var b = NewTrack(rig, "B");
            Assert.That(rig.Layout.TrackInputOf(b).Audio, Is.EqualTo(AudioFrom.Stereo(0)), "In 1/2");
            new FakeInput(rig.Core, 1);
            var c = NewTrack(rig, "C");
            Assert.That(rig.Layout.TrackInputOf(c).Audio, Is.EqualTo(AudioFrom.Mono(0)));
        }

        [AvaloniaTest]
        public void AMonoInputIsCentredAndAStereoPairKeepsItsSides()
        {
            using var setup = Setup(out var rig);
            var device = new FakeInput(rig.Core, 4);
            var a = NewTrack(rig, "A", AudioFrom.Mono(2));
            rig.Layout.SetMonitor(a, 1);                                        // in
            var heard = device.Run(rig, 8000, (f, c) => c == 2 ? Sine(f) : c == 0 ? 0.9f : 0);
            Assert.That(Peak(heard, 0), Is.EqualTo(0.5f).Within(0.03f), "In 3, to the left");
            Assert.That(Peak(heard, 1), Is.EqualTo(0.5f).Within(0.03f), "and the right");

            rig.Layout.SetAudioFrom(a, AudioFrom.Stereo(0));
            heard = device.Run(rig, 8000, (f, c) => c == 0 ? Sine(f, 440, 0.6f) : c == 1 ? Sine(f, 660, 0.2f) : 0);
            Assert.That(Peak(heard, 0), Is.EqualTo(0.6f).Within(0.03f));
            Assert.That(Peak(heard, 1), Is.EqualTo(0.2f).Within(0.03f));

            rig.Layout.SetAudioFrom(a, AudioFrom.Stereo(2));
            heard = device.Run(rig, 8000, (f, c) => c == 2 ? Sine(f) : 0.7f);
            Assert.That(Peak(heard, 0), Is.EqualTo(0.5f).Within(0.03f), "In 3/4: left from 3");
            Assert.That(Peak(heard, 1), Is.EqualTo(0.7f).Within(0.03f), "right from 4");

            rig.Layout.SetAudioFrom(a, AudioFrom.None);
            heard = device.Run(rig, 8000, (f, c) => 0.5f);
            Assert.That(Peak(heard, 0), Is.EqualTo(0f), "None: silence");
        }

        [AvaloniaTest]
        public void MonitorHearsInAutoWhileArmedAndOffNever()
        {
            using var setup = Setup(out var rig);
            var device = new FakeInput(rig.Core, 2);
            var a = NewTrack(rig, "A");                                         // In 1/2
            Assert.That(rig.Layout.MonitorOf(a), Is.EqualTo(0), "auto");
            float Hear() => Peak(device.Run(rig, 6000, (f, c) => Sine(f)), 0);

            Assert.That(Hear(), Is.EqualTo(0f), "auto, not armed");
            Assert.That(rig.Layout.SetMachineArmed(a, true), Is.True);
            Assert.That(rig.Layout.IsMachineArmed(a), Is.True);
            Assert.That(Hear(), Is.GreaterThan(0.4f), "auto, armed");
            rig.Layout.SetMonitor(a, 2);
            Assert.That(Hear(), Is.EqualTo(0f), "off, armed");
            rig.Layout.SetMachineArmed(a, false);
            rig.Layout.SetMonitor(a, 1);
            Assert.That(Hear(), Is.GreaterThan(0.4f), "in, not armed");
        }

        [AvaloniaTest]
        public void TheInputMeterReadsTheInputWhileArmedOrMonitoringAndHoldsClipping()
        {
            using var setup = Setup(out var rig);
            var device = new FakeInput(rig.Core, 2);
            var a = NewTrack(rig, "A");
            device.Run(rig, 6000, (f, c) => Sine(f));
            Assert.That(rig.Layout.InputMeterOf(a)!.Level, Is.EqualTo(0), "not armed, not monitored: nothing");
            rig.Layout.SetMachineArmed(a, true);
            device.Run(rig, 6000, (f, c) => Sine(f));
            System.Threading.Thread.Sleep(20);
            var meter = rig.Layout.InputMeterOf(a)!;
            Assert.That(meter.Level, Is.GreaterThan(0.8), "-6 dB is high on a -60 to 0 meter");
            Assert.That(meter.Clipped, Is.False);
            device.Run(rig, 6000, (f, c) => 1.0f);
            System.Threading.Thread.Sleep(20);
            meter = rig.Layout.InputMeterOf(a)!;
            Assert.That(meter.Clipped, Is.True, "full scale lights the clip LED");
            device.Run(rig, 6000, (f, c) => 0f);
            System.Threading.Thread.Sleep(20);
            Assert.That(rig.Layout.InputMeterOf(a)!.Clipped, Is.True, "and it stays");
        }

        [AvaloniaTest]
        public void ATrackCanListenToAnotherTracksOutputToResampleIt()
        {
            using var setup = Setup(out var rig);
            var device = new FakeInput(rig.Core, 2);
            var a = NewTrack(rig, "Source");
            rig.Layout.SetMonitor(a, 1);
            var b = NewTrack(rig, "Resampler", AudioFrom.Track("Source"));
            rig.Layout.SetMonitor(b, 1);
            Assert.That(rig.Layout.AudioChoices(b).Select(f => f.ToText()), Does.Contain("track:Source"));
            Assert.That(rig.Layout.AudioChoices(b).Select(f => f.ToText()), Does.Not.Contain("track:Resampler"), "not itself");

            device.Run(rig, 12000, (f, c) => Sine(f));
            SessionClipEngine.PlayerOf(b)!.TakePeak();
            device.Run(rig, 12000, (f, c) => Sine(f));
            var (l, _) = SessionClipEngine.PlayerOf(b)!.TakePeak();
            Assert.That(l, Is.GreaterThan(0.3f * 32768), "the resampler hears the source after its mixer");

            rig.Layout.SetMonitor(a, 2);                                        // the source falls silent
            device.Run(rig, 4000, (f, c) => Sine(f));
            SessionClipEngine.PlayerOf(b)!.TakePeak();
            device.Run(rig, 8000, (f, c) => Sine(f));
            Assert.That(SessionClipEngine.PlayerOf(b)!.TakePeak().Left, Is.LessThan(0.01f * 32768));
        }

        [AvaloniaTest]
        public void ATakeFromIn1LinesUpWithWhatWasPlayedWithinAMillisecondAfterTheRoundTrip()
        {
            using var setup = Setup(out var rig);
            var core = rig.Core;
            const int outLatency = 300, inLatency = 100, round = outLatency + inLatency, spacing = 4410;
            core.OutputLatencySamples = outLatency;
            var device = new FakeInput(core, 2, inLatency);
            var a = NewTrack(rig, "Mic", AudioFrom.Mono(0));
            core.BPM = 120;
            rig.Layout.SetRecordBars(1);
            rig.Layout.Document.CountInBars = 0;
            rig.Layout.SetMachineArmed(a, true);
            int track = rig.Layout.FirstTrackOf(a);
            var take = rig.Layout.RecordTake(track, 0)!;
            Assert.That(take, Is.Not.Null);
            core.Playing = true;

            // What is played at frame t comes back on In 1 at t + round trip: a click every spacing frames.
            float Click(long f, int c) => c == 0 && f >= round && (f - round) % spacing == 0 ? 0.9f : 0f;
            long start;
            {
                var cap = ((PatternCore)take).SessionAudio.Capture;
                var f = typeof(ReBuzz.Core.Clips.AudioCapture).GetField("started", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                int r = 0;
                while (!(bool)f.GetValue(cap) && r < 20000) { device.Run(rig, 1, Click); r++; }
                start = r;
            }
            for (int i = 0; i < 40 && (i < 4 || rig.Layout.IsRecordingClip(take)); i++)
                { device.Run(rig, 4410, Click); rig.Layout.PollTakes(); }
            Assert.That(rig.Layout.IsRecordingClip(take), Is.False, "the take is done");

            var source = rig.Layout.AudioSourceOf(take)!;
            var left = source.Left.ToArray();
            var clicks = Enumerable.Range(0, left.Length).Where(i => Math.Abs(left[i]) > 0.4f).ToList();
            Assert.That(clicks.Count, Is.GreaterThanOrEqualTo(4), "the clicks were recorded");
            int onGrid = 0;
            foreach (int at in clicks)
            {
                int off = (int)((((at + start) + spacing / 2) % spacing) - spacing / 2);
                if (Math.Abs(off) <= Rate / 1000) onGrid++;
            }
            Assert.That(onGrid, Is.EqualTo(clicks.Count), "every click within 1 ms of where it was played (take began at render frame " + start + "): " + string.Join(", ", clicks.Take(8)));
        }

        // ------------------------------------------------------------------ midi from

        static int NoteOn(int note, int channel = 0) => 0x90 | channel | (note << 8) | (100 << 16);
        static int NoteOff(int note, int channel = 0) => 0x80 | channel | (note << 8);

        static IPattern TakeOf(SessionRig rig, MachineCore machine, int scene)
        {
            int track = rig.Layout.FirstTrackOf(machine);
            var take = rig.Layout.RecordTake(track, scene)!;
            Assert.That(take, Is.Not.Null);
            return take;
        }

        static int[] NotesIn(SessionRig rig, IPattern take) => rig.Layout.NotesOf(take).Select(n => n.Note).OrderBy(n => n).ToArray();

        [AvaloniaTest]
        public void TwoDevicesPlayTwoArmedTracksAndTheSameSourcePlaysBoth()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            var bass = rig.Add("Neo Poly", "Bass");
            var pads = new MidiDevice(0, "Pads");
            var keys = new MidiDevice(1, "Keys");
            Assert.That(layout.SetMidiFrom(lead, MidiFromKind.Device, "Keys"), Is.True);
            Assert.That(layout.SetMidiFrom(bass, MidiFromKind.Device, "Pads"), Is.True);
            layout.SetRecordBars(2);
            layout.SetMachineArmed(lead, true);
            layout.SetMachineArmed(bass, true);
            var t1 = TakeOf(rig, lead, 0);
            var t2 = TakeOf(rig, bass, 0);
            rig.Core.Playing = true;
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(keys, NoteOn(60));
            rig.Core.SendMIDIInput(pads, NoteOn(40));
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(keys, NoteOff(60));
            rig.Core.SendMIDIInput(pads, NoteOff(40));
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(MidiDevice.Unknown, NoteOn(72));                 // a device nobody listens to
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(MidiDevice.Unknown, NoteOff(72));
            rig.PlayTo(SessionRig.Bar * 2 + 2);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(lead))!);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(bass))!);
            Assert.That(NotesIn(rig, t1), Is.EqualTo(new[] { 60 }), "Lead hears Keys only");
            Assert.That(NotesIn(rig, t2), Is.EqualTo(new[] { 40 }), "Bass hears Pads only");
        }

        // ------------------------------------------------------------------ Neo Voice to MIDI (docs/specs/voice-to-midi.md §7)

        /// <summary>Plays a sung A3 (220 Hz, a harmonic tone) into Neo Voice to MIDI's Work, a block at a time, as the host would.</summary>
        static void Sing(MachineCore voice, double seconds, ref long frame)
        {
            var instance = voice.ManagedMachine!;
            var work = instance.GetType().GetMethod("Work")!;
            var input = new Buzz.MachineInterface.Sample[256];
            var output = new Buzz.MachineInterface.Sample[256];
            int blocks = (int)(seconds * Rate / 256);
            for (int b = 0; b < blocks; b++)
            {
                for (int i = 0; i < 256; i++, frame++)
                {
                    double t = frame / (double)Rate, v = 0;
                    for (int h = 1; h <= 8; h++) v += Math.Sin(2 * Math.PI * 220 * h * t) / h;
                    input[i].L = input[i].R = (float)(v * 0.25 * 32768);
                }
                work.Invoke(instance, new object[] { output, input, 256, Buzz.MachineInterface.WorkModes.WM_READWRITE });
            }
        }

        [Test]
        public void AMachineSourceIsShortInTheClosedChooserAndFullInTheList()
        {
            // PL1: "Machine: Voice MIDI" is cut in the narrow Session I/O strip; the closed box shows the machine's name with a glyph, the list and the tooltip the full label.
            Assert.That(Views.Session.TrackInputView.ShortMidiLabel("Machine: Voice MIDI"), Is.EqualTo("\u25A3 Voice MIDI"));
            Assert.That(Views.Session.TrackInputView.ShortMidiLabel("Machine: Voice MIDI (not in the song)"), Is.EqualTo("\u25A3 Voice MIDI (not in the song)"));
            Assert.That(Views.Session.TrackInputView.ShortMidiLabel("All inputs"), Is.EqualTo("All inputs"));
            Assert.That(Views.Session.TrackInputView.ShortMidiLabel("Computer keyboard"), Is.EqualTo("Computer keyboard"));
            Assert.That(Views.Session.TrackInputView.ShortMidiLabel(null), Is.EqualTo(""));
        }

        [AvaloniaTest]
        public void AnArmedTrackRecordsTheNotesOfAMachineNamedInItsMidiFrom()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var voice = rig.Add("Neo Voice to MIDI", "Voice MIDI", track: false);
            var lead = rig.Add("Neo Poly", "Lead");
            var bass = rig.Add("Neo Poly", "Bass");
            Assert.That(layout.SetMidiFrom(lead, MidiFromKind.Machine, "Voice MIDI"), Is.True);
            Assert.That(layout.MidiLabel(lead), Is.EqualTo("Machine: Voice MIDI"));
            layout.SetRecordBars(2);
            layout.SetMachineArmed(lead, true);
            layout.SetMachineArmed(bass, true);                                     // All inputs: a machine's MIDI is not for it
            var t1 = TakeOf(rig, lead, 0);
            var t2 = TakeOf(rig, bass, 0);
            rig.Core.Playing = true;
            rig.RunTicks(2);
            long frame = 0;
            Sing(voice, 0.25, ref frame);
            rig.RunTicks(2);
            Sing(voice, 0.25, ref frame);
            rig.RunTicks(4);
            rig.PlayTo(SessionRig.Bar * 2 + 2);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(lead))!);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(bass))!);
            Assert.That(NotesIn(rig, t1), Does.Contain(57), "a sung A3 is note 57");
            Assert.That(NotesIn(rig, t2), Is.Empty, "All inputs take devices, not a machine");
        }

        [AvaloniaTest]
        public void NotesGoToATargetMachineWithinTheNextBlock()
        {
            using var setup = Setup(out var rig);
            rig.Layout.Ensure();
            var voice = rig.Add("Neo Voice to MIDI", "Voice MIDI", track: false);
            var lead = rig.Add("Neo Poly", "Lead");
            voice.ManagedMachine!.GetType().GetMethod("SetTarget")!.Invoke(voice.ManagedMachine, new object[] { "Lead" });
            Assert.That((bool)voice.ManagedMachine.GetType().GetProperty("TargetFound")!.GetValue(voice.ManagedMachine)!, Is.True, "the target is found by name");
            var before = rig.RunFrames(4096);
            Assert.That(before.Max(Math.Abs), Is.LessThan(0.001f), "silent before the voice");
            long frame = 0;
            Sing(voice, 0.2, ref frame);
            var heard = rig.RunFrames(Rate / 4);
            Assert.That(heard.Max(Math.Abs), Is.GreaterThan(0.005f), "Neo Poly plays the sung note");
        }

        sealed class FakePort : IControllerPort
        {
            public event Action<int> Received;
            public void Send(int message) { }
            public void Send(byte[] message) { }
            public void Press(int message) => Received?.Invoke(message);
            public void Dispose() { }
        }

        [AvaloniaTest]
        public void AllInputsLeaveOutAControlSurfaceAndTwoTracksOnTheSameSourceBothPlay()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            var bass = rig.Add("Neo Poly", "Bass");
            using var controllers = new SessionControllers(rig.Core, null, loadScripts: false, autoRefresh: false);
            controllers.Attach(new Apc40Mk2Script(), new FakePort(), "test", "Pads");
            rig.Core.SessionControllers = controllers;
            var pads = new MidiDevice(0, "Pads");
            var keys = new MidiDevice(1, "Keys");
            layout.SetRecordBars(2);
            layout.SetMachineArmed(lead, true);
            layout.SetMachineArmed(bass, true);                                 // both: All inputs
            var t1 = TakeOf(rig, lead, 0);
            var t2 = TakeOf(rig, bass, 0);
            rig.Core.Playing = true;
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(keys, NoteOn(60));
            rig.Core.SendMIDIInput(pads, NoteOn(36));                           // the surface's pad: runs the session, plays nothing
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(keys, NoteOff(60));
            rig.Core.SendMIDIInput(pads, NoteOff(36));
            rig.PlayTo(SessionRig.Bar * 2 + 2);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(lead))!);
            layout.StopRecording(layout.SequenceOf(layout.FirstTrackOf(bass))!);
            Assert.That(NotesIn(rig, t1), Is.EqualTo(new[] { 60 }), "layered: both hear Keys");
            Assert.That(NotesIn(rig, t2), Is.EqualTo(new[] { 60 }));
        }

        [AvaloniaTest]
        public void ADeviceThatIsNotConnectedShowsSoAndPlaysAgainWhenItComesBack()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            layout.SetMidiFrom(lead, MidiFromKind.Device, "Keys");
            Assert.That(layout.MidiLabel(lead), Is.EqualTo("Keys (not connected)"));
            Assert.That(layout.MidiDevices(lead).Select(d => (d.Name, d.Connected)), Does.Contain(("Keys", false)), "the saved name stays on the list");
            layout.SetRecordBars(1);
            layout.SetMachineArmed(lead, true);
            var take = TakeOf(rig, lead, 0);
            rig.Core.Playing = true;
            rig.RunTicks(1);
            rig.Core.SendMIDIInput(new MidiDevice(0, "Pads"), NoteOn(50));
            rig.Core.SendMIDIInput(new MidiDevice(3, "keys"), NoteOn(62));      // plugged in, at another index
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(new MidiDevice(3, "keys"), NoteOff(62));
            rig.Core.SendMIDIInput(new MidiDevice(0, "Pads"), NoteOff(50));
            rig.PlayTo(SessionRig.Bar + 2);
            Assert.That(NotesIn(rig, take), Is.EqualTo(new[] { 62 }), "the name is the identity");
        }

        [AvaloniaTest]
        public void AChannelFiltersAndUnarmedTracksLeaveMidiToTheSelectedMachine()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            layout.SetMidiChannel(lead, 3);
            layout.SetRecordBars(1);
            layout.SetMachineArmed(lead, true);
            var take = TakeOf(rig, lead, 0);
            rig.Core.Playing = true;
            rig.RunTicks(1);
            rig.Core.SendMIDIInput(new MidiDevice(0, "Keys"), NoteOn(60, 0));
            rig.Core.SendMIDIInput(new MidiDevice(0, "Keys"), NoteOn(64, 2));
            rig.RunTicks(2);
            rig.Core.SendMIDIInput(new MidiDevice(0, "Keys"), NoteOff(60, 0));
            rig.Core.SendMIDIInput(new MidiDevice(0, "Keys"), NoteOff(64, 2));
            rig.PlayTo(SessionRig.Bar + 2);
            Assert.That(NotesIn(rig, take), Is.EqualTo(new[] { 64 }), "channel 3 only");
        }

        [AvaloniaTest]
        public void AnArmedMicLeavesTheKeyboardToTheSelectedMachine()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            new FakeInput(rig.Core, 2);
            var mic = NewTrack(rig, "Mic");
            layout.SetMachineArmed(mic, true);
            var keys = new MidiDevice(0, "Keys");
            var sc = rig.Core.SessionCore;
            Assert.That(sc.OnMidiInput(keys, NoteOn(60)), Is.False, "only an audio track is armed: the selected machine plays");
            Assert.That(sc.OnMidiInput(keys, NoteOff(60)), Is.False, "and its release goes the same way");

            Assert.That(layout.SetMidiFrom(mic, MidiFromKind.Device, "Keys"), Is.False, "an audio track takes no MIDI source");
            Assert.That(layout.SetMidiChannel(mic, 3), Is.False);

            var lead = rig.Add("Neo Poly", "Lead");                                   // with a generator armed too, it takes the keyboard
            layout.SetMachineArmed(lead, true);
            Assert.That(sc.OnMidiInput(keys, NoteOn(62)), Is.True);
            Assert.That(sc.OnMidiInput(keys, NoteOff(62)), Is.True);
        }

        [AvaloniaTest]
        public void ADeviceNoArmedTrackListensToFallsBackToTheSelectedMachine()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            layout.Ensure();
            var lead = rig.Add("Neo Poly", "Lead");
            Assert.That(layout.SetMidiFrom(lead, MidiFromKind.Device, "Keys"), Is.True);
            layout.SetMachineArmed(lead, true);
            var sc = rig.Core.SessionCore;
            var keys = new MidiDevice(1, "Keys");
            var pads = new MidiDevice(0, "Pads");
            Assert.That(sc.OnMidiInput(pads, NoteOn(40)), Is.False, "a pad controller is not dead because a keyboard track is armed");
            Assert.That(sc.OnMidiInput(keys, NoteOn(60)), Is.True, "the keyboard plays the armed track");
            Assert.That(sc.OnMidiInput(pads, NoteOff(40)), Is.False, "the pad's release goes where its press went");
            Assert.That(sc.OnMidiInput(keys, NoteOff(60)), Is.True);
        }

        // ------------------------------------------------------------------ saved with the song

        static void Reopen(ReBuzzCore core, string path)
        {
            core.Modified = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            Assert.That(core.TryOpenSongFile(path), Is.True, "opens " + Path.GetFileName(path));
            Dialogs.Responder = null;
        }

        static string Work(string name)
        {
            string dir = TestTemp.Path($"track-inputs-{Environment.ProcessId}");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, name);
        }

        [AvaloniaTest]
        public void InputsAreSavedWithTheSongAndArmIsNot()
        {
            using var setup = Setup(out var rig);
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var layout = rig.Layout;
            new FakeInput(core, 4);
            var a = NewTrack(rig, "Mic", AudioFrom.Mono(1));
            var lead = rig.Add("Neo Poly", "Lead");
            layout.SetMidiFrom(lead, MidiFromKind.Device, "Keys");
            layout.SetMidiChannel(lead, 5);
            layout.SetMonitor(a, 1);
            layout.SetMachineArmed(a, true);
            layout.SetMachineArmed(lead, true);
            foreach (var ext in new[] { ".nbl" })
            {
                string path = Work("inputs" + ext);
                core.SaveSongCopy(path);
                Reopen(core, path);
                var layout2 = core.SessionLayout;
                var mic = core.SongCore.MachinesList.First(m => m.Name == "Mic");
                var poly = core.SongCore.MachinesList.First(m => m.Name == "Lead");
                Assert.That(layout2.TrackInputOf(mic).Audio, Is.EqualTo(AudioFrom.Mono(1)), ext);
                Assert.That(layout2.MonitorOf(mic), Is.EqualTo(1), ext + ": the monitor is a parameter of the machine, saved with it");
                var input = layout2.TrackInputOf(poly);
                Assert.That((input.Midi, input.Device, input.Channel), Is.EqualTo((MidiFromKind.Device, "Keys", 5)), ext);
                Assert.That(layout2.IsMachineArmed(mic), Is.False, ext + ": a reopened song starts disarmed");
                Assert.That(layout2.IsMachineArmed(poly), Is.False);
                Assert.That(rig.Session.ArmedTracks.Count, Is.EqualTo(0));
                // arm again for the next round
                layout2.SetMachineArmed(mic, true);
            }
        }

        [AvaloniaTest]
        public void ASongSavedBeforeLoadsAsWiredInputsAndAllMidiInputs()
        {
            using var setup = Setup(out var rig);
            var core = rig.Core;
            using var restore = Studio.AfterTest(() => { Dialogs.Responder = null; core.Modified = false; });
            var a = NewTrack(rig, "Loops", AudioFrom.Wired);                    // what an old song's track is
            Assert.That(rig.Layout.Document.Inputs, Is.Empty, "nothing is written for the defaults");
            string path = Work("old.nbl");
            core.SaveSongCopy(path);
            Assert.That(System.IO.File.ReadAllText(path), Does.Not.Contain("<Inputs"), "as an older build wrote it");
            Reopen(core, path);
            var m = core.SongCore.MachinesList.First(x => x.Name == "Loops");
            var input = core.SessionLayout.TrackInputOf(m);
            Assert.That(input.Audio, Is.EqualTo(AudioFrom.Wired));
            Assert.That(input.Midi, Is.EqualTo(MidiFromKind.All));
        }

        [AvaloniaTest]
        public void EachChangeOfInputIsOneUndoStepAndArmIsNone()
        {
            using var setup = Setup(out var rig);
            var layout = rig.Layout;
            new FakeInput(rig.Core, 2);
            var a = NewTrack(rig, "Mic");
            var stack = layout.UndoStack;
            layout.SetAudioFrom(a, AudioFrom.Mono(1));
            layout.SetMonitor(a, 2);
            layout.SetMachineArmed(a, true);
            Assert.That(layout.TrackInputOf(a).Audio, Is.EqualTo(AudioFrom.Mono(1)));
            stack.Undo();                                                       // arm made no step: this is the monitor
            Assert.That(layout.MonitorOf(a), Is.EqualTo(0));
            Assert.That(layout.IsMachineArmed(a), Is.True, "arm stays as it was");
            stack.Undo();
            Assert.That(layout.TrackInputOf(a).Audio, Is.EqualTo(AudioFrom.Stereo(0)), "back to the new track's default");
            stack.Redo();
            Assert.That(layout.TrackInputOf(a).Audio, Is.EqualTo(AudioFrom.Mono(1)));
        }

        // ------------------------------------------------------------------ the agent

        static System.Text.Json.Nodes.JsonObject Tool(string tool, System.Text.Json.Nodes.JsonObject args = null)
        {
            var message = new System.Text.Json.Nodes.JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new System.Text.Json.Nodes.JsonObject { ["name"] = tool, ["arguments"] = args ?? new System.Text.Json.Nodes.JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new ReBuzz.Agent.Mcp.McpSession(), _ => System.Threading.Tasks.Task.CompletedTask, System.Threading.CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                System.Threading.Thread.Sleep(2);
            }
            return task.GetAwaiter().GetResult()["result"]!.AsObject();
        }

        [AvaloniaTest]
        public void TheAgentReadsAndSetsInputsAndArmsAudioTracks()
        {
            using var setup = Setup(out var rig);
            new FakeInput(rig.Core, 4);
            var layout = rig.Layout;
            var voice = NewTrack(rig, "Voice");
            var lead = rig.Add("Neo Poly", "Lead");
            var got = Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "get" });
            Assert.That(got["isError"]?.GetValue<bool>() ?? false, Is.False, got.ToJsonString());
            var s = got["structuredContent"]!.AsObject();
            Assert.That(s["audio_inputs"]!["channels"]!.AsArray().Count, Is.EqualTo(4));
            var tracks = s["tracks"]!.AsArray();
            Assert.That(tracks.Select(t => (string)t!["machine"]), Is.SupersetOf(new[] { "Voice", "Lead" }));
            Assert.That((string)tracks.First(t => (string)t!["machine"] == "Voice")!["audio_from"], Is.EqualTo("in:1-2"), "the new track's default");

            var set = Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "set", ["machine"] = "Voice", ["audio_from"] = "in:3", ["monitor"] = "in" });
            Assert.That(set["isError"]?.GetValue<bool>() ?? false, Is.False, set.ToJsonString());
            Assert.That(layout.TrackInputOf(voice).Audio, Is.EqualTo(AudioFrom.Mono(2)));
            Assert.That(layout.MonitorOf(voice), Is.EqualTo(1));
            var lset = Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "set", ["machine"] = "Lead", ["midi_from"] = "device:Pads", ["channel"] = 2 });
            Assert.That(lset["isError"]?.GetValue<bool>() ?? false, Is.False, lset.ToJsonString());
            var input = layout.TrackInputOf(lead);
            Assert.That((input.Midi, input.Device, input.Channel), Is.EqualTo((MidiFromKind.Device, "Pads", 2)));

            Assert.That(Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "set", ["machine"] = "Voice", ["audio_from"] = "in:9" })["isError"]?.GetValue<bool>(), Is.True, "the device has 4 inputs");
            Assert.That(Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "set", ["machine"] = "Lead", ["audio_from"] = "in:1" })["isError"]?.GetValue<bool>(), Is.True, "a generator takes no audio input");
            Assert.That(Tool("track_input", new System.Text.Json.Nodes.JsonObject { ["action"] = "set", ["machine"] = "Voice", ["audio_from"] = "track:Voice" })["isError"]?.GetValue<bool>(), Is.True, "not itself");

            // Arm for an audio track too.
            var arm = Tool("session_record", new System.Text.Json.Nodes.JsonObject { ["action"] = "arm", ["machine"] = "Voice" });
            Assert.That(arm["isError"]?.GetValue<bool>() ?? false, Is.False, arm.ToJsonString());
            Assert.That(layout.IsMachineArmed(voice), Is.True);
            Tool("session_record", new System.Text.Json.Nodes.JsonObject { ["action"] = "disarm", ["machine"] = "Voice" });
            Assert.That(layout.IsMachineArmed(voice), Is.False);
            var master = Tool("session_record", new System.Text.Json.Nodes.JsonObject { ["action"] = "arm", ["machine"] = "Master" });
            Assert.That(master["isError"]?.GetValue<bool>(), Is.True);

        }
    }
}
