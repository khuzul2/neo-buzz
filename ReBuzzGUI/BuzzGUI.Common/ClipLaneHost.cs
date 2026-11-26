// Fork (docs/specs/clips.md §5.5, §6, T2): the seam between a tracker and the vertical envelope lanes that sit beside its parameter columns.
// The tracker (the Modern Pattern Editor's Avalonia view) is a machine loaded in its own context and cannot see the studio's types; the studio's
// lane control (which edits the clip store through the song's history) cannot see the editor's. They meet here, in the assembly both share:
// the editor describes the rows of one column (IClipRowAxis) and asks the provider for a control to put beside it; the studio sets the provider.
// Nothing here draws or edits; no UI toolkit is referenced (the control is handed over as an object, an Avalonia Control).

using System;
using BuzzGUI.Interfaces;

namespace BuzzGUI.Common
{
    /// <summary>
    /// The vertical axis of one parameter column of a tracker: where a clip time is, in pixels from the top of the clip's rows (the lane scrolls
    /// with the grid, so these do not change with the scroll), which time a row starts at, and which part of it is in view. Times are the clip's
    /// (1/240 tick), as <see cref="ClipEvent.Time"/>.
    /// </summary>
    public interface IClipRowAxis
    {
        /// <summary>The clip's length in 1/240 ticks.</summary>
        int Length { get; }

        /// <summary>The height of all the clip's rows, in pixels.</summary>
        double Extent { get; }

        /// <summary>The y of a clip time (a time past the end is at the end).</summary>
        double Y(int time);

        /// <summary>The clip time at a y, not snapped; 0 or more and at most <see cref="Length"/>.</summary>
        int TimeAt(double y);

        /// <summary>The time of the row start nearest a time, in this column's own rows (a beat can hold more or fewer rows than another).</summary>
        int SnapToRow(int time);

        /// <summary>Puts into <paramref name="into"/> (cleared first) the start time of each row of this column from <paramref name="from"/> up to but not including <paramref name="to"/>.</summary>
        void RowStarts(int from, int to, System.Collections.Generic.List<int> into);

        /// <summary>The part of the rows in view: y of the top and of the bottom, as <see cref="Y"/> counts.</summary>
        (double Top, double Bottom) View { get; }

        /// <summary>The play position in clip time, or -1 when the clip does not play.</summary>
        int PlayPosition { get; }

        /// <summary>One beat's length in 1/240 ticks, and the beats in a bar (for the rules).</summary>
        int BeatUnits { get; }

        int BarUnits { get; }

        /// <summary>The view moved: the scroll, the play position. A lane draws again from what it has read.</summary>
        event Action Changed;

        /// <summary>The clip's events or the rows changed (a value typed, an undo, the agent, a new clip length or zoom): a lane reads the clip again.</summary>
        event Action DataChanged;
    }

    /// <summary>What makes the lanes: the studio. When none is set (the engine's tests, the portable build without the studio) the tracker shows no lane toggles.</summary>
    public interface IClipLaneProvider
    {
        /// <summary>Whether a column of this parameter and track can have a lane (a state parameter; a note column, a trigger or an input has none).</summary>
        bool Supports(IClip clip, IParameter parameter, int track);

        /// <summary>The lane for a column: an Avalonia Control the tracker puts beside the column and sizes to its strip and the clip's rows.</summary>
        object Create(IClip clip, IParameter parameter, int track, IClipRowAxis axis);
    }

    public static class ClipLaneHost
    {
        public static IClipLaneProvider Provider { get; set; }
    }
}
