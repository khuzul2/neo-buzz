using System;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor;
using WDE.ModernPatternEditor.MPEStructures;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class MPESetBeatSubdivisionAction : ClipAction
    {
        Tuple<MPEClipColumnBeatRef, int>[] newSubdiv;
        int[] oldSubdiv;
        MPEClip mpeClip;

        public MPESetBeatSubdivisionAction(MPEClip clip, IEnumerable<Tuple<MPEClipColumnBeatRef, int>> subdiv)
            : base(clip.Clip)
        {
            mpeClip = clip;
            this.newSubdiv = subdiv.ToArray();
            oldSubdiv = newSubdiv
                .Select(s => s.Item1.GetColumn(mpeClip).BeatSubdivision.ContainsKey(s.Item1.Beat)
                ? s.Item1.GetColumn(mpeClip).BeatSubdivision[s.Item1.Beat] : mpeClip.RowsPerBeat).ToArray();
        }

        protected override void DoAction()
        {
            foreach (var subdiv in newSubdiv)
            {
                var mpeColumn = mpeClip.GetColumn(subdiv.Item1.GetColumn(mpeClip));
                mpeColumn.BeatRowsList[subdiv.Item1.Beat] = subdiv.Item2;
            }
            var p = Clip;
            newSubdiv.Run(s => s.Item1.GetColumn(mpeClip).SetBeatSubdivision(s.Item1.Beat, s.Item2));

            mpeClip.Editor.clipControl.MoveCursorDelta(0, 0);
        }

        protected override void UndoAction()
        {
            var p = Clip;
            newSubdiv.Run((s, i) =>
            {
                if (oldSubdiv[i] >= 0)
                {
                    s.Item1.GetColumn(mpeClip).SetBeatSubdivision(s.Item1.Beat, oldSubdiv[i]);
                    var mpeColumn = mpeClip.GetColumn(s.Item1.GetColumn(mpeClip));
                    mpeColumn.BeatRowsList[s.Item1.Beat] = oldSubdiv[i];
                }
            });

            mpeClip.Editor.clipControl.MoveCursorDelta(0, 0);
        }

    }
}
