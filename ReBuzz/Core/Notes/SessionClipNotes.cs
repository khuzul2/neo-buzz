// Fork (docs/specs/daw-modes.md S4): a session clip's notes, as the Session View's piano roll
// edited them before (session-view.md 0.7.1): its engine MIDI column through SessionLayout, each
// edit one undo step of the session's own history.

using System.Collections.Generic;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal sealed class SessionClipNotes : NoteSourceBase
    {
        readonly SessionLayout layout;
        readonly IPattern original;

        public SessionClipNotes(SessionLayout layout, IPattern clip)
        {
            this.layout = layout;
            original = clip;
        }

        /// <summary>The clip, as it stands now: the session's undo may have made it again (0.7.1 hardening).</summary>
        public override IPattern Pattern => layout.Current(original);

        /// <summary>Whether the clip is in the song (not deleted, or brought back by an undo).</summary>
        public override bool IsLive => Pattern is PatternCore p && !p.IsDeleted && layout.IsInSong(p.Machine);

        public override List<ClipNote> Notes() => IsLive ? layout.NotesOf(Pattern) : new List<ClipNote>();

        public override string WhyNotEditable =>
            !IsLive ? "The clip was deleted: undo in the Session View brings it back."
            : layout.IsRecordingClip(Pattern) ? "Recording: edits wait until it stops."
            : null;

        protected override string Apply(IReadOnlyList<ClipNote> removed, IReadOnlyList<ClipNote> added)
        {
            layout.EditNotes(Pattern, removed, added);
            return null;
        }

        public override void Preview(int note, int velocity, bool on) => layout.PreviewNote(Pattern, note, velocity, on);

        protected override void DoUndo() => layout.Undo();

        protected override void DoRedo() => layout.Redo();
    }
}
