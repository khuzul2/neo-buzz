// Fork (docs/specs/studio-mvp.md S4, agent-knowledge.md K3b): removing a track, as an undo step. Moved here from the studio's SequenceEdits
// so the agent's arrangement_lane removes a lane with the same action (BuzzGUI.Common's own copy is in its Windows build only).

using System;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>Removes a track; undo brings it back with its events (BuzzGUI.Common DeleteTrackAction).</summary>
    internal sealed class DeleteTrackAction : MachineAction
    {
        readonly ISong song;
        readonly int seqIndex;
        Tuple<int, EventRef>[] events;
        ISequence deleted;

        public DeleteTrackAction(ISequence s) : base(s.Machine)
        {
            song = s.Machine.Graph.Buzz.Song;
            seqIndex = song.Sequences.IndexOf(s);
        }

        protected override void DoAction()
        {
            if (seqIndex >= song.Sequences.Count) return;
            var seq = song.Sequences[seqIndex];
            events = seq.Events.Select(e => Tuple.Create(e.Key, new EventRef(e.Value))).ToArray();
            deleted = seq;
            song.RemoveSequence(seq);
        }

        protected override void UndoAction()
        {
            var mac = Machine;
            song.AddSequence(mac, seqIndex);
            var seq = song.Sequences[seqIndex];
            foreach (var e in events) e.Item2.Set(seq, e.Item1);
            Restorations.Report(deleted, seq);
        }
    }
}
