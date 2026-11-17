using ReBuzz.Audio;
using ReBuzz.Core.Session;
using ReBuzz.Midi;
using System.Text;
using static ReBuzz.Core.Session.SessionDocument;

namespace ReBuzz.SessionTests
{
    /// <summary>Track inputs' pure parts (docs/specs/neo-audio.md "Inputs and arm"): Audio From text, MIDI routing, the saved form, the meter and the input ring.</summary>
    [TestFixture]
    public class TrackInputTests
    {
        // ------------------------------------------------------------------ Audio From

        [TestCase("wired")]
        [TestCase("none")]
        [TestCase("in:3")]
        [TestCase("in:1-2")]
        [TestCase("track:Bass 2")]
        public void AudioFromTextRoundTrips(string text)
        {
            Assert.That(AudioFrom.TryParse(text, out var from), Is.True);
            Assert.That(from.ToText(), Is.EqualTo(text));
        }

        [Test]
        public void AudioFromCountsInputsFromOneInTextAndFromZeroInside()
        {
            Assert.That(AudioFrom.TryParse("in:3", out var mono), Is.True);
            Assert.That(mono.Kind, Is.EqualTo(AudioFromKind.Mono));
            Assert.That(mono.Channel, Is.EqualTo(2));
            Assert.That(AudioFrom.TryParse("In 1/2", out var pair), Is.True);
            Assert.That(pair, Is.EqualTo(AudioFrom.Stereo(0)));
            Assert.That(pair.ChannelsNeeded, Is.EqualTo(2));
            Assert.That(AudioFrom.TryParse("in:2-5", out _), Is.False, "a pair is neighbours");
            Assert.That(AudioFrom.TryParse("in:0", out _), Is.False);
            Assert.That(AudioFrom.TryParse("", out _), Is.False);
        }

        [Test]
        public void AudioFromLabelsUseDriverNamesWhenThereAre()
        {
            Assert.That(AudioFrom.Mono(1).Label(), Is.EqualTo("In 2"));
            Assert.That(AudioFrom.Stereo(0).Label(), Is.EqualTo("In 1/2"));
            Assert.That(AudioFrom.Mono(0).Label(new[] { "Mic", "Line" }), Is.EqualTo("Mic"));
            Assert.That(AudioFrom.Stereo(0).Label(new[] { "Mic", "Line" }), Is.EqualTo("Mic / Line"));
            Assert.That(AudioFrom.Wired.Label(), Is.EqualTo("Wired inputs"));
            Assert.That(AudioFrom.None.Label(), Is.EqualTo("None"));
            Assert.That(AudioFrom.Track("Drums").Label(), Is.EqualTo("Track: Drums"));
        }

        // ------------------------------------------------------------------ MIDI routing

        static readonly MidiDevice Pads = new MidiDevice(0, "Pads");
        static readonly MidiDevice Keys = new MidiDevice(1, "Keys");

        [Test]
        public void AllInputsTakeEveryDeviceExceptControlSurfaces()
        {
            var all = new TrackInput();
            Assert.That(MidiRouting.Accepts(all, Pads, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(all, Keys, 5, false), Is.True);
            Assert.That(MidiRouting.Accepts(all, MidiDevice.Unknown, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(all, MidiDevice.Keyboard, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(all, Pads, 0, true), Is.False, "a claimed control surface");
        }

        [Test]
        public void ADeviceTrackTakesOnlyThatDeviceByName()
        {
            var t = new TrackInput { Midi = MidiFromKind.Device, Device = "keys" };
            Assert.That(MidiRouting.Accepts(t, Keys, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(t, new MidiDevice(7, "Keys"), 0, false), Is.True, "the name is the identity, not the index");
            Assert.That(MidiRouting.Accepts(t, Pads, 0, false), Is.False);
            Assert.That(MidiRouting.Accepts(t, MidiDevice.Keyboard, 0, false), Is.False);
            Assert.That(MidiRouting.Accepts(t, MidiDevice.Unknown, 0, false), Is.False);
            Assert.That(MidiRouting.AcceptsKeyboard(t), Is.False);
        }

        [Test]
        public void TheKeyboardTrackTakesOnlyTheKeyboardAndAChannelFilters()
        {
            var k = new TrackInput { Midi = MidiFromKind.Keyboard };
            Assert.That(MidiRouting.Accepts(k, MidiDevice.Keyboard, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(k, Pads, 0, false), Is.False);
            Assert.That(MidiRouting.AcceptsKeyboard(k), Is.True);

            var ch = new TrackInput { Channel = 3 };
            Assert.That(MidiRouting.Accepts(ch, Pads, 2, false), Is.True, "channel 3 is 2 in the message");
            Assert.That(MidiRouting.Accepts(ch, Pads, 3, false), Is.False);
        }

        // ------------------------------------------------------------------ MIDI From a machine (Neo Voice to MIDI)

        [Test]
        public void AMachineTrackTakesOnlyThatMachineByNameAndAChannelFilters()
        {
            var voice = MidiDevice.Machine("Voice 1");
            var t = new TrackInput { Midi = MidiFromKind.Machine, Device = "voice 1" };
            Assert.That(voice.IsMachine, Is.True);
            Assert.That(voice.IsDevice, Is.False, "a machine is not a MIDI device");
            Assert.That(MidiRouting.Accepts(t, voice, 0, false), Is.True);
            Assert.That(MidiRouting.Accepts(t, MidiDevice.Machine("Voice 2"), 0, false), Is.False);
            Assert.That(MidiRouting.Accepts(t, Keys, 0, false), Is.False, "a machine track hears no device");
            Assert.That(MidiRouting.Accepts(t, MidiDevice.Keyboard, 0, false), Is.False);
            Assert.That(MidiRouting.AcceptsKeyboard(t), Is.False);
            var ch = new TrackInput { Midi = MidiFromKind.Machine, Device = "Voice 1", Channel = 10 };
            Assert.That(MidiRouting.Accepts(ch, voice, 9, false), Is.True, "channel 10 is 9 in the message");
            Assert.That(MidiRouting.Accepts(ch, voice, 0, false), Is.False);
        }

        [Test]
        public void AMachinesMidiNeverReachesAllInputsOrATrackWithNoEntry()
        {
            var voice = MidiDevice.Machine("Voice 1");
            Assert.That(MidiRouting.Accepts(new TrackInput(), voice, 0, false), Is.False, "All inputs are for devices");
            Assert.That(MidiRouting.Accepts(null, voice, 0, false), Is.False);
            Assert.That(MidiRouting.Accepts(new TrackInput { Midi = MidiFromKind.Device, Device = "Voice 1" }, voice, 0, false), Is.False, "a device named like a machine is not the machine");
            Assert.That(MidiRouting.Accepts(new TrackInput { Midi = MidiFromKind.Keyboard }, voice, 0, false), Is.False);
        }

        [Test]
        public void AMachineSourceRoundTripsThroughTheDocument()
        {
            var d = new SessionDocument();
            d.SetInput("Lead", new TrackInput { Midi = MidiFromKind.Machine, Device = "Voice 1", Channel = 2 });
            var copy = Load(new MemoryStream(d.ToBytes()))!;
            var input = copy.GetInput("Lead");
            Assert.That(input.Midi, Is.EqualTo(MidiFromKind.Machine));
            Assert.That(input.Device, Is.EqualTo("Voice 1"));
            Assert.That(input.Channel, Is.EqualTo(2));
            Assert.That(input.SameAs(d.GetInput("Lead")), Is.True);
            Assert.That(input.MidiLabel, Is.EqualTo("Machine: Voice 1"));
            Assert.That(new TrackInput { Midi = MidiFromKind.Machine, Device = "Voice 2" }.SameAs(input), Is.False);
        }

        // ------------------------------------------------------------------ saved form

        [Test]
        public void InputsRoundTripThroughTheDocumentAndDefaultsAreNotWritten()
        {
            var d = new SessionDocument();
            d.SetInput("Audio", new TrackInput { Audio = AudioFrom.Stereo(2), Channel = 4 });
            d.SetInput("Lead", new TrackInput { Midi = MidiFromKind.Device, Device = "Keys" });
            d.SetInput("Pad", new TrackInput { Midi = MidiFromKind.Keyboard });
            d.SetInput("Wire", new TrackInput());
            d.SetInput("Res", new TrackInput { Audio = AudioFrom.Track("Audio") });
            Assert.That(d.Inputs.ContainsKey("Wire"), Is.False);

            var copy = Load(new MemoryStream(d.ToBytes()))!;
            Assert.That(copy.GetInput("Audio").SameAs(d.GetInput("Audio")), Is.True);
            Assert.That(copy.GetInput("Lead").Device, Is.EqualTo("Keys"));
            Assert.That(copy.GetInput("Pad").Midi, Is.EqualTo(MidiFromKind.Keyboard));
            Assert.That(copy.GetInput("Res").Audio, Is.EqualTo(AudioFrom.Track("Audio")));
            Assert.That(copy.GetInput("Nobody").Audio, Is.EqualTo(AudioFrom.Wired));
        }

        [Test]
        public void ASongWithoutInputsLoadsAsWiredAndAllInputs()
        {
            var d = Load(new MemoryStream(Encoding.UTF8.GetBytes("<Session><Tracks/></Session>")))!;
            Assert.That(d.Inputs, Is.Empty);
            var input = d.GetInput("Audio");
            Assert.That(input.Audio, Is.EqualTo(AudioFrom.Wired));
            Assert.That(input.Midi, Is.EqualTo(MidiFromKind.All));
        }

        // ------------------------------------------------------------------ meter

        static double Db(float peak) => Math.Clamp((20 * Math.Log10(peak / 32768.0) + 60) / 60, 0, 1);

        [Test]
        public void TheMeterRisesHoldsAndFalls()
        {
            var m = new InputMeterModel();
            m.Update(16384, 0.02, Db);
            double peak = m.Level;
            Assert.That(peak, Is.GreaterThan(0.8));
            m.Update(0, 0.1, Db);
            Assert.That(m.Level, Is.LessThan(peak));
            Assert.That(m.Hold, Is.EqualTo(peak).Within(1e-9), "the peak stays");
            m.Update(0, 2.0, Db);
            Assert.That(m.Hold, Is.LessThan(peak), "and goes after the hold time");
        }

        [Test]
        public void TheClipLightStaysUntilCleared()
        {
            var m = new InputMeterModel();
            m.Update(32768, 0.01, Db);
            Assert.That(m.Clipped, Is.True);
            m.Update(0, 5, Db);
            Assert.That(m.Clipped, Is.True);
            m.ClearClip();
            Assert.That(m.Clipped, Is.False);
        }

        // ------------------------------------------------------------------ input ring

        static float[] Ramp(int frames, int channels, float start = 0)
        {
            var a = new float[frames * channels];
            for (int i = 0; i < frames; i++)
                for (int c = 0; c < channels; c++) a[i * channels + c] = start + i + c * 0.5f;
            return a;
        }

        [Test]
        public void TheRingDeliversFramesInOrderPerChannel()
        {
            var e = new EngineInput();
            e.Configure(2, new[] { "Mic" }, 44100, 64);
            Assert.That(e.ChannelNames, Is.EqualTo(new[] { "Mic", "In 2" }));
            e.Push(Ramp(100, 2), 100, 2);
            e.BeginBlock(40);
            Assert.That(e.Channel(0)[0], Is.EqualTo(0));
            Assert.That(e.Channel(0)[39], Is.EqualTo(39));
            Assert.That(e.Channel(1)[3], Is.EqualTo(3.5f));
            e.BeginBlock(60);
            Assert.That(e.Channel(0)[0], Is.EqualTo(40));
            Assert.That(e.UnderrunFrames, Is.EqualTo(0));
        }

        [Test]
        public void ARunDryIsSilenceAndCountedOnceTheDeviceHasStarted()
        {
            var e = new EngineInput();
            e.Configure(1, null, 44100, 0);
            e.BeginBlock(32);
            Assert.That(e.UnderrunFrames, Is.EqualTo(0), "the device hasn't delivered yet");
            e.Push(Ramp(10, 1, 1), 10, 1);
            e.BeginBlock(32);
            Assert.That(e.Channel(0)[9], Is.EqualTo(10));
            Assert.That(e.Channel(0)[10], Is.EqualTo(0));
            Assert.That(e.UnderrunFrames, Is.EqualTo(22));
        }

        [Test]
        public void ARingThatIsFullDropsTheNewFramesAndCountsThem()
        {
            var e = new EngineInput();
            e.Configure(1, null, 44100, 0);
            int chunk = EngineInput.RingFrames / 2;
            e.Push(new float[chunk], chunk, 1);
            e.Push(new float[chunk], chunk, 1);
            e.Push(new float[100], 100, 1);
            Assert.That(e.OverrunFrames, Is.EqualTo(100));
        }

        [Test]
        public void TheRingWrapsAndFlushDropsWhatWaits()
        {
            var e = new EngineInput();
            e.Configure(1, null, 44100, 0);
            for (int round = 0; round < 5; round++)
            {
                var data = new float[EngineInput.RingFrames / 2];
                for (int i = 0; i < data.Length; i++) data[i] = round * 1000 + (i % 100);
                e.Push(data, data.Length, 1);
                int taken = 0;
                while (taken < data.Length)
                {
                    int n = Math.Min(256, data.Length - taken);
                    e.BeginBlock(n);
                    Assert.That(e.Channel(0)[n - 1], Is.EqualTo(round * 1000 + ((taken + n - 1) % 100)));
                    taken += n;
                }
            }
            e.Push(new float[50], 50, 1);
            e.Flush();
            e.BeginBlock(10);
            Assert.That(e.Channel(0)[0], Is.EqualTo(0));
        }

        [Test]
        public void ASourceWithFewerChannelsLeavesTheRestSilentAndPeaksAreTaken()
        {
            var e = new EngineInput();
            e.Configure(4, null, 48000, 0);
            e.Push(new[] { 0.5f, -0.25f, 0.5f, -0.25f }, 2, 2);
            e.BeginBlock(2);
            Assert.That(e.Channel(2)[0], Is.EqualTo(0));
            Assert.That(e.TakePeak(0), Is.EqualTo(0.5f));
            Assert.That(e.TakePeak(0), Is.EqualTo(0), "taking forgets it");
            Assert.That(e.TakePeak(1), Is.EqualTo(0.25f));
            Assert.That(e.Channel(9).Length, Is.GreaterThan(0), "a channel the device doesn't have is silence");
        }
    }
}
