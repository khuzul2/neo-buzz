// Fork (docs/specs/note-wires.md §11, CHD1): where a note insert sits relative to its instrument: left of it, in chain order, the last insert
// nearest the instrument. Pure arithmetic, used by the drop (where a new insert is put) and by the Machines view's Tracks mode (where the
// inserts are drawn); neither moves a stored position of a machine that was placed by a person.

namespace ReBuzz.Core.NoteWires
{
    internal static class NoteInsertLayout
    {
        /// <summary>The x of the insert at <paramref name="index"/> in a chain of <paramref name="count"/>: <paramref name="step"/> apart, the last one one step left of the instrument.</summary>
        public static float X(float instrumentX, int index, int count, float step) => instrumentX - step * (count - index);
    }
}
