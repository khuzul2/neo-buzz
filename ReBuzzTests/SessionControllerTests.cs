using ReBuzz.Control;
using ReBuzz.Core.Session;
using System;
using System.Collections.Generic;
using System.Linq;
using static ReBuzzTests.HardeningTests063;

namespace ReBuzzTests
{
    /// <summary>
    /// Fork addition: control surfaces (docs/specs/session-view.md 0.8) driving the session
    /// through a fake port: pads launch the slots in the ring, the lights follow, the ring moves.
    /// The scripts' own mappings are tested in ReBuzz.SessionTests.
    /// </summary>
    public class SessionControllerTests
    {
        const int Bar = 16;

        sealed class FakePort : IControllerPort
        {
            public readonly List<int> Sent = new List<int>();
            public readonly List<byte[]> SysEx = new List<byte[]>();
            public bool Disposed;
            public event Action<int> Received;
            public void Send(int message) => Sent.Add(message);
            public void Send(byte[] message) => SysEx.Add(message);
            public void Press(int message) => Received?.Invoke(message);
            public void Dispose() => Disposed = true;
        }

        static int On(int channel, int note, int velocity) => 0x90 | channel | (note << 8) | (velocity << 16);
        static int Cc(int channel, int cc, int value) => 0xB0 | channel | (cc << 8) | (value << 16);

        static readonly int Uncoloured = ControllerPalette.Nearest(new Rgb(0x8E, 0xB9, 0xC2));

        [Test]
        public void ALaunchpadLaunchesTheSlotsInItsRingAndItsLightsFollow()
        {
            using var rig = new KeysRig();
            var a = rig.Clip("A", Bar, (0, 60));
            var b = rig.Clip("B", Bar, (0, 62));
            var layout = rig.Layout;
            layout.Ensure();
            while (layout.SceneCount < 12) layout.InsertScene(layout.SceneCount);
            layout.SetSlot(0, 0, a);
            layout.SetSlot(0, 1, b);
            using var controllers = new SessionControllers(rig.Core, null, loadScripts: false);
            var port = new FakePort();
            var c = controllers.Attach(new LaunchpadMiniMk3Script(), port, "test");

            Assert.That(port.SysEx[0], Is.EqualTo(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0D, 0x0E, 0x01, 0xF7 }), "Programmer mode");
            Assert.That(port.Sent, Does.Contain(On(0, 81, Uncoloured)), "clip A, top left, in its colour");
            Assert.That(port.Sent, Does.Contain(On(0, 71, Uncoloured)), "clip B below it");
            Assert.That(port.Sent, Does.Contain(On(0, 82, 0)), "an empty slot: off");

            port.Sent.Clear();
            controllers.Refresh(c);
            Assert.That(port.Sent, Is.Empty, "nothing changed: nothing sent");

            port.Press(On(0, 81, 100));                               // the top left pad
            var track = layout.SequenceOf(0)!;
            Assert.That(rig.Session.GetState(track, a), Is.EqualTo(BuzzGUI.Interfaces.ClipState.Queued));
            controllers.Refresh(c);
            Assert.That(port.Sent, Is.EqualTo(new[] { On(0, 81, Uncoloured), On(1, 81, ControllerPalette.Green) }), "waiting: flashing green");

            rig.PlayTo(2);
            port.Sent.Clear();
            controllers.Refresh(c);
            Assert.That(port.Sent, Does.Contain(On(2, 81, ControllerPalette.Green)), "playing: pulsing green");
            Assert.That(port.Sent, Does.Contain(Cc(0, 92, ControllerPalette.White)), "the ring can go down");

            port.Press(Cc(0, 92, 127));                               // ▼
            Assert.That(controllers.Rings.Single(), Is.EqualTo((0, 1, 8, 8)));
            Assert.That(controllers.InRing(0, 8), Is.True);
            Assert.That(controllers.InRing(0, 0), Is.False);
            port.Press(On(0, 81, 100));                               // the top left pad: now scene 1
            Assert.That(rig.Session.GetState(track, b), Is.EqualTo(BuzzGUI.Interfaces.ClipState.Queued));

            port.Press(Cc(0, 95, 127));                               // Session held: shift
            port.Press(On(0, 81, 100));                               // shift + pad: stop the track
            port.Press(Cc(0, 95, 0));
            rig.PlayTo(Bar * 2 + 1);
            Assert.That(rig.Session.GetPlaying(track), Is.Null, "stopped");

            controllers.Detach(c);
            Assert.That(port.SysEx[^1], Is.EqualTo(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0D, 0x0E, 0x00, 0xF7 }), "Live mode again");
            Assert.That(port.Disposed, Is.True);
        }

        [Test]
        public void AnApc40ArmsTracksAndAnArmedEmptySlotRecords()
        {
            using var rig = new KeysRig();
            var layout = rig.Layout;
            layout.Ensure();
            using var controllers = new SessionControllers(rig.Core, null, loadScripts: false);
            var port = new FakePort();
            var c = controllers.Attach(new Apc40Mk2Script(), port, "test");
            port.Press(On(0, 48, 127));                               // Record Arm, track 1 (channel 0)
            Assert.That(layout.IsArmed(0), Is.True);
            port.Sent.Clear();
            controllers.Refresh(c);
            Assert.That(port.Sent, Does.Contain(On(0, 48, 1)), "its arm light");
            Assert.That(port.Sent, Does.Contain(On(0, 32, ControllerPalette.DimRed)), "its empty slots: ready to record");

            port.Press(On(0, 32, 127));                               // the top left pad: an armed empty slot
            Assert.That(layout.PatternAt(0, 0), Is.Not.Null, "a take is made there");
            Assert.That(layout.IsRecordingInto(layout.SequenceOf(0)!, layout.PatternAt(0, 0)), Is.True);
            controllers.Refresh(c);
            Assert.That(port.Sent, Does.Contain(On(14, 32, ControllerPalette.Red)), "a take waiting: blinking red");
        }

        [Test]
        public void ScriptsMatchTheirPortsAmongMany()
        {
            var inputs = new[] { "Keystation 49", "LPMiniMK3 DAW", "MIDIIN2 (LPMiniMK3 MIDI)", "APC mini mk2", "MIDIIN2 (APC mini mk2)" };
            var outputs = new[] { "Microsoft GS Wavetable Synth", "LPMiniMK3 DAW", "MIDIOUT2 (LPMiniMK3 MIDI)", "APC mini mk2", "MIDIOUT2 (APC mini mk2)" };
            var found = SessionControllers.Match(BuiltInScripts.All(), inputs, outputs);
            Assert.That(found.Select(f => (f.Script.Name, f.InputName, f.OutputName)), Is.EqualTo(new[]
            {
                ("Novation Launchpad Mini MK3", "MIDIIN2 (LPMiniMK3 MIDI)", "MIDIOUT2 (LPMiniMK3 MIDI)"),
                ("Akai APC mini mk2", "APC mini mk2", "APC mini mk2"),
            }));
        }
    }
}
