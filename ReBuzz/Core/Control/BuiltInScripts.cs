#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): the controllers ReBuzz knows,
// written from their makers' published MIDI protocols (Novation's Programmer's Reference
// Manuals, Akai's Communications Protocols, Ableton's Push 2 MIDI and Display Interface).
// Pure, as the script API (ReBuzz.SessionTests links this file).

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Control
{
    /// <summary>
    /// A controller whose pads take a palette colour as the velocity of a note-on (or the value
    /// of a CC), on a channel that sets how it lights: still, pulsing or blinking. A pulse or
    /// blink goes between the colour set still before it and its own.
    /// </summary>
    public abstract class PaletteScript : ControllerScript
    {
        protected enum Light { Still, Pulse, Blink }

        protected abstract int StillChannel { get; }
        protected abstract int PulseChannel { get; }
        protected abstract int BlinkChannel { get; }

        /// <summary>How a slot lights (Live's conventions): clips in their colour, playing green and pulsing, waiting blinking, recording red.</summary>
        protected static (int Color, Light Mode, int Base) Look(PadState state, Rgb color) => state switch
        {
            PadState.ArmedEmpty => (ControllerPalette.DimRed, Light.Still, 0),
            PadState.Stopped => (ControllerPalette.Nearest(color), Light.Still, 0),
            PadState.Queued => (ControllerPalette.Green, Light.Blink, ControllerPalette.Nearest(color)),
            PadState.Playing => (ControllerPalette.Green, Light.Pulse, ControllerPalette.DimGreen),
            PadState.Stopping => (ControllerPalette.Green, Light.Blink, 0),
            PadState.Recording => (ControllerPalette.Red, Light.Pulse, ControllerPalette.DimRed),
            PadState.RecordQueued => (ControllerPalette.Red, Light.Blink, 0),
            _ => (ControllerPalette.Off, Light.Still, 0),
        };

        /// <summary>Lights a note's LED (key: the note).</summary>
        protected void NoteLed(LedFrame leds, int note, int color, Light mode = Light.Still, int baseColor = 0) =>
            Set(leds, note, (ch, v) => Note(ch, note, v), color, mode, baseColor);

        /// <summary>Lights a CC's LED (key: 128 + the CC).</summary>
        protected void CcLed(LedFrame leds, int cc, int color, Light mode = Light.Still, int baseColor = 0) =>
            Set(leds, 128 + cc, (ch, v) => Cc(ch, cc, v), color, mode, baseColor);

        void Set(LedFrame leds, int key, Func<int, int, int> message, int color, Light mode, int baseColor)
        {
            if (mode == Light.Still)
                leds.Set(key, message(StillChannel, color));
            else
                leds.Set(key, message(StillChannel, baseColor), message(mode == Light.Pulse ? PulseChannel : BlinkChannel, color));
        }
    }

    // ====================================================================== Novation Launchpad

    /// <summary>
    /// Launchpads (Mini MK3, X, Pro MK3) in Programmer mode: pads are notes 11-88 (row, then
    /// column, from the bottom left); the scene buttons on the right are CCs 89, 79, … 19.
    /// Lights: channel 1 still, 2 flashing, 3 pulsing (MIDI channels 0, 1, 2 here).
    /// </summary>
    public abstract class LaunchpadScript : PaletteScript
    {
        protected override int StillChannel => 0;
        protected override int PulseChannel => 2;
        protected override int BlinkChannel => 1;

        /// <summary>The SysEx device byte (0x0D Mini MK3, 0x0C X, 0x0E Pro MK3).</summary>
        protected abstract byte DeviceId { get; }

        /// <summary>What its ports' names have in them ("LPMiniMK3", "LPX", "LPProMK3").</summary>
        protected abstract string PortTag { get; }

        protected virtual int UpCc => 91;
        protected virtual int DownCc => 92;
        protected virtual int LeftCc => 93;
        protected virtual int RightCc => 94;
        /// <summary>Held as shift (Session on the Mini and X).</summary>
        protected virtual int ShiftCc => 95;

        byte[] Sysex(params byte[] body) => new byte[] { 0xF0, 0x00, 0x20, 0x29, 0x02, DeviceId }.Concat(body).Append((byte)0xF7).ToArray();

        public override IEnumerable<byte[]> Hello() => new[] { Sysex(0x0E, 0x01) };       // Programmer mode
        public override IEnumerable<byte[]> Goodbye() => new[] { Sysex(0x0E, 0x00) };     // back to Live mode

        /// <summary>The MIDI ports (not the DAW ones, nor DIN).</summary>
        public override int MatchInput(string port) =>
            Has(port, PortTag) && !Has(port, "DAW") && !Has(port, "DIN") ? (Has(port, "MIDI") ? 10 : 5) : 0;

        public override int MatchOutput(string port) => MatchInput(port);

        public override ControlEvent Read(int message, bool shift)
        {
            int n = Data1(message);
            bool pressed = IsPress(message);
            if (IsNote(message) && n >= 11 && n <= 88 && n % 10 >= 1 && n % 10 <= 8)
            {
                int x = n % 10 - 1, y = 8 - n / 10;
                // With shift a press stops the track; a release is still a pad's (one held before shift).
                if (shift && pressed) return new ControlEvent(ControlKind.TrackStop, x, 0, true, 127);
                return new ControlEvent(ControlKind.Pad, x, y, pressed, Data2(message));
            }
            if (!IsCc(message)) return ControlEvent.None;
            if (n % 10 == 9 && n >= 19 && n <= 89)
                return shift ? (pressed ? new ControlEvent(ControlKind.StopAll, 0, 0, true, 127) : ControlEvent.None)
                             : new ControlEvent(ControlKind.Scene, 0, 8 - n / 10, pressed, Data2(message));
            if (n == ShiftCc) return new ControlEvent(ControlKind.Shift, 0, 0, pressed, 127);
            if (n == UpCc) return new ControlEvent(ControlKind.Up, 0, 0, pressed, 127);
            if (n == DownCc) return new ControlEvent(ControlKind.Down, 0, 0, pressed, 127);
            if (n == LeftCc) return new ControlEvent(ControlKind.Left, 0, 0, pressed, 127);
            if (n == RightCc) return new ControlEvent(ControlKind.Right, 0, 0, pressed, 127);
            return ReadMore(n, pressed, shift);
        }

        /// <summary>Other buttons a model has.</summary>
        protected virtual ControlEvent ReadMore(int cc, bool pressed, bool shift) => ControlEvent.None;

        public override void Render(SurfaceState s, LedFrame leds)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (color, mode, baseColor) = Look(s.Pad(x, y), s.Color(x, y));
                    NoteLed(leds, (8 - y) * 10 + x + 1, color, mode, baseColor);
                }
            for (int y = 0; y < 8; y++)
            {
                int cc = (8 - y) * 10 + 9;
                if (s.Shift) CcLed(leds, cc, ControllerPalette.DimRed);                          // stop all
                else if (s.SceneQueued[y]) CcLed(leds, cc, ControllerPalette.Green, Light.Blink);
                else CcLed(leds, cc, s.SceneHasClips[y] ? ControllerPalette.DimWhite : ControllerPalette.Off);
            }
            CcLed(leds, UpCc, s.CanMove(0, -1) ? ControllerPalette.White : ControllerPalette.Off);
            CcLed(leds, DownCc, s.CanMove(0, 1) ? ControllerPalette.White : ControllerPalette.Off);
            CcLed(leds, LeftCc, s.CanMove(-1, 0) ? ControllerPalette.White : ControllerPalette.Off);
            CcLed(leds, RightCc, s.CanMove(1, 0) ? ControllerPalette.White : ControllerPalette.Off);
            CcLed(leds, ShiftCc, s.Shift ? ControllerPalette.White : ControllerPalette.DimWhite);
            RenderMore(s, leds);
        }

        protected virtual void RenderMore(SurfaceState s, LedFrame leds) { }
    }

    public sealed class LaunchpadMiniMk3Script : LaunchpadScript
    {
        public override string Name => "Novation Launchpad Mini MK3";
        protected override byte DeviceId => 0x0D;
        protected override string PortTag => "LPMiniMK3";
    }

    public sealed class LaunchpadXScript : LaunchpadScript
    {
        public override string Name => "Novation Launchpad X";
        protected override byte DeviceId => 0x0C;
        protected override string PortTag => "LPX";
    }

    /// <summary>
    /// Launchpad Pro MK3: Shift (CC 90), ◀ ▶ on the top row (91, 92), ▲ ▼ on the left column
    /// (80, 70), Play (20); the bottom row (CCs 1-8) stops each track. (From Novation's
    /// Programmer mode layout; the left and bottom rows aren't in its manual's text.)
    /// </summary>
    public sealed class LaunchpadProMk3Script : LaunchpadScript
    {
        public override string Name => "Novation Launchpad Pro MK3";
        protected override byte DeviceId => 0x0E;
        protected override string PortTag => "LPProMK3";
        protected override int UpCc => 80;
        protected override int DownCc => 70;
        protected override int LeftCc => 91;
        protected override int RightCc => 92;
        protected override int ShiftCc => 90;
        const int PlayCc = 20;

        protected override ControlEvent ReadMore(int cc, bool pressed, bool shift)
        {
            if (cc >= 1 && cc <= 8) return pressed ? new ControlEvent(ControlKind.TrackStop, cc - 1, 0, true, 127) : ControlEvent.None;
            if (cc == PlayCc) return pressed ? new ControlEvent(ControlKind.Play, 0, 0, true, 127) : ControlEvent.None;
            return ControlEvent.None;
        }

        protected override void RenderMore(SurfaceState s, LedFrame leds)
        {
            for (int x = 0; x < 8; x++)
                CcLed(leds, 1 + x, s.TrackPlaying[x] ? ControllerPalette.Red : s.TrackExists[x] ? ControllerPalette.DimRed : ControllerPalette.Off);
            CcLed(leds, PlayCc, s.Playing ? ControllerPalette.Green : ControllerPalette.DimGreen);
        }
    }

    // ====================================================================== Akai

    /// <summary>
    /// APC mini mk2: pads are notes 0-63 from the bottom left; the bottom row (100-107) stops
    /// each track, or with Shift moves the ring (104 ▲, 105 ▼, 106 ◀, 107 ▶); the right column
    /// (112-119) launches scenes, or with Shift stops all; Shift is 122. Pad lights: channels
    /// 0-6 set brightness (6: full), 7-10 pulse, 11-15 blink; the single-colour buttons take
    /// 0 off, 1 on, 2 blinking.
    /// </summary>
    public sealed class ApcMiniMk2Script : PaletteScript
    {
        public override string Name => "Akai APC mini mk2";
        protected override int StillChannel => 6;
        protected override int PulseChannel => 9;     // 1/4
        protected override int BlinkChannel => 14;    // 1/4

        const int ShiftNote = 122;

        public override int MatchInput(string port) => Has(port, "APC mini mk2") && !Has(port, "MIDIIN2") && !Has(port, "MIDIOUT2") ? 10 : 0;
        public override int MatchOutput(string port) => MatchInput(port);

        public override ControlEvent Read(int message, bool shift)
        {
            if (!IsNote(message)) return ControlEvent.None;
            int n = Data1(message);
            bool pressed = IsPress(message);
            if (n <= 63)
            {
                int x = n % 8, y = 7 - n / 8;
                return new ControlEvent(ControlKind.Pad, x, y, pressed, Data2(message));
            }
            if (n == ShiftNote) return new ControlEvent(ControlKind.Shift, 0, 0, pressed, 127);
            if (!pressed) return ControlEvent.None;
            if (n >= 100 && n <= 107)
            {
                if (!shift) return new ControlEvent(ControlKind.TrackStop, n - 100, 0, true, 127);
                return n switch
                {
                    104 => new ControlEvent(ControlKind.Up, 0, 0, true, 127),
                    105 => new ControlEvent(ControlKind.Down, 0, 0, true, 127),
                    106 => new ControlEvent(ControlKind.Left, 0, 0, true, 127),
                    107 => new ControlEvent(ControlKind.Right, 0, 0, true, 127),
                    _ => ControlEvent.None,
                };
            }
            if (n >= 112 && n <= 119)
                return shift ? new ControlEvent(ControlKind.StopAll, 0, 0, true, 127) : new ControlEvent(ControlKind.Scene, 0, n - 112, true, 127);
            return ControlEvent.None;
        }

        public override void Render(SurfaceState s, LedFrame leds)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (color, mode, baseColor) = Look(s.Pad(x, y), s.Color(x, y));
                    NoteLed(leds, (7 - y) * 8 + x, color, mode, baseColor);
                }
            for (int x = 0; x < 8; x++)
            {
                int value = s.Shift
                    ? (x >= 4 && s.CanMove(x == 6 ? -1 : x == 7 ? 1 : 0, x == 4 ? -1 : x == 5 ? 1 : 0) ? 1 : 0)
                    : (s.TrackPlaying[x] ? 1 : 0);
                leds.Set(100 + x, Note(0, 100 + x, value));
            }
            for (int y = 0; y < 8; y++)
                leds.Set(112 + y, Note(0, 112 + y, s.SceneQueued[y] ? 2 : s.SceneHasClips[y] ? 1 : 0));
        }
    }

    /// <summary>
    /// APC40 mkII in Ableton Live mode (its introduction message): clip pads are notes 0-39
    /// (the top row 32-39), scene buttons 82-86, Clip Stop 52 and Record Arm 48 on the track's
    /// channel, Stop All 81, the bank arrows 94 ▲ 95 ▼ 96 ▶ 97 ◀, Shift 98, Play 91, Session
    /// Record 102. RGB lights: channel 0 still, 6-10 pulse, 11-15 blink.
    /// </summary>
    public sealed class Apc40Mk2Script : PaletteScript
    {
        public override string Name => "Akai APC40 mkII";
        public override int Height => 5;
        protected override int StillChannel => 0;
        protected override int PulseChannel => 9;     // 1/4
        protected override int BlinkChannel => 14;    // 1/4

        public override int MatchInput(string port) => Has(port, "APC40 mkII") ? 10 : 0;
        public override int MatchOutput(string port) => MatchInput(port);

        /// <summary>Ableton Live mode (0x41), host version 8.2.1.</summary>
        public override IEnumerable<byte[]> Hello() => new[] { new byte[] { 0xF0, 0x47, 0x7F, 0x29, 0x60, 0x00, 0x04, 0x41, 0x08, 0x02, 0x01, 0xF7 } };

        /// <summary>Back to Generic mode (0x40).</summary>
        public override IEnumerable<byte[]> Goodbye() => new[] { new byte[] { 0xF0, 0x47, 0x7F, 0x29, 0x60, 0x00, 0x04, 0x40, 0x08, 0x02, 0x01, 0xF7 } };

        public override ControlEvent Read(int message, bool shift)
        {
            if (!IsNote(message)) return ControlEvent.None;
            int n = Data1(message), channel = message & 0x0F;
            bool pressed = IsPress(message);
            if (n <= 39) return new ControlEvent(ControlKind.Pad, n % 8, 4 - n / 8, pressed, Data2(message));
            if (n == 98) return new ControlEvent(ControlKind.Shift, 0, 0, pressed, 127);
            if (!pressed) return ControlEvent.None;
            return n switch
            {
                >= 82 and <= 86 => new ControlEvent(ControlKind.Scene, 0, n - 82, true, 127),
                52 => new ControlEvent(ControlKind.TrackStop, channel, 0, true, 127),
                48 => new ControlEvent(ControlKind.TrackArm, channel, 0, true, 127),
                81 => new ControlEvent(ControlKind.StopAll, 0, 0, true, 127),
                94 => new ControlEvent(ControlKind.Up, 0, 0, true, 127),
                95 => new ControlEvent(ControlKind.Down, 0, 0, true, 127),
                96 => new ControlEvent(ControlKind.Right, 0, 0, true, 127),
                97 => new ControlEvent(ControlKind.Left, 0, 0, true, 127),
                91 => new ControlEvent(ControlKind.Play, 0, 0, true, 127),
                102 => new ControlEvent(ControlKind.SessionRecord, 0, 0, true, 127),
                _ => ControlEvent.None,
            };
        }

        public override void Render(SurfaceState s, LedFrame leds)
        {
            for (int y = 0; y < 5; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (color, mode, baseColor) = Look(s.Pad(x, y), s.Color(x, y));
                    NoteLed(leds, (4 - y) * 8 + x, color, mode, baseColor);
                }
            for (int y = 0; y < 5; y++)
            {
                if (s.SceneQueued[y]) NoteLed(leds, 82 + y, ControllerPalette.Green, Light.Blink);
                else NoteLed(leds, 82 + y, s.SceneHasClips[y] ? ControllerPalette.DimGreen : ControllerPalette.Off);
            }
            for (int x = 0; x < 8; x++)
            {
                leds.Set(1000 + x, Note(x, 52, s.TrackPlaying[x] ? 1 : 0));         // Clip Stop
                leds.Set(1100 + x, Note(x, 48, s.TrackArmed[x] ? 1 : 0));           // Record Arm
            }
            leds.Set(91, Note(0, 91, s.Playing ? 1 : 0));
            leds.Set(102, Note(0, 102, s.SessionRecording ? 1 : 0));
        }
    }

    // ====================================================================== Ableton Push 2

    /// <summary>
    /// Push 2 in User mode, on its User port: pads are notes 36-99 from the bottom left; the
    /// scene buttons CCs 43 (top) to 36; the buttons below the display (CCs 20-27) stop each
    /// track, those above it (102-109) arm it; ◀ ▶ ▲ ▼ are 44-47, Shift 49, Play 85,
    /// Record 86 (Session Record), Stop Clip 29 (stop all). Lights: channel 0 still, 6-10
    /// pulse, 11-15 blink; it gets the shared palette's entries when it connects.
    /// </summary>
    public sealed class Push2Script : PaletteScript
    {
        public override string Name => "Ableton Push 2 (User mode)";
        protected override int StillChannel => 0;
        protected override int PulseChannel => 9;
        protected override int BlinkChannel => 14;

        static byte[] Sysex(params byte[] body) => new byte[] { 0xF0, 0x00, 0x21, 0x1D, 0x01, 0x01 }.Concat(body).Append((byte)0xF7).ToArray();

        /// <summary>The User port: "MIDIIN2 (Ableton Push 2)" on Windows, "Ableton Push 2 User Port" on macOS.</summary>
        public override int MatchInput(string port) =>
            Has(port, "Ableton Push 2") && (Has(port, "MIDIIN2") || Has(port, "User Port")) ? 10 : 0;

        public override int MatchOutput(string port) =>
            Has(port, "Ableton Push 2") && (Has(port, "MIDIOUT2") || Has(port, "User Port")) ? 10 : 0;

        /// <summary>The palette entries the scripts use.</summary>
        public static IEnumerable<int> PaletteEntries =>
            new[] { ControllerPalette.DarkGrey, ControllerPalette.Grey, ControllerPalette.White }
                .Concat(Enumerable.Range(0, ControllerPalette.Families.Length).SelectMany(f => new[] { ControllerPalette.Full(f), ControllerPalette.Dim(f) }));

        public override IEnumerable<byte[]> Hello()
        {
            yield return Sysex(0x0A, 0x01);                                   // User mode
            foreach (int index in PaletteEntries)
            {
                var c = ControllerPalette.ColorOf(index);
                int w = (c.R + c.G + c.B) / 3;
                yield return Sysex(0x03, (byte)index, (byte)(c.R & 0x7F), (byte)(c.R >> 7), (byte)(c.G & 0x7F), (byte)(c.G >> 7),
                    (byte)(c.B & 0x7F), (byte)(c.B >> 7), (byte)(w & 0x7F), (byte)(w >> 7));
            }
            yield return Sysex(0x05);                                         // reapply the palette
        }

        public override IEnumerable<byte[]> Goodbye() => new[] { Sysex(0x0A, 0x00) };   // Live mode again

        public override ControlEvent Read(int message, bool shift)
        {
            int n = Data1(message);
            bool pressed = IsPress(message);
            if (IsNote(message))
            {
                if (n < 36 || n > 99) return ControlEvent.None;
                return new ControlEvent(ControlKind.Pad, (n - 36) % 8, 7 - (n - 36) / 8, pressed, Data2(message));
            }
            if (!IsCc(message)) return ControlEvent.None;
            if (n == 49) return new ControlEvent(ControlKind.Shift, 0, 0, pressed, 127);
            if (!pressed) return ControlEvent.None;
            return n switch
            {
                >= 36 and <= 43 => new ControlEvent(ControlKind.Scene, 0, 43 - n, true, 127),
                >= 20 and <= 27 => new ControlEvent(ControlKind.TrackStop, n - 20, 0, true, 127),
                >= 102 and <= 109 => new ControlEvent(ControlKind.TrackArm, n - 102, 0, true, 127),
                44 => new ControlEvent(ControlKind.Left, 0, 0, true, 127),
                45 => new ControlEvent(ControlKind.Right, 0, 0, true, 127),
                46 => new ControlEvent(ControlKind.Up, 0, 0, true, 127),
                47 => new ControlEvent(ControlKind.Down, 0, 0, true, 127),
                85 => new ControlEvent(ControlKind.Play, 0, 0, true, 127),
                86 => new ControlEvent(ControlKind.SessionRecord, 0, 0, true, 127),
                29 => new ControlEvent(ControlKind.StopAll, 0, 0, true, 127),
                _ => ControlEvent.None,
            };
        }

        public override void Render(SurfaceState s, LedFrame leds)
        {
            for (int y = 0; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    var (color, mode, baseColor) = Look(s.Pad(x, y), s.Color(x, y));
                    NoteLed(leds, 36 + (7 - y) * 8 + x, color, mode, baseColor);
                }
            for (int y = 0; y < 8; y++)
            {
                if (s.SceneQueued[y]) CcLed(leds, 43 - y, ControllerPalette.Green, Light.Blink);
                else CcLed(leds, 43 - y, s.SceneHasClips[y] ? ControllerPalette.DimWhite : ControllerPalette.Off);
            }
            for (int x = 0; x < 8; x++)
            {
                CcLed(leds, 20 + x, s.TrackPlaying[x] ? ControllerPalette.Red : s.TrackExists[x] ? ControllerPalette.DimRed : ControllerPalette.Off);
                CcLed(leds, 102 + x, s.TrackArmed[x] ? ControllerPalette.Red : s.TrackExists[x] ? ControllerPalette.DarkGrey : ControllerPalette.Off);
            }
            // White buttons: the value is the brightness.
            leds.Set(128 + 44, Cc(0, 44, s.CanMove(-1, 0) ? 127 : 0));
            leds.Set(128 + 45, Cc(0, 45, s.CanMove(1, 0) ? 127 : 0));
            leds.Set(128 + 46, Cc(0, 46, s.CanMove(0, -1) ? 127 : 0));
            leds.Set(128 + 47, Cc(0, 47, s.CanMove(0, 1) ? 127 : 0));
            leds.Set(128 + 49, Cc(0, 49, s.Shift ? 127 : 20));
            CcLed(leds, 85, s.Playing ? ControllerPalette.Green : ControllerPalette.DarkGrey);
            CcLed(leds, 86, s.SessionRecording ? ControllerPalette.Red : ControllerPalette.DarkGrey);
        }
    }

    /// <summary>The scripts ReBuzz comes with.</summary>
    public static class BuiltInScripts
    {
        public static IReadOnlyList<ControllerScript> All() => new ControllerScript[]
        {
            new LaunchpadMiniMk3Script(), new LaunchpadXScript(), new LaunchpadProMk3Script(),
            new ApcMiniMk2Script(), new Apc40Mk2Script(), new Push2Script(),
        };
    }
}
