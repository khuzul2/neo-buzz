// Fork (docs/specs/studio-mvp.md S4): what a pattern block shows of its pattern, as
// PatternElement.DrawPatternEvents does: small marks for its notes and parameter changes. Its
// default mode (PatternBoxEventHint = Midi) draws the pattern editor's MIDI events in note lanes;
// Detail draws each editor column as a lane. Patterns without an editor machine (studio-mvp.md
// §3.6) show their engine columns: MIDI notes as a small piano roll (a lane per pitch, as long as
// the note), parameter columns in detail.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Sequence
{
    internal enum PreviewKind : byte { Note, Off, Parameter }

    /// <summary>
    /// One mark: where in the pattern (0..1 of its length), its lane of how many, and its kind.
    /// <see cref="FixedHeight"/> marks (Detail) are 4 px high; the others fill 90% of their lane.
    /// A <see cref="Length"/> (0..1 of the pattern) makes it as long as its note; 0 is the
    /// editor's event width.
    /// </summary>
    internal readonly record struct PreviewMark(double X, int Lane, int Lanes, PreviewKind Kind, bool FixedHeight, double Length = 0);

    internal static class PatternPreview
    {
        const int MidiTimeBase = 960;       // PatternEditorMachineMIDIEvents' ticks per Buzz tick

        public static PreviewMark[] Of(IPattern pattern)
        {
            if (pattern == null || pattern.Length <= 0) return Array.Empty<PreviewMark>();
            try
            {
                if (pattern.Machine is MachineCore mc && mc.EditorMachine != null)
                {
                    var midi = pattern.PatternEditorMachineMIDIEvents;
                    if (midi != null && midi.Length >= 2)
                        return Midi(Pairs(midi), pattern.Length, Math.Max(1, pattern.Machine.TrackCount));
                    var columns = pattern.PatternEditorMachineEvents?.ToList();
                    if (columns != null && columns.Count > 0)
                        return Detail(columns.Select(c => (c.Parameter, c.Events)).ToList(), pattern.Length);
                }
                return EngineColumns(pattern);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NullReferenceException || e is KeyNotFoundException)
            {
                return Array.Empty<PreviewMark>();
            }
        }

        static List<(int Time, int Message)> Pairs(int[] me)
        {
            var list = new List<(int, int)>(me.Length / 2);
            for (int i = 0; i + 1 < me.Length; i += 2) list.Add((me[i], me[i + 1]));
            return list;
        }

        /// <summary>
        /// PatternBoxEventHintType.Midi: note-ons take the leftmost free lane of <paramref name="lanes"/>,
        /// note-offs end their note's lane (and are drawn there), other messages show where they fall.
        /// </summary>
        static PreviewMark[] Midi(List<(int Time, int Message)> events, int length, int lanes)
        {
            var marks = new List<PreviewMark>();
            var held = new int[lanes];
            int pos = 0;
            foreach (var (time, message) in events)
            {
                int command = message & 0xF0;
                int data1 = (message >> 8) & 0x7F;
                for (int i = 0; i < lanes; i++)
                    if (held[i] == 0) { pos = i; break; }
                var kind = command == 0x80 ? PreviewKind.Off : command == 0x90 ? PreviewKind.Note : PreviewKind.Parameter;
                if (command == 0x90)
                {
                    bool found = false;
                    for (int i = 0; i < lanes; i++)
                        if (held[(pos + i) % lanes] == 0)
                        {
                            pos = (pos + i) % lanes;
                            found = true;
                            break;
                        }
                    if (!found) pos = (pos + 1) % lanes;
                    held[pos] = data1;
                }
                else if (command == 0x80)
                {
                    bool found = false;
                    for (int i = 0; i < lanes; i++)
                        if (held[i] == data1)
                        {
                            pos = i;
                            found = true;
                            held[i] = 0;
                            break;
                        }
                    if (!found) continue;       // an off without its note isn't drawn
                }
                marks.Add(new PreviewMark(time / (double)MidiTimeBase / length, pos, lanes, kind, false));
            }
            return marks.ToArray();
        }

        /// <summary>PatternBoxEventHintType.Detail: a lane per column; notes, note-offs and other values.</summary>
        static PreviewMark[] Detail(List<(IParameter Parameter, IEnumerable<PatternEvent> Events)> columns, int length)
        {
            var marks = new List<PreviewMark>();
            for (int i = 0; i < columns.Count; i++)
            {
                bool note = columns[i].Parameter?.Type == ParameterType.Note;
                foreach (var pe in columns[i].Events)
                {
                    var kind = !note ? PreviewKind.Parameter : pe.Value == BuzzNote.Off ? PreviewKind.Off : PreviewKind.Note;
                    marks.Add(new PreviewMark(pe.Time / (double)PatternEvent.TimeBase / length, i, columns.Count, kind, true));
                }
            }
            return marks.ToArray();
        }

        /// <summary>At most this many pitch lanes; a wider range shares them.</summary>
        const int MaxPitchLanes = 16;

        /// <summary>The engine's own columns: MIDI notes as a piano roll (high notes on top), else the parameter columns in detail.</summary>
        static PreviewMark[] EngineColumns(IPattern pattern)
        {
            int end = pattern.Length * PatternEvent.TimeBase;
            var notes = new List<(int Time, int Duration, int Pitch)>();
            foreach (var column in pattern.Columns.Where(c => c.Type == PatternColumnType.MIDI))
            {
                foreach (var e in column.GetEvents(0, end))
                {
                    int status = e.Value & 0xF0;
                    int velocity = (e.Value >> 16) & 0x7F;
                    if (status == 0x90 && velocity > 0) notes.Add((e.Time, Math.Max(1, e.Duration), (e.Value >> 8) & 0x7F));
                }
            }
            if (notes.Count > 0)
            {
                var pitches = notes.Select(n => n.Pitch).Distinct().OrderByDescending(p => p).ToList();
                int high = pitches[0], low = pitches[^1];
                int lanes = Math.Min(MaxPitchLanes, pitches.Count);
                int LaneOf(int pitch) => pitches.Count <= MaxPitchLanes
                    ? pitches.IndexOf(pitch)
                    : Math.Min(lanes - 1, (int)((high - pitch) / (double)(high - low + 1) * lanes));
                double length = pattern.Length * (double)PatternEvent.TimeBase;
                return notes.Select(n => new PreviewMark(n.Time / length, LaneOf(n.Pitch), lanes, PreviewKind.Note, false, n.Duration / length)).ToArray();
            }
            var columns = pattern.Columns.Where(c => c.Type == PatternColumnType.Parameter)
                .Select(c => (c.Parameter, (IEnumerable<PatternEvent>)c.GetEvents(0, end).ToList())).ToList();
            return columns.Count > 0 ? Detail(columns, pattern.Length) : Array.Empty<PreviewMark>();
        }
    }
}
