// Fork (docs/specs/daw-modes.md AR2): the controllers of a pattern's engine MIDI column, the CC lanes under the piano roll. Pitch
// bend and control changes sit in the column beside the notes (session takes and the agent keep them; until now no view showed them).
// A controller point is one event; an edit is one undo step of the song's history (the notes' way, MidiColumnNotes).

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    /// <summary>A control change (controller 0-127, value 0-127) or a pitch bend (controller <see cref="PitchBend"/>, value 0-16383).</summary>
    internal readonly record struct ControllerPoint(int Time, int Controller, int Value, int Channel = 0)
    {
        public const int PitchBend = 128;
        public const int BendCentre = 8192;

        public int Max => Controller == PitchBend ? 16383 : 127;

        /// <summary>The MIDI column event it is.</summary>
        public (int Time, int Value, int Duration) ToEvent()
        {
            int ch = Channel & 0x0F;
            if (Controller == PitchBend)
            {
                int v = Math.Clamp(Value, 0, 16383);
                return (Time, 0xE0 | ch | ((v & 0x7F) << 8) | (((v >> 7) & 0x7F) << 16), 0);
            }
            return (Time, 0xB0 | ch | ((Controller & 0x7F) << 8) | ((Math.Clamp(Value, 0, 127) & 0x7F) << 16), 0);
        }

        /// <summary>The point a MIDI column event is, if it is a control change or a pitch bend.</summary>
        public static bool TryFrom((int Time, int Value, int Duration) e, out ControllerPoint point)
        {
            int status = e.Value & 0xFF, d1 = (e.Value >> 8) & 0x7F, d2 = (e.Value >> 16) & 0x7F;
            switch (status & 0xF0)
            {
                case 0xB0:
                    point = new ControllerPoint(e.Time, d1, d2, status & 0x0F);
                    return true;
                case 0xE0:
                    point = new ControllerPoint(e.Time, PitchBend, d1 | (d2 << 7), status & 0x0F);
                    return true;
                default:
                    point = default;
                    return false;
            }
        }

        /// <summary>The usual names of the controllers a lane offers first.</summary>
        public static string NameOf(int controller) => controller switch
        {
            PitchBend => "Pitch bend",
            1 => "Mod wheel (CC 1)",
            7 => "Volume (CC 7)",
            10 => "Pan (CC 10)",
            11 => "Expression (CC 11)",
            64 => "Sustain (CC 64)",
            74 => "Cutoff (CC 74)",
            _ => $"CC {controller}",
        };

        /// <summary>The controllers every machine's lane list offers, besides those the column already holds.</summary>
        public static readonly int[] Usual = { 1, 7, 10, 11, 64, PitchBend };
    }

    /// <summary>The controllers of one pattern's MIDI column, read and edited on the song's history.</summary>
    internal sealed class MidiControllers
    {
        readonly ReBuzzCore buzz;
        readonly MidiColumnNotes notes;

        public MidiControllers(ReBuzzCore buzz, IPattern pattern)
        {
            this.buzz = buzz;
            notes = new MidiColumnNotes(buzz, pattern);
            Pattern = pattern;
        }

        public IPattern Pattern { get; }

        /// <summary>The pattern's length in pattern time (1/240 tick).</summary>
        public int Length => notes.Length;

        /// <summary>Null when the points can be edited now, else why not.</summary>
        public string WhyNotEditable => notes.WhyNotEditable;

        /// <summary>Every control change and pitch bend of the column, in time order.</summary>
        public List<ControllerPoint> Points()
        {
            var points = new List<ControllerPoint>();
            if (!notes.IsLive) return points;
            var column = SessionLayout.ColumnFor(Pattern, ColumnKey.Midi, create: false);
            if (column == null) return points;
            foreach (var e in column.GetEvents(int.MinValue, int.MaxValue))
                if (ControllerPoint.TryFrom((e.Time, e.Value, e.Duration), out var p)) points.Add(p);
            points.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Controller.CompareTo(b.Controller));
            return points;
        }

        /// <summary>The controllers the column holds, lowest first (pitch bend last).</summary>
        public List<int> Present() => Points().Select(p => p.Controller).Distinct().OrderBy(c => c).ToList();

        /// <summary>One edit, one undo step: points go, points come. Null when done, else why not; nothing changes then.</summary>
        public string Edit(IReadOnlyList<ControllerPoint> removed, IReadOnlyList<ControllerPoint> added)
        {
            if (WhyNotEditable is string why) return why;
            removed ??= Array.Empty<ControllerPoint>();
            added ??= Array.Empty<ControllerPoint>();
            if (removed.Count == 0 && added.Count == 0) return null;
            if (removed.SequenceEqual(added)) return null;
            buzz.SongCore.ActionStack.Do(new ColumnEventsAction(buzz, Pattern,
                new[] { new ColumnEventsAction.Change(ColumnKey.Midi, removed.Select(ToEvent).ToArray(), added.Select(ToEvent).ToArray()) }));
            buzz.SetModifiedFlag();
            return null;
        }

        static PatternEvent ToEvent(ControllerPoint p)
        {
            var (time, value, duration) = p.ToEvent();
            return new PatternEvent(time, value, duration);
        }
    }
}
