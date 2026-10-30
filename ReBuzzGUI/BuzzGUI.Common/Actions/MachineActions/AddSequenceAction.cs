using BuzzGUI.Interfaces;
using System.Linq;

namespace BuzzGUI.Common.Actions.MachineActions
{
    public class AddSequenceAction : MachineAction
    {
        readonly ISong song;
        ISequence removed;         // fork (0.6.2): the sequence its undo removed; a redo reports the new one

        public AddSequenceAction(IMachine machine)
            : base(machine)
        {
            song = machine.Graph.Buzz.Song;
        }

        protected override void DoAction()
        {
            var mac = Machine;
            if (mac == null) return;
            song.AddSequence(mac, song.Sequences.Count);
            Restorations.Report(removed, song.Sequences.LastOrDefault());
        }

        protected override void UndoAction()
        {
            var seq = song.Sequences.LastOrDefault();
            if (seq != null)
            {
                removed = seq;
                song.RemoveSequence(seq);
            }
        }

    }
}
