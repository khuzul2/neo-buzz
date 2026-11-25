using BuzzGUI.Common.Actions;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WDE.ModernPatternEditor.MPEStructures
{
    internal class ReBuzzClipColumn : IClipColumn
    {
        internal MPEClip MPEClip { get; }

        public ClipColumnType Type { get; }

        public IClip Clip { get; }

        public IMachine Machine
        {
            get;
            set;
        }

        public IParameter Parameter { get; }

        public int Track { get; set; }

        List<ClipEvent> clipEvents = new List<ClipEvent>();

        Dictionary<int, int> beatSubdivision;
        public ReadOnlyDictionary<int, int> BeatSubdivision { get => BuzzGUI.Interfaces.IDictionaryExtensions.AsReadOnly(beatSubdivision); }

        Dictionary<string, string> metadata = new Dictionary<string, string>();
        public IDictionary<string, string> Metadata { get => metadata; }

        public event Action<IEnumerable<ClipEvent>, bool> EventsChanged;
        public event Action<int> BeatSubdivisionChanged;
        public event PropertyChangedEventHandler PropertyChanged;

        public ReBuzzClipColumn(MPEClip p, ClipColumnType type, IClip clip, IMachine machine, IParameter parameter, int track, Dictionary<int, int> beatSubdivisions, Dictionary<string, string> metadatas)
        {
            MPEClip = p;
            Type = type;
            Clip = clip;
            Machine = machine;
            Parameter = parameter;
            Track = track;
            beatSubdivision = beatSubdivisions;
            if (beatSubdivision == null)
                beatSubdivision = new Dictionary<int, int>();
            metadata = metadatas;
            if (metadata == null)
                metadata = new Dictionary<string, string>();
        }

        // Returns the events from PatternColumn object. Not from pattern editor machine.
        public IEnumerable<ClipEvent> GetEvents(int tbegin, int tend)
        {
            return clipEvents.Where(pe => pe.Time >= tbegin && pe.Time < tend).ToArray();
        }

        public void GetEventsInto(int tbegin, int tend, List<ClipEvent> dest)
        {
            dest.Clear();
            foreach (var pe in clipEvents)
                if (pe.Time >= tbegin && pe.Time < tend)
                    dest.Add(pe);
        }

        public void SetBeatSubdivision(int beatindex, int subdiv)
        {
            beatSubdivision[beatindex] = subdiv;
            BeatSubdivisionChanged?.Invoke(beatindex);
        }

        public void SetEvents(IEnumerable<ClipEvent> clipEvents, bool set)
        {
            foreach (var pe in clipEvents)
            {
                if (set)
                    this.clipEvents.Add(pe);
                else
                    this.clipEvents.Remove(pe);
            }
            EventsChanged?.Invoke(clipEvents, set);
        }
    }

    public struct MPEClipColumnBeatRef
    {
        private readonly MPEClipColumnRef column;

        private readonly int beat;

        public int Beat => beat;

        public IClipColumn GetColumn(MPEClip p)
        {
            return column.GetColumn(p);
        }

        public MPEClipColumnBeatRef(MPEClipColumnRef pcr, int beat)
        {
            column = pcr;
            this.beat = beat;
        }
    }

    public struct MPEClipColumnRef
    {
        private readonly int index;

        public IClipColumn GetColumn(MPEClip p)
        {
            return p.Columns[index];
        }

        public MPEClipColumnRef(IClipColumn pc)
        {
            var mpepc = pc as ReBuzzClipColumn;
            index = mpepc.MPEClip.Columns.IndexOf(mpepc);
        }
    }
}
