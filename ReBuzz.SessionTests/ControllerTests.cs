using ReBuzz.Control;

namespace ReBuzz.SessionTests
{
    /// <summary>Control surfaces (docs/specs/session-view.md 0.8), pure: the script API and the built-in scripts.</summary>
    [TestFixture]
    public class ControllerTests
    {
        static int On(int channel, int note, int velocity) => 0x90 | channel | (note << 8) | (velocity << 16);
        static int Cc(int channel, int cc, int value) => 0xB0 | channel | (cc << 8) | (value << 16);

        static SurfaceState State(int w, int h)
        {
            var s = new SurfaceState(w, h) { TrackCount = 12, SceneCount = 20 };
            for (int x = 0; x < w; x++) s.TrackExists[x] = true;
            for (int y = 0; y < h; y++) s.SceneExists[y] = true;
            return s;
        }

        static List<int> Lights(ControllerScript script, SurfaceState s)
        {
            var frame = new LedFrame();
            script.Render(s, frame);
            return LedFrame.Changes(null, frame);
        }

        [Test]
        public void ThePaletteFindsTheNearestFamilyAndGreysForUnsaturatedColours()
        {
            Assert.That(ControllerPalette.Nearest(new Rgb(0xD7, 0x00, 0x1C)), Is.EqualTo(ControllerPalette.Red));
            Assert.That(ControllerPalette.Nearest(new Rgb(0x37, 0x9C, 0x35)), Is.EqualTo(ControllerPalette.Green));
            Assert.That(ControllerPalette.Nearest(new Rgb(0xF7, 0xC1, 0x2D)), Is.EqualTo(ControllerPalette.Yellow).Or.EqualTo(ControllerPalette.Orange));
            Assert.That(ControllerPalette.Nearest(new Rgb(0x44, 0x6F, 0xB6)), Is.InRange(ControllerPalette.Full(8), ControllerPalette.Full(10)), "steel blue: a blue");
            Assert.That(ControllerPalette.Nearest(new Rgb(200, 200, 205)), Is.EqualTo(ControllerPalette.White));
            Assert.That(ControllerPalette.Nearest(Rgb.Black), Is.EqualTo(ControllerPalette.Off));
            Assert.That(ControllerPalette.Nearest(new Rgb(255, 0, 0), dim: true), Is.EqualTo(ControllerPalette.DimRed));
            Assert.That(ControllerPalette.ColorOf(ControllerPalette.Red), Is.EqualTo(new Rgb(255, 0, 0)));
        }

        [Test]
        public void OnlyChangedLightsAreSent()
        {
            var a = new LedFrame();
            a.Set(1, 0x10);
            a.Set(2, 0x20, 0x21);
            Assert.That(LedFrame.Changes(null, a), Is.EqualTo(new[] { 0x10, 0x20, 0x21 }), "the first frame: everything");
            var b = a.Copy();
            b.Set(2, 0x22);
            b.Set(3, 0x30);
            Assert.That(LedFrame.Changes(a, b), Is.EqualTo(new[] { 0x22, 0x30 }));
            Assert.That(LedFrame.Changes(b, b.Copy()), Is.Empty);
        }

        [Test]
        public void ScriptsFindTheirPorts()
        {
            var mini = new LaunchpadMiniMk3Script();
            Assert.That(mini.MatchInput("MIDIIN2 (LPMiniMK3 MIDI)"), Is.InRange(1, 100));
            Assert.That(mini.MatchInput("LPMiniMK3 MIDI"), Is.InRange(1, 100));
            Assert.That(mini.MatchInput("LPMiniMK3 DAW"), Is.EqualTo(0), "not the DAW port");
            Assert.That(mini.MatchInput("LPX MIDI"), Is.EqualTo(0));
            var push = new Push2Script();
            Assert.That(push.MatchInput("MIDIIN2 (Ableton Push 2)"), Is.InRange(1, 100));
            Assert.That(push.MatchInput("Ableton Push 2"), Is.EqualTo(0), "the Live port is Live's");
            Assert.That(push.MatchOutput("MIDIOUT2 (Ableton Push 2)"), Is.InRange(1, 100));
            var apc = new ApcMiniMk2Script();
            Assert.That(apc.MatchInput("APC mini mk2"), Is.InRange(1, 100));
            Assert.That(apc.MatchInput("MIDIIN2 (APC mini mk2)"), Is.EqualTo(0), "the notes port");
            Assert.That(new Apc40Mk2Script().MatchInput("APC40 mkII"), Is.InRange(1, 100));
        }

        [Test]
        public void ALaunchpadEntersProgrammerModeAndReadsItsGrid()
        {
            var lp = new LaunchpadMiniMk3Script();
            Assert.That(lp.Hello().Single(), Is.EqualTo(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0D, 0x0E, 0x01, 0xF7 }));
            Assert.That(lp.Goodbye().Single(), Is.EqualTo(new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, 0x0D, 0x0E, 0x00, 0xF7 }));
            Assert.That(new LaunchpadXScript().Hello().Single()[5], Is.EqualTo(0x0C));
            Assert.That(lp.Read(On(0, 11, 100), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 0, 7, true, 100)), "bottom left: the ring's last row");
            Assert.That(lp.Read(On(0, 88, 0), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 7, 0, false, 0)), "top right, released");
            Assert.That(lp.Read(On(0, 34, 90), true).Kind, Is.EqualTo(ControlKind.TrackStop), "with shift (Session held): stop the track");
            Assert.That(lp.Read(On(0, 34, 90), true).X, Is.EqualTo(3));
            Assert.That(lp.Read(Cc(0, 89, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Scene, 0, 0, true, 127)));
            Assert.That(lp.Read(Cc(0, 19, 127), false).Y, Is.EqualTo(7));
            Assert.That(lp.Read(Cc(0, 19, 127), true).Kind, Is.EqualTo(ControlKind.StopAll));
            Assert.That(lp.Read(Cc(0, 91, 127), false).Kind, Is.EqualTo(ControlKind.Up));
            Assert.That(lp.Read(Cc(0, 94, 127), false).Kind, Is.EqualTo(ControlKind.Right));
            Assert.That(lp.Read(Cc(0, 95, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Shift, 0, 0, true, 127)));
            var pro = new LaunchpadProMk3Script();
            Assert.That(pro.Read(Cc(0, 80, 127), false).Kind, Is.EqualTo(ControlKind.Up));
            Assert.That(pro.Read(Cc(0, 3, 127), false), Is.EqualTo(new ControlEvent(ControlKind.TrackStop, 2, 0, true, 127)));
        }

        [Test]
        public void ALaunchpadLightsClipsLikeLive()
        {
            var lp = new LaunchpadMiniMk3Script();
            var s = State(8, 8);
            s.SetPad(0, 7, PadState.Stopped, new Rgb(0xD7, 0x00, 0x1C));      // bottom left: a red clip
            s.SetPad(1, 7, PadState.Playing, Rgb.White);
            s.SetPad(2, 7, PadState.Queued, new Rgb(0x37, 0x9C, 0x35));
            s.SetPad(3, 7, PadState.ArmedEmpty, Rgb.Black);
            s.SetPad(0, 0, PadState.Recording, Rgb.White);
            s.SceneHasClips[7] = true;
            s.SceneQueued[0] = true;
            var lights = Lights(lp, s);
            Assert.That(lights, Does.Contain(On(0, 11, ControllerPalette.Red)), "still, in its colour (channel 1)");
            Assert.That(lights, Does.Contain(On(2, 12, ControllerPalette.Green)), "playing: pulsing green (channel 3)");
            int queued = lights.IndexOf(On(0, 13, ControllerPalette.Green));
            Assert.That(lights[queued + 1], Is.EqualTo(On(1, 13, ControllerPalette.Green)), "waiting: its colour, then flashing green (channel 2)");
            Assert.That(lights, Does.Contain(On(0, 14, ControllerPalette.DimRed)), "an armed track's empty slot: dim red");
            Assert.That(lights, Does.Contain(On(2, 81, ControllerPalette.Red)), "recording: pulsing red");
            Assert.That(lights, Does.Contain(On(0, 15, 0)), "empty: off");
            Assert.That(lights, Does.Contain(Cc(0, 19, ControllerPalette.DimWhite)), "a scene with clips");
            Assert.That(lights, Does.Contain(Cc(1, 89, ControllerPalette.Green)), "a scene waiting: flashing");
            Assert.That(lights, Does.Contain(Cc(0, 91, 0)), "can't go up from the top");
            Assert.That(lights, Does.Contain(Cc(0, 92, ControllerPalette.White)), "can go down");
            Assert.That(lights, Does.Contain(Cc(0, 94, ControllerPalette.White)), "can go right: 12 tracks");
        }

        [Test]
        public void AnApcMiniMk2ReadsItsGridAndButtonsAndLightsThem()
        {
            var apc = new ApcMiniMk2Script();
            Assert.That(apc.Read(On(0, 0, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 0, 7, true, 127)), "note 0: bottom left");
            Assert.That(apc.Read(On(0, 63, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 7, 0, true, 127)));
            Assert.That(apc.Read(On(0, 102, 127), false), Is.EqualTo(new ControlEvent(ControlKind.TrackStop, 2, 0, true, 127)));
            Assert.That(apc.Read(On(0, 104, 127), true).Kind, Is.EqualTo(ControlKind.Up), "Shift + ▲");
            Assert.That(apc.Read(On(0, 107, 127), true).Kind, Is.EqualTo(ControlKind.Right));
            Assert.That(apc.Read(On(0, 113, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Scene, 0, 1, true, 127)));
            Assert.That(apc.Read(On(0, 113, 127), true).Kind, Is.EqualTo(ControlKind.StopAll));
            Assert.That(apc.Read(On(0, 122, 127), false).Kind, Is.EqualTo(ControlKind.Shift));
            var s = State(8, 8);
            s.SetPad(0, 7, PadState.Stopped, new Rgb(0xD7, 0x00, 0x1C));
            s.SetPad(1, 7, PadState.Playing, Rgb.White);
            s.TrackPlaying[1] = true;
            s.SceneHasClips[7] = true;
            var lights = Lights(apc, s);
            Assert.That(lights, Does.Contain(On(6, 0, ControllerPalette.Red)), "full brightness: channel 7");
            Assert.That(lights, Does.Contain(On(9, 1, ControllerPalette.Green)), "pulsing");
            Assert.That(lights, Does.Contain(On(0, 101, 1)), "the playing track's stop button lit");
            Assert.That(lights, Does.Contain(On(0, 119, 1)), "the bottom scene has clips");
        }

        [Test]
        public void AnApc40Mk2IntroducesItselfAndReadsItsFiveRows()
        {
            var apc = new Apc40Mk2Script();
            Assert.That(apc.Height, Is.EqualTo(5));
            Assert.That(apc.Hello().Single(), Is.EqualTo(new byte[] { 0xF0, 0x47, 0x7F, 0x29, 0x60, 0x00, 0x04, 0x41, 0x08, 0x02, 0x01, 0xF7 }));
            Assert.That(apc.Read(On(0, 32, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 0, 0, true, 127)), "the top row: 32-39");
            Assert.That(apc.Read(On(0, 7, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 7, 4, true, 127)));
            Assert.That(apc.Read(On(5, 52, 127), false), Is.EqualTo(new ControlEvent(ControlKind.TrackStop, 5, 0, true, 127)), "Clip Stop on the track's channel");
            Assert.That(apc.Read(On(2, 48, 127), false).Kind, Is.EqualTo(ControlKind.TrackArm));
            Assert.That(apc.Read(On(0, 84, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Scene, 0, 2, true, 127)));
            Assert.That(apc.Read(On(0, 97, 127), false).Kind, Is.EqualTo(ControlKind.Left));
            var s = State(8, 5);
            s.TrackArmed[3] = true;
            var lights = Lights(apc, s);
            Assert.That(lights, Does.Contain(On(3, 48, 1)), "record arm lit on the track's channel");
        }

        [Test]
        public void APush2GoesToUserModeWritesItsPaletteAndReadsItsButtons()
        {
            var push = new Push2Script();
            var hello = push.Hello().ToList();
            Assert.That(hello[0], Is.EqualTo(new byte[] { 0xF0, 0x00, 0x21, 0x1D, 0x01, 0x01, 0x0A, 0x01, 0xF7 }), "User mode");
            Assert.That(hello[^1], Is.EqualTo(new byte[] { 0xF0, 0x00, 0x21, 0x1D, 0x01, 0x01, 0x05, 0xF7 }), "reapply the palette");
            var red = hello.Single(m => m.Length > 7 && m[6] == 0x03 && m[7] == ControllerPalette.Red);
            Assert.That(red.Skip(8).Take(6), Is.EqualTo(new byte[] { 0x7F, 0x01, 0, 0, 0, 0 }), "255 = 0x7F + 1 × 128");
            Assert.That(push.Read(On(0, 36, 100), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 0, 7, true, 100)));
            Assert.That(push.Read(On(0, 99, 100), false), Is.EqualTo(new ControlEvent(ControlKind.Pad, 7, 0, true, 100)));
            Assert.That(push.Read(Cc(0, 43, 127), false), Is.EqualTo(new ControlEvent(ControlKind.Scene, 0, 0, true, 127)), "scene 1: CC 43");
            Assert.That(push.Read(Cc(0, 36, 127), false).Y, Is.EqualTo(7));
            Assert.That(push.Read(Cc(0, 22, 127), false), Is.EqualTo(new ControlEvent(ControlKind.TrackStop, 2, 0, true, 127)));
            Assert.That(push.Read(Cc(0, 104, 127), false), Is.EqualTo(new ControlEvent(ControlKind.TrackArm, 2, 0, true, 127)));
            Assert.That(push.Read(Cc(0, 46, 127), false).Kind, Is.EqualTo(ControlKind.Up));
            Assert.That(push.Read(Cc(0, 46, 0), false).Kind, Is.EqualTo(ControlKind.None), "a release does nothing");
            Assert.That(push.Read(Cc(0, 49, 0), false), Is.EqualTo(new ControlEvent(ControlKind.Shift, 0, 0, false, 127)), "shift's release counts");
        }
    }
}
