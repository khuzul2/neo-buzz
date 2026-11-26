// Fork (docs/specs/studio-mvp.md S4): what a clip block shows of its clip, as
// ClipElement.DrawClipEvents does: small marks for its notes and parameter changes. Its
// default mode (ClipBoxEventHint = Midi) draws the clip editor's MIDI events in note lanes;
// Detail draws each editor column as a lane. Clips without an editor machine (studio-mvp.md
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
    /// One mark: where in the clip (0..1 of its length), its lane of how many, and its kind.
    /// <see cref="FixedHeight"/> marks (Detail) are 4 px high; the others fill 90% of their lane.
    /// A <see cref="Length"/> (0..1 of the clip) makes it as long as its note; 0 is the
    /// editor's event width.
    /// </summary>
    internal readonly record struct PreviewMark(double X, int Lane, int Lanes, PreviewKind Kind, bool FixedHeight, double Length = 0);

    internal static class ClipPreview
    {
        public static PreviewMark[] Of(IClip clip)
        {
            if (clip == null || clip.Length <= 0) return Array.Empty<PreviewMark>();
            try
            {
                if (clip.Machine is MachineCore mc && mc.EditorMachine != null)
                {
                    var columns = clip.ClipEditorMachineEvents?.ToList();
                    if (columns != null && columns.Count > 0)
                        return Detail(columns.Select(c => (c.Parameter, c.Events)).ToList(), clip.Length);
                }
                return EngineColumns(clip);
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is NullReferenceException || e is KeyNotFoundException)
            {
                return Array.Empty<PreviewMark>();
            }
        }

        /// <summary>ClipBoxEventHintType.Detail: a lane per column; notes, note-offs and other values.</summary>
        static PreviewMark[] Detail(List<(IParameter Parameter, IEnumerable<ClipEvent> Events)> columns, int length)
        {
            var marks = new List<PreviewMark>();
            for (int i = 0; i < columns.Count; i++)
            {
                bool note = columns[i].Parameter?.Type == ParameterType.Note;
                foreach (var pe in columns[i].Events)
                {
                    var kind = !note ? PreviewKind.Parameter : pe.Value == BuzzNote.Off ? PreviewKind.Off : PreviewKind.Note;
                    marks.Add(new PreviewMark(pe.Time / (double)ClipEvent.TimeBase / length, i, columns.Count, kind, true));
                }
            }
            return marks.ToArray();
        }

        /// <summary>At most this many pitch lanes; a wider range shares them.</summary>
        const int MaxPitchLanes = 16;

        /// <summary>The engine's own columns: MIDI notes as a piano roll (high notes on top), else the parameter columns in detail.</summary>
        static PreviewMark[] EngineColumns(IClip clip)
        {
            int end = clip.Length * ClipEvent.TimeBase;
            var notes = new List<(int Time, int Duration, int Pitch)>();
            foreach (var column in clip.Columns.Where(c => c.Type == ClipColumnType.MIDI))
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
                double length = clip.Length * (double)ClipEvent.TimeBase;
                return notes.Select(n => new PreviewMark(n.Time / length, LaneOf(n.Pitch), lanes, PreviewKind.Note, false, n.Duration / length)).ToArray();
            }
            var columns = clip.Columns.Where(c => c.Type == ClipColumnType.Parameter)
                .Select(c => (c.Parameter, (IEnumerable<ClipEvent>)c.GetEvents(0, end).ToList())).ToList();
            return columns.Count > 0 ? Detail(columns, clip.Length) : Array.Empty<PreviewMark>();
        }
    }
}
