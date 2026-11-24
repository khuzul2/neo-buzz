// Fork (docs/specs/note-wires.md): the host side of note wires for a machine's own menu and editor, and the migration of the old single target.
namespace BuzzGUI.Interfaces
{
    /// <summary>
    /// The host's note wires for a machine's own menu and editor (implemented by the core: <c>machine.Graph.Buzz as INoteWireHost</c>). Each change is one undo step.
    /// </summary>
    public interface INoteWireHost
    {
        /// <summary>The machines <paramref name="source"/> sends notes to, by name.</summary>
        System.Collections.Generic.IReadOnlyList<string> NoteTargetsOf(IMachine source);

        /// <summary>The machines that take notes (other than <paramref name="source"/>) a wire could go to.</summary>
        System.Collections.Generic.IReadOnlyList<string> NoteTargetCandidates(IMachine source);

        /// <summary>Adds a wire. Returns null, or the reason it was refused (a loop, a machine that takes no notes).</summary>
        string ConnectNotes(IMachine source, string target);

        /// <summary>Removes a wire; false when there was none.</summary>
        bool DisconnectNotes(IMachine source, string target);
    }

    /// <summary>
    /// A machine that used to keep one note target of its own (Neo Voice to MIDI, Neo Audio to MIDI, Neo Plug's MIDI out): a song saved that way opens with the
    /// target here, and the core turns it into a note wire (without an undo step) and takes it away so it is saved as a wire from then on.
    /// </summary>
    public interface INoteLegacyTarget
    {
        /// <summary>The target name the saved song carried (once), or null.</summary>
        string TakeLegacyTarget();
    }
}
