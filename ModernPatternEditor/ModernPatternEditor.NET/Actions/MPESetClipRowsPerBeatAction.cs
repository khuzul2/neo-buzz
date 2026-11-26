using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor;
using WDE.ModernPatternEditor.MPEStructures;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class MPESetClipRowsPerBeatAction : ClipAction
    {
        // Just save all data
        Dictionary<int, ClipEvent[]> oldColumnEvents;
        Dictionary<int, int[]> oldBeats;

        int oldRowsPerBeat;
        int newRowsPerBeat;

        MPEClip mpeClip;
        ClipVM clipVM;

        public MPESetClipRowsPerBeatAction(MPEClip clip, ClipVM clipVM, int newRowsPerBeat)
            : base(clip.Clip)
        {
            mpeClip = clip;
            this.clipVM = clipVM;
            this.newRowsPerBeat = newRowsPerBeat;
            this.oldRowsPerBeat = clip.RowsPerBeat;
        }

        protected override void DoAction()
        {
            oldColumnEvents = new Dictionary<int, ClipEvent[]>();
            oldBeats = new Dictionary<int, int[]>();
            foreach (var c in mpeClip.MPEClipColumns)
            {
                int index = mpeClip.MPEClipColumns.IndexOf(c);
                var pe = c.GetEvents(0, Clip.Length * ClipEvent.TimeBase).ToArray();
                oldColumnEvents[index] = pe;
                oldBeats[index] = c.BeatRowsList.ToArray();
            }
            mpeClip.RowsPerBeat = newRowsPerBeat;
            mpeClip.Quantize();
            clipVM.DefaultRPB = newRowsPerBeat;

            clipVM.Clip = Clip;

        }

        protected override void UndoAction()
        {
            mpeClip.RowsPerBeat = oldRowsPerBeat;
            clipVM.DefaultRPB = oldRowsPerBeat;
            foreach (var c in mpeClip.MPEClipColumns)
            {
                int index = mpeClip.MPEClipColumns.IndexOf(c);
                var pe = c.GetEvents(0, Clip.Length * ClipEvent.TimeBase).ToArray();
                c.SetEvents(pe, false); // Clear
                c.SetEvents(oldColumnEvents[index], true);
                c.SetBeats(oldBeats[index].ToList());
            }

            clipVM.Clip = Clip;
        }
    }
}
