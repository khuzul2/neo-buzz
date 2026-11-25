using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Xml.Serialization;

namespace BuzzGUI.Interfaces
{
    [StructLayout(LayoutKind.Sequential)]
    [XmlType("PatternEvent")]      // the Modern Pattern Editor's XML export stores this name
    public struct ClipEvent
    {
        public const int TimeBase = 240;            // ticks per one buzz tick

        public ClipEvent(int time, int value, int duration = 0) { Time = time; Value = value; Duration = duration; }

        public int Time;                            // 0 <= Time < Clip.Length * TimeBase
        public int Value;
        public int Duration;                        // duration for midi note on events (if negative, there will be no associated note off event)
    }

    public enum ClipColumnType { Parameter, MIDI };

    public interface IClipColumn : INotifyPropertyChanged
    {
        ClipColumnType Type { get; }
        IClip Clip { get; }
        IMachine Machine { get; set; }              // this can be changed after the column is created if Type == MIDI
        IParameter Parameter { get; }
        int Track { get; }

        //IDictionary<int, int> Guides { get; }		// time, step pairs (similar to sequence editor time signatures)
        ReadOnlyDictionary<int, int> BeatSubdivision { get; }
        IDictionary<string, string> Metadata { get; }

        IEnumerable<ClipEvent> GetEvents(int tbegin, int tend);      // tbegin <= t < tend
        void GetEventsInto(int tbegin, int tend, List<ClipEvent> dest);  // zero-alloc: fills a caller-owned buffer
        void SetEvents(IEnumerable<ClipEvent> e, bool set);

        void SetBeatSubdivision(int beatindex, int subdiv);

        event Action<IEnumerable<ClipEvent>, bool> EventsChanged;
        event Action<int> BeatSubdivisionChanged;
    }
}
