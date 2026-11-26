using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace WDE.ModernPatternEditor
{
    public class ParameterColumnSet : ColumnRenderer.IColumnSet
    {
        int beatCount;
        public int BeatCount
        {
            get { return beatCount; }
            set
            {
                beatCount = value;
                PropertyChanged.Raise(this, "BeatCount");
            }
        }

        public bool Muted { get; set; }

        public string Label { get; set; }
        public Color Color { get { return Clip.Clip.Machine.GetThemeColor(); } }

        public readonly ClipVM Clip;
        protected ParameterColumn[] columns;
        protected readonly int track;
        public IList<ColumnRenderer.IColumn> Columns { get { return columns; } }

        /// <summary>Fork (daw-modes.md AR3): a set whose columns the subclass makes (the Notes lanes).</summary>
        protected ParameterColumnSet(ClipVM clip, int track)
        {
            this.Clip = clip;
            this.track = track;
            columns = new ParameterColumn[0];
        }

        public ParameterColumnSet(ClipVM clip, IEnumerable<IClipColumn> ic, int track)
        {
            this.Clip = clip;
            this.track = track;
            columns = ic.Select(c => new ParameterColumn(this, c, track, clip.DefaultRPB)).ToArray();
        }

        public virtual void Release()
        {
            foreach (var c in columns) c.Release();
        }

        public void FireBeatsInvalidated(HashSet<int> beats)
        {
            if (BeatsInvalidated != null) BeatsInvalidated(beats);
        }

        public virtual void SendCCsAtTime(int time)
        {
            foreach (var c in columns)
            {
                c.SendCCAtTime(time);
            }
            columns[0].ClipColumn.Machine.SendControlChanges();
        }

        public event Action<HashSet<int>> BeatsInvalidated;

        #region INotifyPropertyChanged Members

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        #endregion
    }
}
