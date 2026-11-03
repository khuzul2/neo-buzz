// Fork (docs/specs/daw-modes.md S4): where a piano roll's notes come from. The Session View's
// rolls (WPF and the studio) had SessionLayout's calls built in; the arrangement and the Pattern
// tab will show the same control on other stores. A source is one pattern's notes in one store:
//  - SessionClipNotes: a session clip's MIDI column, on the Session View's own undo history;
//  - MidiColumnNotes: a pattern's engine MIDI column, on the song's undo history;
//  - TrackerColumnNotes: a classic machine's Note columns, edited with tracker rules (D3 A:
//    a note per track, so no more notes at once than the machine has tracks);
//  - EditorPreviewNotes: what a pattern editor exports (PatternEditorMachineMIDIEvents), read only.

using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal interface INoteSource
    {
        /// <summary>The pattern, as it stands now (an undo may have made it again); null if there is none.</summary>
        IPattern Pattern { get; }

        /// <summary>Whether the pattern is in the song (not deleted). Without it there are no notes.</summary>
        bool IsLive { get; }

        /// <summary>The notes, in time order (empty when the pattern isn't live).</summary>
        List<ClipNote> Notes();

        /// <summary>The pattern's length in pattern time (1/240 tick).</summary>
        int Length { get; }

        /// <summary>Null when the notes can be edited now, else why not, in words for the user.</summary>
        string WhyNotEditable { get; }

        /// <summary>
        /// One edit, one undo step: <paramref name="removed"/> (notes as <see cref="Notes"/> gave
        /// them) go, <paramref name="added"/> come. Null when done (or nothing to do), else why not;
        /// nothing changes then.
        /// </summary>
        string Edit(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added);

        /// <summary>Plays (or ends) a note on the pattern's machine, as the keys or a note are clicked.</summary>
        void Preview(int note, int velocity, bool on);

        /// <summary>Undo and redo in the history the edits go to.</summary>
        void Undo();

        void Redo();

        /// <summary>Raised after this source's own edits, undos and redos (hosts poll for changes made elsewhere).</summary>
        event Action Changed;
    }

    /// <summary>What every source does the same way.</summary>
    internal abstract class NoteSourceBase : INoteSource
    {
        public abstract IPattern Pattern { get; }

        public virtual bool IsLive => Pattern is PatternCore p && !p.IsDeleted && p.Machine?.Graph?.Buzz?.Song?.Machines.Contains(p.Machine) == true;

        public abstract List<ClipNote> Notes();

        public virtual int Length => SessionLayout.ClipLengthOf(Pattern);

        public abstract string WhyNotEditable { get; }

        public string Edit(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            if (WhyNotEditable is string why) return why;
            removed ??= Array.Empty<ClipNote>();
            added ??= Array.Empty<ClipNote>();
            if (removed.Count == 0 && added.Count == 0) return null;
            if (removed.Count == added.Count && System.Linq.Enumerable.SequenceEqual(removed, added)) return null;
            string refused = Apply(removed, added);
            if (refused == null) Changed?.Invoke();
            return refused;
        }

        /// <summary>The edit, checked as editable and not empty: null when made, else why not.</summary>
        protected abstract string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added);

        public abstract void Preview(int note, int velocity, bool on);

        public void Undo()
        {
            DoUndo();
            Changed?.Invoke();
        }

        public void Redo()
        {
            DoRedo();
            Changed?.Invoke();
        }

        protected abstract void DoUndo();

        protected abstract void DoRedo();

        public event Action Changed;

        /// <summary>A MIDI note-on (or note-off) sent to the machine, as SessionLayout.PreviewNote sends it.</summary>
        protected static void SendMidiNote(ReBuzzCore buzz, IPattern pattern, int note, int velocity, bool on)
        {
            if (pattern?.Machine is not MachineCore machine || machine.DLL == null || machine.DLL.IsMissing || machine.DLL.IsCrashed || !machine.Ready) return;
            int data = on
                ? 0x90 | ((note & 0x7F) << 8) | ((Math.Clamp(velocity, 1, 127) & 0x7F) << 16)
                : 0x80 | ((note & 0x7F) << 8);
            buzz.MachineManager.SendMidiInput(machine, data, false);
        }
    }
}
