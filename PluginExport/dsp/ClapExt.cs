// CLAP 1.2 structs the product needs beyond the spike's Clap.cs: the host, its extensions we call (params, latency, timer, gui),
// the plug-in extensions for notes, tail and timers, and the note, MIDI and gesture events. Layouts follow the MIT-licensed headers.
using System;
using System.Runtime.InteropServices;

namespace NeoExport
{
    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapHost
    {
        public ClapVersion Version; public void* HostData; public byte* Name, Vendor, Url, VersionStr;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte*, void*> GetExtension;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestRestart, RequestProcess, RequestCallback;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapHostParams
    {
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, void> Rescan;
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, uint, void> Clear;
        public delegate* unmanaged[Cdecl]<ClapHost*, void> RequestFlush;
    }

    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapHostLatency { public delegate* unmanaged[Cdecl]<ClapHost*, void> Changed; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapHostTimerSupport
    {
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, uint*, byte> RegisterTimer;
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, byte> UnregisterTimer;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapHostGui
    {
        public delegate* unmanaged[Cdecl]<ClapHost*, void> ResizeHintsChanged;
        public delegate* unmanaged[Cdecl]<ClapHost*, uint, uint, byte> RequestResize;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte> RequestShow, RequestHide;
        public delegate* unmanaged[Cdecl]<ClapHost*, byte, void> Closed;
    }

    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapPluginTimerSupport { public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, void> OnTimer; }
    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapPluginTail { public delegate* unmanaged[Cdecl]<ClapPlugin*, uint> Get; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapNotePortInfo { public uint Id, SupportedDialects, PreferredDialect; public fixed byte Name[256]; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapPluginNotePorts
    {
        public delegate* unmanaged[Cdecl]<ClapPlugin*, byte, uint> Count;
        public delegate* unmanaged[Cdecl]<ClapPlugin*, uint, byte, ClapNotePortInfo*, byte> Get;
    }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventNote { public ClapEventHeader Header; public int NoteId; public short PortIndex, Channel, Key; public double Velocity; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventMidi { public ClapEventHeader Header; public ushort PortIndex; public fixed byte Data[3]; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapEventParamGesture { public ClapEventHeader Header; public uint ParamId; }

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct ClapResizeHints { public byte CanResizeH, CanResizeV, PreserveAspect; public uint AspectW, AspectH; }

    /// <summary>The window handle a host gives clap.gui set_parent: a union of x11 id, cocoa NSView*, win32 HWND.</summary>
    [StructLayout(LayoutKind.Sequential)] public unsafe struct ClapWindow { public byte* Api; public void* Handle; }
}
