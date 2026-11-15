// The CLAP 1.2 ABI the plug-in needs, as blittable structs with function pointers (layouts checked against the headers'
// sizes by native/abi_check.c and the self-test). MIT-licensed headers: github.com/free-audio/clap.
using System;
using System.Runtime.InteropServices;

namespace NeoExport
{
    [StructLayout(LayoutKind.Sequential)] public struct ClapVersion { public uint Major, Minor, Revision; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapDescriptor
    {
        public ClapVersion Version;
        public byte* Id, Name, Vendor, Url, ManualUrl, SupportUrl, PluginVersion, Description;
        public byte** Features;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPlugin
    {
        public ClapDescriptor* Desc;
        public void* PluginData;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Init;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, uint, uint, byte> Activate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Deactivate;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> StartProcessing;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> StopProcessing;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Reset;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapProcess*, int> Process;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, void*> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> OnMainThread;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapFactory
    {
        public delegate* unmanaged[Cdecl]<ClapFactory*, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapFactory*, uint, ClapDescriptor*> Descriptor;
        public delegate* unmanaged[Cdecl]<ClapFactory*, void*, byte*, ClapPlugin*> Create;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventHeader { public uint Size, Time; public ushort SpaceId, Type; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventParamValue
    {
        public ClapEventHeader Header;
        public uint ParamId; public void* Cookie; public int NoteId; public short PortIndex, Channel, Key; public double Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventTransport
    {
        public ClapEventHeader Header;
        public uint Flags; public long SongPosBeats, SongPosSeconds; public double Tempo, TempoInc;
        public long LoopStartBeats, LoopEndBeats, LoopStartSeconds, LoopEndSeconds, BarStart;
        public int BarNumber; public ushort TsigNum, TsigDenom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapInputEvents
    {
        public void* Ctx;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint> Size;
        public delegate* unmanaged[Cdecl]<ClapInputEvents*, uint, ClapEventHeader*> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapOutputEvents { public void* Ctx; public delegate* unmanaged[Cdecl]<ClapOutputEvents*, ClapEventHeader*, byte> TryPush; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapAudioBuffer { public float** Data32; public double** Data64; public uint ChannelCount, Latency; public ulong ConstantMask; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapProcess
    {
        public long SteadyTime; public uint FramesCount; public ClapEventTransport* Transport;
        public ClapAudioBuffer* AudioInputs; public ClapAudioBuffer* AudioOutputs; public uint AudioInputsCount, AudioOutputsCount;
        public ClapInputEvents* InEvents; public ClapOutputEvents* OutEvents;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapAudioPortInfo
    {
        public uint Id; public fixed byte Name[256]; public uint Flags, ChannelCount; public byte* PortType; public uint InPlacePair;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginAudioPorts
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte, ClapAudioPortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapParamInfo
    {
        public uint Id, Flags; public void* Cookie; public fixed byte Name[256]; public fixed byte Module[1024];
        public double Min, Max, Default;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginParams
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, ClapParamInfo*, byte> GetInfo;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, double*, byte> GetValue;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, double, byte*, uint, byte> ValueToText;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte*, double*, byte> TextToValue;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapInputEvents*, ClapOutputEvents*, void> Flush;
    }

    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapOStream { public void* Ctx; public delegate* unmanaged[Cdecl]<ClapOStream*, void*, ulong, long> Write; }
    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapIStream { public void* Ctx; public delegate* unmanaged[Cdecl]<ClapIStream*, void*, ulong, long> Read; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginState
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapOStream*, byte> Save;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, ClapIStream*, byte> Load;
    }

    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapPluginLatency { public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Get; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginGui
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> IsApiSupported;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte**, byte*, byte> GetPreferredApi;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, byte, byte> Create;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> Destroy;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, double, byte> SetScale;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint*, uint*, byte> GetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> CanResize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void*, byte> GetResizeHints;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint*, uint*, byte> AdjustSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, uint, byte> SetSize;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void*, byte> SetParent;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void*, byte> SetTransient;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte*, void> SuggestTitle;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Show;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte> Hide;
    }

    /// <summary>The spike's own extension ("neo.spike.stats/1"): what the measurement harness reads and sets. Not part of a shipping plug-in.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NeoStats
    {
        public int DspGc0, DspGc1, DspGc2, EdGc0, EdGc1, EdGc2, EdAlive, EdReady, InprocReady, Pad;
        public long DspPauseNs, DspMaxPauseNs, DspAllocBytes, EdPauseNs, EdMaxPauseNs, EdAllocBytes, EditsApplied, EditsPosted, EdFrames;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct NeoSpikeExt
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, NeoStats*, void> GetStats;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, int, void> SetInprocLoad;
        public delegate* unmanaged[Cdecl]<byte*, int, int> GcConfig;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, int, int, int> EditorOpen;   // load, edits -> 1 when the editor library is running
        public delegate* unmanaged[Cdecl]<ClapPlugin*, void> EditorClose;
    }
}
