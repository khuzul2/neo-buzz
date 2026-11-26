using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using WDE.ModernPatternEditor;
using WDE.ModernPatternEditor.MPEStructures;

namespace BuzzGUI.Common.Actions.ClipActions
{
    public class MPEAddOrRemoveColumnsAction : ClipAction
    {
        // <Position, Column>
        List<MPEClipColumn> removedColumns = new List<MPEClipColumn>();
        List<MPEClipColumn> addedColumns = new List<MPEClipColumn>();

        IEnumerable<IParameter> parameters;

        MPEClip mpeClip;
        ClipVM clipVM;

        public MPEAddOrRemoveColumnsAction(MPEClip clip, ClipVM clipVM, IEnumerable<IParameter> parameters)
            : base(clip.Clip)
        {
            mpeClip = clip;
            this.clipVM = clipVM;
            this.parameters = parameters;
        }

        protected override void DoAction()
        {
            // 1. Removed colunms
            var rc = mpeClip.MPEClipColumns.Where(x => parameters.All(p => p != x.Parameter && x.GroupType == ParameterGroupType.Track)).ToList();
            for (int i = 0; i < rc.Count(); i++)
            {
                var column = rc.ElementAt(i);
                removedColumns.Add(column);
                mpeClip.MPEClipColumns.Remove(column);
            }

            // 2. Insert added columns
            var ip = parameters.Where(p => mpeClip.MPEClipColumns.All(x => p != x.Parameter));

            foreach (var param in ip)
            {
                var mpeColumns = mpeClip.AddColumnToGroup(param);
                foreach (var column in mpeColumns)
                {
                    addedColumns.Add(column);
                }
            }

            mpeClip.UpdateData();
            clipVM.Clip = Clip; //Update
        }


        protected override void UndoAction()
        {
            foreach (var column in removedColumns)
            {
                mpeClip.MPEClipColumns.Add(column);
            }

            foreach (var column in addedColumns)
            {
                mpeClip.MPEClipColumns.Remove(column);
            }

            removedColumns.Clear();
            addedColumns.Clear();
            mpeClip.UpdateData();
            clipVM.Clip = Clip; //Update
        }
    }
}
