using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor;
using WDE.ModernPatternEditor.MPEStructures;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class MPESetClipLengthAction : ClipAction
    {
        // Just save all data
        Dictionary<int, ClipEvent[]> oldColumnEvents;
        Dictionary<int, int[]> oldBeats;

        int oldBeatCount;
        int newBeatCount;

        MPEClip mpeClip;

        public MPESetClipLengthAction(MPEClip clip, int oldBeatCount, int newBeatCount)
            : base(clip.Clip)
        {
            mpeClip = clip;
            this.newBeatCount = newBeatCount;
            this.oldBeatCount = oldBeatCount;
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

            lock (mpeClip.Editor.syncLock)
            {
                mpeClip.SetBeatCount(newBeatCount);
                mpeClip.Clip.Length = newBeatCount * ClipControl.BUZZ_TICKS_PER_BEAT;
            }
        }

        protected override void UndoAction()
        {
            mpeClip.SetBeatCount(oldBeatCount);
            foreach (var c in mpeClip.MPEClipColumns)
            {
                int index = mpeClip.MPEClipColumns.IndexOf(c);
                var pe = c.GetEvents(0, Clip.Length * ClipEvent.TimeBase).ToArray();
                c.SetEvents(pe, false); // Clear
                c.SetEvents(oldColumnEvents[index], true);
                c.SetBeats(oldBeats[index].ToList());
                c.UpdateLength();
            }

            lock (mpeClip.Editor.syncLock)
            {
                mpeClip.Clip.Length = oldBeatCount * ClipControl.BUZZ_TICKS_PER_BEAT;
            }
        }
    }
}
