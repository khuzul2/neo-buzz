#nullable disable
// Fork addition (neo-buzz, docs/specs/session-view.md 0.8): control surfaces for the Session
// View, the script API. A script turns a controller's MIDI into what was pressed, and the
// session's state into its lights; ReBuzz does the rest (ports, the session ring, launching,
// sending only the lights that changed). Pure: no dependency on the rest of ReBuzz, so
// ReBuzz.SessionTests links this file, and scripts outside ReBuzz can be written against it.
//
// A new controller is a class deriving from ControllerScript, built into a .dll and put in the
// Controllers folder beside ReBuzz.exe (see docs/specs/session-view.md, 0.8).

using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Control
{
    /// <summary>A colour, 0-255 a channel.</summary>
    public readonly record struct Rgb(byte R, byte G, byte B)
    {
        public static readonly Rgb Black = new Rgb(0, 0, 0);
        public static readonly Rgb White = new Rgb(255, 255, 255);
        public static readonly Rgb Green = new Rgb(0x43, 0xC4, 0x63);
        public static readonly Rgb Red = new Rgb(0xE8, 0x2A, 0x2A);
        public static readonly Rgb Amber = new Rgb(0xE8, 0xA3, 0x3A);
    }

    /// <summary>What a slot of the session ring shows.</summary>
    public enum PadState
    {
        /// <summary>Outside the session (no track or scene there).</summary>
        Off,
        /// <summary>An empty slot.</summary>
        Empty,
        /// <summary>An empty slot of a record-armed track: pressing it records a take.</summary>
        ArmedEmpty,
        /// <summary>A clip, not playing.</summary>
        Stopped,
        /// <summary>A clip waiting for its launch time.</summary>
        Queued,
        /// <summary>A clip playing.</summary>
        Playing,
        /// <summary>A clip playing, with a stop pending.</summary>
        Stopping,
        /// <summary>A clip recording (a take, or an overdub).</summary>
        Recording,
        /// <summary>A take waiting for its launch time.</summary>
        RecordQueued
    }

    /// <summary>
    /// The part of the session a controller shows: its ring (Width × Height slots from
    /// TrackOffset, SceneOffset), each slot's state and colour, the ring's tracks and scenes,
    /// and the transport. ReBuzz fills it; a script reads it.
    /// </summary>
    public sealed class SurfaceState
    {
        public SurfaceState(int width, int height)
        {
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            pads = new PadState[Width * Height];
            colors = new Rgb[Width * Height];
            TrackExists = new bool[Width];
            TrackArmed = new bool[Width];
            TrackPlaying = new bool[Width];
            SceneExists = new bool[Height];
            SceneHasClips = new bool[Height];
            SceneQueued = new bool[Height];
        }

        readonly PadState[] pads;
        readonly Rgb[] colors;

        public int Width { get; }
        public int Height { get; }

        /// <summary>The ring's first track and scene.</summary>
        public int TrackOffset { get; set; }
        public int SceneOffset { get; set; }

        /// <summary>The session's tracks and scenes.</summary>
        public int TrackCount { get; set; }
        public int SceneCount { get; set; }

        /// <summary>The transport plays.</summary>
        public bool Playing { get; set; }

        /// <summary>Session Record is on.</summary>
        public bool SessionRecording { get; set; }

        /// <summary>The controller's shift is held.</summary>
        public bool Shift { get; set; }

        /// <summary>Per ring column: a track is there; it is record-armed; a clip plays (or waits) on it.</summary>
        public bool[] TrackExists { get; }
        public bool[] TrackArmed { get; }
        public bool[] TrackPlaying { get; }

        /// <summary>Per ring row: a scene is there; it has clips; a clip in it waits to launch.</summary>
        public bool[] SceneExists { get; }
        public bool[] SceneHasClips { get; }
        public bool[] SceneQueued { get; }

        public PadState Pad(int x, int y) => In(x, y) ? pads[y * Width + x] : PadState.Off;

        public Rgb Color(int x, int y) => In(x, y) ? colors[y * Width + x] : Rgb.Black;

        public void SetPad(int x, int y, PadState state, Rgb color)
        {
            if (!In(x, y)) return;
            pads[y * Width + x] = state;
            colors[y * Width + x] = color;
        }

        bool In(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;

        /// <summary>Whether the ring can move that way (some of the session lies there).</summary>
        public bool CanMove(int dx, int dy) =>
            (dx < 0 && TrackOffset > 0) || (dx > 0 && TrackOffset + Width < TrackCount) ||
            (dy < 0 && SceneOffset > 0) || (dy > 0 && SceneOffset + Height < SceneCount);

        /// <summary>Back to all off.</summary>
        public void Clear()
        {
            Array.Clear(pads);
            Array.Clear(colors);
            Array.Clear(TrackExists);
            Array.Clear(TrackArmed);
            Array.Clear(TrackPlaying);
            Array.Clear(SceneExists);
            Array.Clear(SceneHasClips);
            Array.Clear(SceneQueued);
            Playing = SessionRecording = Shift = false;
        }
    }

    /// <summary>What a control of the controller does.</summary>
    public enum ControlKind
    {
        None,
        /// <summary>A slot of the ring (X, Y).</summary>
        Pad,
        /// <summary>A scene of the ring (Y).</summary>
        Scene,
        /// <summary>Stop a track's clip (X).</summary>
        TrackStop,
        /// <summary>Record-arm a track (X).</summary>
        TrackArm,
        StopAll,
        /// <summary>Move the ring (with shift: a page).</summary>
        Up, Down, Left, Right,
        /// <summary>Play or stop the transport.</summary>
        Play,
        /// <summary>Session Record on or off.</summary>
        SessionRecord,
        /// <summary>The script's shift (held for other meanings).</summary>
        Shift
    }

    /// <summary>A control pressed or released.</summary>
    public readonly record struct ControlEvent(ControlKind Kind, int X, int Y, bool Pressed, int Velocity)
    {
        public static readonly ControlEvent None = default;
    }

    /// <summary>
    /// The lights a script wants: for each light (a key the script chooses, e.g. its note or
    /// 128 + its CC), the MIDI message that sets it, and maybe one more sent after it. ReBuzz
    /// sends only the lights that changed since the last frame.
    /// </summary>
    public sealed class LedFrame
    {
        readonly Dictionary<int, long> leds = new Dictionary<int, long>();

        public void Set(int led, int message, int then = 0) => leds[led] = (uint)message | ((long)(uint)then << 32);

        public int Count => leds.Count;

        public bool TryGet(int led, out int message, out int then)
        {
            if (leds.TryGetValue(led, out long v))
            {
                message = (int)(uint)v;
                then = (int)(uint)(v >> 32);
                return true;
            }
            message = then = 0;
            return false;
        }

        public void Clear() => leds.Clear();

        /// <summary>The messages that take <paramref name="before"/> to <paramref name="after"/> (all of them when before is null).</summary>
        public static List<int> Changes(LedFrame before, LedFrame after)
        {
            var messages = new List<int>();
            foreach (var (led, value) in after.leds.OrderBy(kv => kv.Key))
            {
                if (before != null && before.leds.TryGetValue(led, out long old) && old == value) continue;
                messages.Add((int)(uint)value);
                int then = (int)(uint)(value >> 32);
                if (then != 0) messages.Add(then);
            }
            return messages;
        }

        public LedFrame Copy()
        {
            var copy = new LedFrame();
            foreach (var kv in leds) copy.leds[kv.Key] = kv.Value;
            return copy;
        }
    }

    /// <summary>
    /// A controller's script: which ports are its, what to send when it connects and leaves,
    /// what its messages mean, and what its lights show. Derive from it for a new controller.
    /// </summary>
    public abstract class ControllerScript
    {
        /// <summary>The controller's name, as menus show it.</summary>
        public abstract string Name { get; }

        /// <summary>The session ring's size: the grid's columns (tracks) and rows (scenes).</summary>
        public virtual int Width => 8;
        public virtual int Height => 8;

        /// <summary>How well a MIDI input's name fits this controller (0: not it; the best is used).</summary>
        public abstract int MatchInput(string port);

        /// <summary>How well a MIDI output's name fits this controller (0: not it).</summary>
        public abstract int MatchOutput(string port);

        /// <summary>Messages (SysEx, or short messages as 1-3 bytes) sent when the controller connects.</summary>
        public virtual IEnumerable<byte[]> Hello() => Array.Empty<byte[]>();

        /// <summary>Messages sent when it is let go (after its lights are turned off).</summary>
        public virtual IEnumerable<byte[]> Goodbye() => Array.Empty<byte[]>();

        /// <summary>What a short MIDI message from the controller means (status in the low byte); <paramref name="shift"/> is held.</summary>
        public abstract ControlEvent Read(int message, bool shift);

        /// <summary>The lights for a state of the session.</summary>
        public abstract void Render(SurfaceState state, LedFrame leds);

        // ------------------------------------------------------------------ helpers for scripts

        public static int Status(int message) => message & 0xFF;
        public static int Data1(int message) => (message >> 8) & 0x7F;
        public static int Data2(int message) => (message >> 16) & 0x7F;

        /// <summary>A note-on (or note-off as velocity 0) or a release.</summary>
        public static bool IsNote(int message) => (message & 0xE0) == 0x80;
        public static bool IsCc(int message) => (message & 0xF0) == 0xB0;

        /// <summary>A press: a note-on with velocity, or a CC above 0.</summary>
        public static bool IsPress(int message) =>
            ((message & 0xF0) == 0x90 && Data2(message) > 0) || ((message & 0xF0) == 0xB0 && Data2(message) > 0);

        public static int Note(int channel, int note, int value) => 0x90 | (channel & 0x0F) | ((note & 0x7F) << 8) | ((value & 0x7F) << 16);
        public static int Cc(int channel, int cc, int value) => 0xB0 | (channel & 0x0F) | ((cc & 0x7F) << 8) | ((value & 0x7F) << 16);

        /// <summary>A port name contains all of these (any case).</summary>
        public static bool Has(string port, params string[] parts) =>
            port != null && parts.All(p => port.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>
    /// The colour palette Novation's Launchpads (MK2 on) and Akai's APC40 mkII and APC mini mk2
    /// share for velocity colours: 0 off, 1-3 greys to white, then families of four (light,
    /// full, dim, dark) from red at 4 round the colour wheel. Push 2 gets the same entries
    /// written into its palette when it connects.
    /// </summary>
    public static class ControllerPalette
    {
        public const int Off = 0, DarkGrey = 1, Grey = 2, White = 3;

        /// <summary>The families' hues (degrees) and full colours, from index 4 on, four entries each.</summary>
        public static readonly (double Hue, Rgb Color)[] Families =
        {
            (0, new Rgb(255, 0, 0)), (28, new Rgb(255, 84, 0)), (55, new Rgb(255, 255, 0)), (80, new Rgb(136, 255, 0)),
            (120, new Rgb(0, 255, 0)), (140, new Rgb(0, 255, 85)), (160, new Rgb(0, 255, 153)), (180, new Rgb(0, 255, 255)),
            (200, new Rgb(0, 169, 255)), (215, new Rgb(0, 85, 255)), (240, new Rgb(0, 0, 255)), (270, new Rgb(135, 0, 255)),
            (300, new Rgb(255, 0, 255)), (330, new Rgb(255, 0, 85)),
        };

        /// <summary>A family's full entry (5 for red, 9 orange, 13 yellow, 21 green, 45 blue, …).</summary>
        public static int Full(int family) => 4 + 4 * family + 1;

        /// <summary>A family's dim entry.</summary>
        public static int Dim(int family) => 4 + 4 * family + 2;

        public static readonly int Red = Full(0), Orange = Full(1), Yellow = Full(2), Green = Full(4), Blue = Full(10);
        public static readonly int DimRed = Dim(0), DimGreen = Dim(4), DimWhite = DarkGrey;

        /// <summary>The nearest entry to a colour: a grey for unsaturated colours, else the nearest family's full (or dim) entry.</summary>
        public static int Nearest(Rgb c, bool dim = false)
        {
            double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max <= 0.05) return Off;
            double saturation = (max - min) / max;
            if (saturation < 0.25)
                return dim ? DarkGrey : max > 0.75 ? White : max > 0.4 ? Grey : DarkGrey;
            double hue;
            double d = max - min;
            if (max == r) hue = 60 * (((g - b) / d) % 6);
            else if (max == g) hue = 60 * ((b - r) / d + 2);
            else hue = 60 * ((r - g) / d + 4);
            if (hue < 0) hue += 360;
            int best = 0;
            double bestDistance = double.MaxValue;
            for (int f = 0; f < Families.Length; f++)
            {
                double distance = Math.Abs(hue - Families[f].Hue);
                distance = Math.Min(distance, 360 - distance);
                if (distance < bestDistance) { bestDistance = distance; best = f; }
            }
            return dim ? Dim(best) : Full(best);
        }

        /// <summary>The colour of an entry (for writing a palette: Push 2).</summary>
        public static Rgb ColorOf(int index)
        {
            switch (index)
            {
                case Off: return Rgb.Black;
                case DarkGrey: return new Rgb(28, 28, 28);
                case Grey: return new Rgb(124, 124, 124);
                case White: return Rgb.White;
            }
            int family = (index - 4) / 4, shade = (index - 4) % 4;
            if (family < 0 || family >= Families.Length) return Rgb.Black;
            var c = Families[family].Color;
            double k = shade switch { 0 => 1.0, 1 => 1.0, 2 => 0.3, _ => 0.1 };
            if (shade == 0)   // light: towards white
                return new Rgb((byte)(c.R + (255 - c.R) / 2), (byte)(c.G + (255 - c.G) / 2), (byte)(c.B + (255 - c.B) / 2));
            return new Rgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        }
    }
}
