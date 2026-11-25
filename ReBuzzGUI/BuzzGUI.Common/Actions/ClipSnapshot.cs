using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.Actions
{
    public class ClipSnapshot
    {
        readonly List<ClipColumnSnapshot> columns;

        public ClipSnapshot(IClip p)
        {
            columns = p.Columns.Select(c => new ClipColumnSnapshot(c, -int.MaxValue, int.MaxValue)).ToList();
        }

        public void CopyTo(IClip p)
        {
            for (int i = 0; i < Math.Min(columns.Count, p.Columns.Count); i++)
            {
                columns[i].CopyTo(p.Columns[i]);
            }
        }
    }
}
