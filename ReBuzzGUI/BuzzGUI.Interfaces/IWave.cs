using System;
using System.Collections.ObjectModel;
using System.ComponentModel;

namespace BuzzGUI.Interfaces
{
    // Fork (mac-engine.md M2): Envelope (0x80) has a name, as BMX songs set it. The XML serializer
    // refuses flags it can't name, so a song with such a wave didn't save as BMXML.
    [Flags]
    public enum WaveFlags { Loop = 1, Not16Bit = 4, Stereo = 8, BidirectionalLoop = 16, Envelope = 0x80 };

    [Flags]
    public enum LoopType { None, Unidirectional, Bidirectional };

    public interface IWave : INotifyPropertyChanged
    {
        int Index { get; }
        string Name { get; }
        WaveFlags Flags { get; set; }
        float Volume { get; set; }
        ReadOnlyCollection<IWaveLayer> Layers { get; }

        /// <summary>
        /// The returned envelope is associated with machine 'm'. The association is needed for CMachineInterface::GetWaveEnvPlayPos calls.
        /// </summary>
        IEnvelope GetEnvelope(int index, IMachine m);

        void Play(IMachine m, int start = 0, int end = -1, LoopType looptype = LoopType.None, int layer = -1);
        void Stop(IMachine m);
    }
}
