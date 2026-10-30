#nullable disable
// Session View MIDI mapping (fork addition). See docs/specs/session-view.md, 0.2.
//
// Which MIDI controls (notes, CCs) drive which session targets: explicit bindings made with
// MIDI learn, plus an optional note grid that maps a block of notes onto a block of slots
// (for pad controllers without a dedicated script). Pure data with no ReBuzz types, so it can
// be unit tested on its own (ReBuzz.SessionTests links this file). Treat a published map as
// immutable: SessionMidi edits a Clone and swaps it in, because the MIDI thread reads it.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace ReBuzz.Core.Session
{
    internal enum MidiControlType { Note, ControlChange }

    /// <summary>A note or controller number on a MIDI channel (0-15).</summary>
    internal readonly record struct MidiControl(MidiControlType Type, int Channel, int Number)
    {
        static readonly string[] NoteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };

        /// <summary>
        /// Decodes a short MIDI message as packed by ReBuzz (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16).
        /// Note on with velocity 0 counts as note off. <paramref name="isPress"/> is true for
        /// note on and for CC values above 0.
        /// </summary>
        public static bool TryDecode(int data, out MidiControl control, out int value, out bool isPress)
        {
            int status = data & 0xFF;
            int data1 = (data >> 8) & 0x7F;
            int data2 = (data >> 16) & 0x7F;
            int channel = status & 0x0F;
            switch (status & 0xF0)
            {
                case 0x90:
                    control = new MidiControl(MidiControlType.Note, channel, data1);
                    value = data2;
                    isPress = data2 > 0;
                    return true;
                case 0x80:
                    control = new MidiControl(MidiControlType.Note, channel, data1);
                    value = 0;
                    isPress = false;
                    return true;
                case 0xB0:
                    control = new MidiControl(MidiControlType.ControlChange, channel, data1);
                    value = data2;
                    isPress = data2 > 0;
                    return true;
                default:
                    control = default;
                    value = 0;
                    isPress = false;
                    return false;
            }
        }

        public static string NoteName(int note) =>
            NoteNames[((note % 12) + 12) % 12] + (note / 12 - 1).ToString(CultureInfo.InvariantCulture);

        public override string ToString() =>
            (Type == MidiControlType.Note ? "Note " + NoteName(Number) : "CC " + Number.ToString(CultureInfo.InvariantCulture)) +
            ", ch " + (Channel + 1).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// What a MIDI control can drive. Track and scene are grid positions (a controller layout),
    /// so they don't follow scenes being inserted or removed.
    /// </summary>
    internal enum SessionTarget
    {
        /// <summary>Launch the slot's clip, or stop the track if the slot is empty.</summary>
        Slot,
        /// <summary>Launch a scene row.</summary>
        Scene,
        /// <summary>Stop a track.</summary>
        TrackStop,
        StopAll,
        /// <summary>Launch quantization: a CC picks a value across its range, a note cycles.</summary>
        Quantization,
        /// <summary>Start/stop the transport.</summary>
        Transport,
        /// <summary>Move the note grid one row up (towards scene 1).</summary>
        GridUp,
        /// <summary>Move the note grid one row down.</summary>
        GridDown,
        /// <summary>Move the note grid one track left.</summary>
        GridLeft,
        /// <summary>Move the note grid one track right.</summary>
        GridRight,
        /// <summary>Undo the last launch (0.6).</summary>
        UndoLaunch,
        /// <summary>Tap tempo (0.6).</summary>
        TapTempo,
        /// <summary>Slow down while held (0.6).</summary>
        NudgeDown,
        /// <summary>Speed up while held (0.6).</summary>
        NudgeUp,
        /// <summary>Crossfader position: a CC across its range (0.6).</summary>
        Crossfader,
        /// <summary>Switch follow actions on or off (0.6).</summary>
        FollowActions
    }

    internal readonly record struct MidiBinding(MidiControl Control, SessionTarget Target, int Track, int Scene)
    {
        public bool IsFor(SessionTarget target, int track, int scene) =>
            Target == target && Track == Normalize(target, track, true) && Scene == Normalize(target, scene, false);

        /// <summary>Positions that don't apply to a target are stored as 0.</summary>
        public static int Normalize(SessionTarget target, int index, bool isTrack) => target switch
        {
            SessionTarget.Slot => index,
            SessionTarget.Scene => isTrack ? 0 : index,
            SessionTarget.TrackStop => isTrack ? index : 0,
            _ => 0
        };
    }

    /// <summary>
    /// A block of notes mapped onto a block of slots: note <see cref="FirstNote"/> + i goes to
    /// column i % <see cref="Columns"/> and row i / Columns, counted from the origin slot.
    /// With <see cref="BottomUp"/> (pads like the MPC layout) the first note is the bottom-left
    /// pad, so rows count upwards on the controller and downwards in the grid.
    /// </summary>
    internal sealed class NoteGrid
    {
        /// <summary>MIDI channel 0-15, or -1 for any channel.</summary>
        public int Channel { get; set; } = -1;
        public int FirstNote { get; set; } = 36;
        public int Columns { get; set; } = 4;
        public int Rows { get; set; } = 4;
        public bool BottomUp { get; set; } = true;
        public int OriginTrack { get; set; }
        public int OriginScene { get; set; }

        public bool TryMap(MidiControl control, out int track, out int scene)
        {
            track = scene = -1;
            if (control.Type != MidiControlType.Note) return false;
            if (Channel >= 0 && control.Channel != Channel) return false;
            int i = control.Number - FirstNote;
            if (Columns < 1 || Rows < 1 || i < 0 || i >= Columns * Rows) return false;
            int column = i % Columns;
            int row = i / Columns;
            track = OriginTrack + column;
            scene = OriginScene + (BottomUp ? Rows - 1 - row : row);
            return true;
        }

        public NoteGrid Clone() => (NoteGrid)MemberwiseClone();
    }

    internal sealed class SessionMidiMap
    {
        public List<MidiBinding> Bindings { get; } = new List<MidiBinding>();
        public NoteGrid Grid { get; set; }

        public bool IsEmpty => Bindings.Count == 0 && Grid == null;

        /// <summary>Binds a control to a target. A control drives one target and a target has one control.</summary>
        public void Bind(MidiControl control, SessionTarget target, int track, int scene)
        {
            track = MidiBinding.Normalize(target, track, true);
            scene = MidiBinding.Normalize(target, scene, false);
            Bindings.RemoveAll(b => b.Control == control || b.IsFor(target, track, scene));
            Bindings.Add(new MidiBinding(control, target, track, scene));
        }

        public bool Unbind(SessionTarget target, int track, int scene) =>
            Bindings.RemoveAll(b => b.IsFor(target, track, scene)) > 0;

        public MidiBinding? Find(SessionTarget target, int track, int scene)
        {
            foreach (var b in Bindings)
                if (b.IsFor(target, track, scene))
                    return b;
            return null;
        }

        /// <summary>
        /// The target a control drives: an explicit binding first, else the note grid (slots).
        /// </summary>
        public bool TryResolve(MidiControl control, out SessionTarget target, out int track, out int scene)
        {
            foreach (var b in Bindings)
            {
                if (b.Control == control)
                {
                    target = b.Target;
                    track = b.Track;
                    scene = b.Scene;
                    return true;
                }
            }
            if (Grid != null && Grid.TryMap(control, out track, out scene))
            {
                target = SessionTarget.Slot;
                return true;
            }
            target = default;
            track = scene = -1;
            return false;
        }

        /// <summary>Whether the map uses this control (so it shouldn't also reach machines).</summary>
        public bool Handles(MidiControl control) => TryResolve(control, out _, out _, out _);

        public SessionMidiMap Clone()
        {
            var copy = new SessionMidiMap { Grid = Grid?.Clone() };
            copy.Bindings.AddRange(Bindings);
            return copy;
        }

        // ------------------------------------------------------------------ persistence

        public XElement ToXml() =>
            new XElement("Midi",
                Bindings.Select(b => new XElement("Bind",
                    new XAttribute("type", b.Control.Type == MidiControlType.Note ? "note" : "cc"),
                    new XAttribute("channel", b.Control.Channel + 1),
                    new XAttribute("number", b.Control.Number),
                    new XAttribute("target", b.Target.ToString()),
                    new XAttribute("track", b.Track),
                    new XAttribute("scene", b.Scene))),
                Grid == null ? null : new XElement("NoteGrid",
                    new XAttribute("channel", Grid.Channel < 0 ? "any" : (Grid.Channel + 1).ToString(CultureInfo.InvariantCulture)),
                    new XAttribute("firstNote", Grid.FirstNote),
                    new XAttribute("columns", Grid.Columns),
                    new XAttribute("rows", Grid.Rows),
                    new XAttribute("bottomUp", Grid.BottomUp ? "true" : "false"),
                    new XAttribute("originTrack", Grid.OriginTrack),
                    new XAttribute("originScene", Grid.OriginScene)));

        /// <summary>Reads a map; skips entries it doesn't understand. Never returns null.</summary>
        public static SessionMidiMap FromXml(XElement e)
        {
            var map = new SessionMidiMap();
            if (e == null) return map;
            foreach (var b in e.Elements("Bind"))
            {
                var type = (string)b.Attribute("type") == "cc" ? MidiControlType.ControlChange : MidiControlType.Note;
                int channel = Int(b, "channel", 1) - 1;
                int number = Int(b, "number", -1);
                if (channel < 0 || channel > 15 || number < 0 || number > 127) continue;
                if (!Enum.TryParse<SessionTarget>((string)b.Attribute("target"), out var target)) continue;
                map.Bindings.Add(new MidiBinding(new MidiControl(type, channel, number), target,
                    Math.Max(0, Int(b, "track", 0)), Math.Max(0, Int(b, "scene", 0))));
            }
            var g = e.Element("NoteGrid");
            if (g != null)
            {
                string ch = (string)g.Attribute("channel");
                map.Grid = new NoteGrid
                {
                    Channel = ch == null || ch == "any" ? -1 : Math.Clamp(Int(g, "channel", 0) - 1, -1, 15),
                    FirstNote = Math.Clamp(Int(g, "firstNote", 36), 0, 127),
                    Columns = Math.Clamp(Int(g, "columns", 4), 1, 16),
                    Rows = Math.Clamp(Int(g, "rows", 4), 1, 16),
                    BottomUp = (string)g.Attribute("bottomUp") != "false",
                    OriginTrack = Math.Max(0, Int(g, "originTrack", 0)),
                    OriginScene = Math.Max(0, Int(g, "originScene", 0)),
                };
            }
            return map;
        }

        public static SessionMidiMap Parse(string xml)
        {
            if (string.IsNullOrWhiteSpace(xml)) return new SessionMidiMap();
            try
            {
                return FromXml(XElement.Parse(xml));
            }
            catch (System.Xml.XmlException)
            {
                return new SessionMidiMap();
            }
        }

        static int Int(XElement e, string name, int fallback) =>
            int.TryParse((string)e.Attribute(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }
}
