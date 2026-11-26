using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class SetBeatSubdivisionAction : ClipAction
    {
        readonly Tuple<ClipColumnBeatRef, int>[] newSubdiv;
        readonly int[] oldSubdiv;

        public SetBeatSubdivisionAction(IClip clip, IEnumerable<Tuple<ClipColumnBeatRef, int>> subdiv)
            : base(clip)
        {
            this.newSubdiv = subdiv.ToArray();
            oldSubdiv = newSubdiv
                .Select(s => s.Item1.GetColumn(clip).BeatSubdivision.ContainsKey(s.Item1.Beat)
                ? s.Item1.GetColumn(clip).BeatSubdivision[s.Item1.Beat] : -1).ToArray();
        }

        protected override void DoAction()
        {
            var p = Clip;
            newSubdiv.Run(s => s.Item1.GetColumn(p).SetBeatSubdivision(s.Item1.Beat, s.Item2));
        }

        protected override void UndoAction()
        {
            var p = Clip;
            newSubdiv.Run((s, i) => { if (oldSubdiv[i] >= 0) s.Item1.GetColumn(p).SetBeatSubdivision(s.Item1.Beat, oldSubdiv[i]); });
        }

    }
}
