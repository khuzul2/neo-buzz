using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BuzzGUI.Interfaces
{
    public interface IClip : INotifyPropertyChanged
    {
        IMachine Machine { get; }
        string Name { get; set;  }
        int Length { get; set; }                                        // length in buzz ticks

        ReadOnlyCollection<IClipColumn> Columns { get; }

        int PlayPosition { get; }                                       // position in PatternEvent.TimeBase units or int.MinValue if not playing
        bool IsPlayingSolo { get; set; }

        int[] ClipEditorMachineMIDIEvents { get; set; }                   // data from pxp or pianoroll as midi events (CMachineInterfaceEx::ExportMidiEvents)
        IEnumerable<IClipEditorColumn> ClipEditorMachineEvents { get; }   // data from pxp or pianoroll as events

        void InsertColumn(int index, IParameter p, int track);          // PatternColumnType.Parameter
        void InsertColumn(int index, IMachine m);                       // PatternColumnType.MIDI

        void DeleteColumn(int index);

        void UpdatePEMachineWaveReferences(IDictionary<int, int> map);

        event Action<IClipColumn> ColumnAdded;
        event Action<IClipColumn> ColumnRemoved;
        event Action<IClipColumn> ClipChanged;
        void NotifyClipChanged();

        IntPtr CClip { get;  }

        IReadOnlyCollection<ISequence> Sequences { get; }

       event Action<IClip> OnClipPlayStart;
       event Action<IClip> OnClipPlayPositionChange;
       event Action<IClip> OnClipPlayEnd;
    }

    public interface IClipEditorColumn
    {
        IParameter Parameter { get; }
        IEnumerable<ClipEvent> Events { get; }
    }
}
