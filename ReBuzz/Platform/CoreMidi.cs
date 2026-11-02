#nullable disable
// Fork (docs/specs/mac-engine.md M6): CoreMIDI, macOS's MIDI system, through our own P/Invoke
// (decision 7 there): a system framework, so there is nothing to bundle or sign, and no package.
// Just what the engine needs: sources and destinations with their names, input ports that turn
// packets into short messages, output ports and virtual endpoints that send them, and the
// client's notifications for hot-plug.
//
// One client per process, made on a thread of its own that runs a CFRunLoop: CoreMIDI calls a
// client's notify proc on the run loop that was current when the process first made a client,
// and .NET threads don't run one.
//
// Real time: read procs run on CoreMIDI's receive thread. They parse packets in place and call
// the receiver once per message, with no allocation. Sends build their packet list in a buffer
// allocated up front.
//
// It uses the MIDI 1.0 packet-list calls (MIDIInputPortCreate, MIDISend, MIDIReceived…). macOS 11
// deprecated them in favour of UMP event lists, but still supports them; the event-list calls
// take Objective-C blocks, which P/Invoke can't make easily. The binding is for LP64 (ItemCount
// is nuint). This file stays free of other references: the seam tests link it.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace ReBuzz.Platform
{
    public static unsafe class CoreMidi
    {
        const string Midi = "/System/Library/Frameworks/CoreMIDI.framework/CoreMIDI";
        const string Cf = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        const uint Utf8 = 0x08000100;          // kCFStringEncodingUTF8

        // ---- CoreFoundation

        [DllImport(Cf)] static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, char* chars, nint length);
        [DllImport(Cf)] static extern nint CFStringGetLength(IntPtr text);
        [DllImport(Cf)] static extern nint CFStringGetMaximumSizeForEncoding(nint length, uint encoding);
        [DllImport(Cf)] static extern byte CFStringGetCString(IntPtr text, byte* buffer, nint size, uint encoding);
        [DllImport(Cf)] static extern void CFRelease(IntPtr cf);
        [DllImport(Cf)] static extern int CFRunLoopRunInMode(IntPtr mode, double seconds, byte returnAfterSourceHandled);

        // ---- CoreMIDI (object, port and endpoint refs are UInt32; ItemCount and ByteCount are unsigned long)

        [DllImport(Midi)] static extern int MIDIClientCreate(IntPtr name, delegate* unmanaged[Cdecl]<void*, void*, void> notify, void* refCon, out uint client);
        [DllImport(Midi)] static extern int MIDIInputPortCreate(uint client, IntPtr name, delegate* unmanaged[Cdecl]<void*, void*, void*, void> read, void* refCon, out uint port);
        [DllImport(Midi)] static extern int MIDIOutputPortCreate(uint client, IntPtr name, out uint port);
        [DllImport(Midi)] static extern int MIDIPortConnectSource(uint port, uint source, void* connRefCon);
        [DllImport(Midi)] static extern int MIDIPortDisconnectSource(uint port, uint source);
        [DllImport(Midi)] static extern int MIDIPortDispose(uint port);
        [DllImport(Midi)] static extern nuint MIDIGetNumberOfSources();
        [DllImport(Midi)] static extern uint MIDIGetSource(nuint index);
        [DllImport(Midi)] static extern nuint MIDIGetNumberOfDestinations();
        [DllImport(Midi)] static extern uint MIDIGetDestination(nuint index);
        [DllImport(Midi)] static extern int MIDIObjectGetStringProperty(uint obj, IntPtr property, out IntPtr value);
        [DllImport(Midi)] static extern int MIDIObjectGetIntegerProperty(uint obj, IntPtr property, out int value);
        [DllImport(Midi)] static extern int MIDIObjectSetIntegerProperty(uint obj, IntPtr property, int value);
        [DllImport(Midi)] static extern int MIDISourceCreate(uint client, IntPtr name, out uint source);
        [DllImport(Midi)] static extern int MIDIDestinationCreate(uint client, IntPtr name, delegate* unmanaged[Cdecl]<void*, void*, void*, void> read, void* refCon, out uint destination);
        [DllImport(Midi)] static extern int MIDIEndpointDispose(uint endpoint);
        [DllImport(Midi)] static extern int MIDISend(uint port, uint destination, void* packets);
        [DllImport(Midi)] static extern int MIDIReceived(uint source, void* packets);
        [DllImport(Midi)] static extern void* MIDIPacketListInit(void* packets);
        [DllImport(Midi)] static extern void* MIDIPacketListAdd(void* packets, nuint listSize, void* current, ulong time, nuint length, byte* data);

        /// <summary>Whether CoreMIDI is here (macOS).</summary>
        public static bool Available
        {
            get
            {
                if (!OperatingSystem.IsMacOS()) return false;
                try
                {
                    MIDIGetNumberOfDestinations();
                    return true;
                }
                catch (DllNotFoundException) { return false; }
                catch (EntryPointNotFoundException) { return false; }
            }
        }

        // ------------------------------------------------------------------ the client

        static readonly object gate = new object();
        static readonly HashSet<uint> own = new HashSet<uint>();        // virtual endpoints made here
        static uint client, outputPort;
        static int clientStatus;
        static Thread notifications;

        /// <summary>
        /// The MIDI setup changed: a device or a virtual endpoint came or went, went offline, or
        /// was renamed. On the notification thread; several come for one device.
        /// </summary>
        public static event Action SetupChanged;

        /// <summary>The process's client (made on first use, with its notification thread).</summary>
        public static uint Client
        {
            get
            {
                lock (gate)
                {
                    if (client != 0) return client;
                    if (notifications == null)
                    {
                        var ready = new ManualResetEventSlim();
                        notifications = new Thread(() => RunNotifications(ready)) { IsBackground = true, Name = "CoreMIDI notifications" };
                        notifications.Start();
                        ready.Wait();
                    }
                    Check(clientStatus, "MIDIClientCreate");
                    return client;
                }
            }
        }

        static void RunNotifications(ManualResetEventSlim ready)
        {
            IntPtr name = CFString("ReBuzz");
            try
            {
                clientStatus = MIDIClientCreate(name, &Notify, null, out client);
            }
            catch (Exception)
            {
                clientStatus = -1;
            }
            finally
            {
                CFRelease(name);
                ready.Set();
            }
            if (client == 0) return;
            IntPtr mode = Constant(Cf, "kCFRunLoopDefaultMode");
            // A background thread: it runs as long as the process (CoreMIDI keeps the client).
            while (true)
            {
                if (CFRunLoopRunInMode(mode, 1.0, 0) == 1)       // kCFRunLoopRunFinished: nothing to run
                    Thread.Sleep(250);
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Notify(void* message, void* refCon)
        {
            try
            {
                // MIDINotification: messageID, then its size. 1 setup changed, 2 object added,
                // 3 object removed, 4 property changed (offline, names).
                int id = *(int*)message;
                if (id >= 1 && id <= 4) SetupChanged?.Invoke();
            }
            catch (Exception)
            {
                // Nothing may cross into CoreMIDI's thread.
            }
        }

        /// <summary>The output port every <see cref="Output"/> sends through.</summary>
        static uint OutputPort
        {
            get
            {
                lock (gate)
                {
                    if (outputPort != 0) return outputPort;
                    uint c = Client;
                    IntPtr name = CFString("ReBuzz out");
                    try { Check(MIDIOutputPortCreate(c, name, out outputPort), "MIDIOutputPortCreate"); }
                    finally { CFRelease(name); }
                    return outputPort;
                }
            }
        }

        // ------------------------------------------------------------------ endpoints

        /// <summary>A source or destination: its ref, the name to show (CoreMIDI's display name), its unique ID, and whether this process made it.</summary>
        public readonly record struct Endpoint(uint Ref, string Name, int UniqueId, bool Own);

        /// <summary>The online sources (inputs to ReBuzz), in CoreMIDI's order.</summary>
        public static List<Endpoint> Sources() => Endpoints(sources: true);

        /// <summary>The online destinations (outputs from ReBuzz), in CoreMIDI's order.</summary>
        public static List<Endpoint> Destinations() => Endpoints(sources: false);

        static List<Endpoint> Endpoints(bool sources)
        {
            _ = Client;                      // the first client starts the MIDI server
            var list = new List<Endpoint>();
            int count = (int)(sources ? MIDIGetNumberOfSources() : MIDIGetNumberOfDestinations());
            for (int i = 0; i < count; i++)
            {
                uint e = sources ? MIDIGetSource((nuint)i) : MIDIGetDestination((nuint)i);
                if (e == 0) continue;
                if (MIDIObjectGetIntegerProperty(e, Keys.Offline, out int offline) == 0 && offline != 0) continue;
                string name = StringProperty(e, Keys.DisplayName) ?? StringProperty(e, Keys.Name) ?? (sources ? "Source " : "Destination ") + (i + 1);
                MIDIObjectGetIntegerProperty(e, Keys.UniqueId, out int id);
                bool mine;
                lock (gate) mine = own.Contains(e);
                list.Add(new Endpoint(e, name, id, mine));
            }
            return list;
        }

        static string StringProperty(uint obj, IntPtr key)
        {
            if (MIDIObjectGetStringProperty(obj, key, out IntPtr text) != 0 || text == IntPtr.Zero) return null;
            try { return FromCFString(text); }
            finally { CFRelease(text); }
        }

        /// <summary>The property names CoreMIDI exports (CFStrings).</summary>
        static class Keys
        {
            public static readonly IntPtr Name = Constant(Midi, "kMIDIPropertyName");
            public static readonly IntPtr DisplayName = Constant(Midi, "kMIDIPropertyDisplayName");
            public static readonly IntPtr UniqueId = Constant(Midi, "kMIDIPropertyUniqueID");
            public static readonly IntPtr Offline = Constant(Midi, "kMIDIPropertyOffline");
        }

        static IntPtr Constant(string library, string symbol) => *(IntPtr*)NativeLibrary.GetExport(NativeLibrary.Load(library), symbol);

        static IntPtr CFString(string text)
        {
            fixed (char* p = text) return CFStringCreateWithCharacters(IntPtr.Zero, p, text.Length);
        }

        static string FromCFString(IntPtr text)
        {
            nint size = CFStringGetMaximumSizeForEncoding(CFStringGetLength(text), Utf8) + 1;
            var buffer = new byte[size];
            fixed (byte* b = buffer)
                return CFStringGetCString(text, b, size, Utf8) != 0 ? Marshal.PtrToStringUTF8((IntPtr)b) : null;
        }

        static void Check(int status, string what)
        {
            if (status != 0) throw new InvalidOperationException($"{what}: CoreMIDI error {status}{Explain(status)}");
        }

        static string Explain(int status) => status switch
        {
            -10830 => " (invalid client)",
            -10834 => " (unknown endpoint)",
            -10839 => " (the MIDI server didn't start)",
            -10842 => " (no such object)",
            -10843 => " (ID not unique)",
            -10844 => " (not permitted)",
            _ => "",
        };

        // ------------------------------------------------------------------ receiving

        static readonly ConcurrentDictionary<nint, Receiver> receivers = new ConcurrentDictionary<nint, Receiver>();
        static long nextReceiver;

        /// <summary>
        /// Packets in, short messages out, in WinMM's order (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16),
        /// on CoreMIDI's receive thread. SysEx is skipped, as ReBuzz's inputs on Windows skip it.
        /// </summary>
        public abstract class Receiver : IDisposable
        {
            readonly Action<int> received;
            MidiParser parser;
            protected readonly nint Id;

            protected Receiver(Action<int> received)
            {
                this.received = received;
                Id = (nint)Interlocked.Increment(ref nextReceiver);
                receivers[Id] = this;
            }

            /// <summary>A MIDIPacketList: packets of a 64-bit time, a 16-bit length and the bytes, each 4-byte aligned on arm64 (MIDIPacketNext).</summary>
            internal void Parse(byte* list)
            {
                uint count = Unsafe.ReadUnaligned<uint>(list);
                byte* p = list + 4;
                for (uint i = 0; i < count; i++)
                {
                    int length = Unsafe.ReadUnaligned<ushort>(p + 8);
                    byte* data = p + 10;
                    for (int j = 0; j < length; j++)
                    {
                        int message = parser.Feed(data[j]);
                        if (message != 0) received(message);
                    }
                    p = data + length;
                    if (AlignedPackets) p = (byte*)(((nuint)p + 3) & ~(nuint)3);
                }
            }

            public virtual void Dispose()
            {
                // A read proc still running finds nothing to call.
                receivers.TryRemove(Id, out _);
            }
        }

        static readonly bool AlignedPackets = RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm;

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void Read(void* list, void* readRefCon, void* connRefCon)
        {
            try
            {
                if (receivers.TryGetValue((nint)readRefCon, out var r)) r.Parse((byte*)list);
            }
            catch (Exception)
            {
                // Nothing may cross into CoreMIDI's thread.
            }
        }

        /// <summary>An input port connected to one source.</summary>
        public sealed class Input : Receiver
        {
            uint port;
            readonly uint source;

            public Input(uint source, Action<int> received) : base(received)
            {
                this.source = source;
                IntPtr name = CFString("ReBuzz in");
                try
                {
                    Check(MIDIInputPortCreate(Client, name, &Read, (void*)Id, out port), "MIDIInputPortCreate");
                    Check(MIDIPortConnectSource(port, source, null), "MIDIPortConnectSource");
                }
                catch
                {
                    Dispose();
                    throw;
                }
                finally
                {
                    CFRelease(name);
                }
            }

            public override void Dispose()
            {
                base.Dispose();
                if (port == 0) return;
                MIDIPortDisconnectSource(port, source);
                MIDIPortDispose(port);
                port = 0;
            }
        }

        /// <summary>A virtual destination other apps send to.</summary>
        public sealed class VirtualDestination : Receiver
        {
            uint endpoint;

            public VirtualDestination(string name, Action<int> received) : base(received)
            {
                IntPtr cfName = CFString(name);
                try
                {
                    lock (gate)
                    {
                        Check(MIDIDestinationCreate(Client, cfName, &Read, (void*)Id, out endpoint), "MIDIDestinationCreate");
                        own.Add(endpoint);
                    }
                }
                catch
                {
                    base.Dispose();
                    throw;
                }
                finally
                {
                    CFRelease(cfName);
                }
                KeepId(endpoint, "destination " + name);
            }

            public uint Ref => endpoint;

            public override void Dispose()
            {
                base.Dispose();
                Disown(ref endpoint);
            }
        }

        // Virtual endpoints are ours: made and marked under the gate, so a scan never offers one
        // as a device. Each keeps its unique ID from run to run, so other apps find it again.

        static void KeepId(uint endpoint, string key)
        {
            uint hash = 2166136261;                                     // FNV-1a of the key
            foreach (char c in "ReBuzz " + key) hash = (hash ^ c) * 16777619;
            MIDIObjectSetIntegerProperty(endpoint, Keys.UniqueId, (int)(hash & 0x7FFFFFFF));   // taken (another ReBuzz): CoreMIDI keeps its own
        }

        static void Disown(ref uint endpoint)
        {
            if (endpoint == 0) return;
            MIDIEndpointDispose(endpoint);
            lock (gate) own.Remove(endpoint);
            endpoint = 0;
        }

        // ------------------------------------------------------------------ sending

        /// <summary>Sends short messages and byte strings in a packet list allocated up front (any thread; one send at a time).</summary>
        public abstract class Sender : IDisposable
        {
            const int Room = 1024;
            readonly object sendLock = new object();
            byte* list = (byte*)NativeMemory.Alloc(Room);

            protected abstract int Deliver(void* packets);

            /// <summary>A short message, as WinMM takes it: status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16.</summary>
            public void Send(int message)
            {
                int status = message & 0xFF;
                int length = MidiParser.Length(status);
                if (length == 0) return;
                byte* bytes = stackalloc byte[3];
                bytes[0] = (byte)status;
                bytes[1] = (byte)(message >> 8 & 0x7F);
                bytes[2] = (byte)(message >> 16 & 0x7F);
                lock (sendLock) Send(bytes, length);
            }

            /// <summary>Bytes as they are (SysEx, or several messages).</summary>
            public void Send(ReadOnlySpan<byte> bytes)
            {
                if (bytes.IsEmpty) return;
                fixed (byte* b = bytes)
                    lock (sendLock) Send(b, bytes.Length);
            }

            /// <summary>All notes off and the sustain pedal up on every channel, as WinMM's reset does.</summary>
            public void Reset()
            {
                byte* bytes = stackalloc byte[96];
                for (int ch = 0; ch < 16; ch++)
                {
                    bytes[ch * 6 + 0] = (byte)(0xB0 | ch); bytes[ch * 6 + 1] = 64; bytes[ch * 6 + 2] = 0;
                    bytes[ch * 6 + 3] = (byte)(0xB0 | ch); bytes[ch * 6 + 4] = 123; bytes[ch * 6 + 5] = 0;
                }
                lock (sendLock) Send(bytes, 96);
            }

            void Send(byte* bytes, int length)
            {
                if (list == null) throw new ObjectDisposedException(GetType().Name);
                // The list's header (4 bytes), the packet's (10), the bytes, and slack for alignment.
                int need = length + 20;
                bool big = need > Room;
                byte* target = big ? (byte*)NativeMemory.Alloc((nuint)need) : list;      // long SysEx only
                try
                {
                    void* packet = MIDIPacketListAdd(target, (nuint)(big ? need : Room), MIDIPacketListInit(target), 0, (nuint)length, bytes);
                    if (packet == null) throw new InvalidOperationException($"MIDIPacketListAdd: {length} bytes don't fit in a packet");
                    Check(Deliver(target), GetType().Name);
                }
                finally
                {
                    if (big) NativeMemory.Free(target);
                }
            }

            public virtual void Dispose()
            {
                lock (sendLock)
                {
                    if (list != null) NativeMemory.Free(list);
                    list = null;
                }
            }
        }

        /// <summary>Sends to a destination (a device's input, or another app's virtual destination).</summary>
        public sealed class Output : Sender
        {
            readonly uint port, destination;

            public Output(uint destination)
            {
                this.destination = destination;
                port = OutputPort;
            }

            protected override int Deliver(void* packets) => MIDISend(port, destination, packets);
        }

        /// <summary>A virtual source other apps receive from; what it sends, they get.</summary>
        public sealed class VirtualSource : Sender
        {
            uint endpoint;

            public VirtualSource(string name)
            {
                IntPtr cfName = CFString(name);
                try
                {
                    lock (gate)
                    {
                        Check(MIDISourceCreate(Client, cfName, out endpoint), "MIDISourceCreate");
                        own.Add(endpoint);
                    }
                }
                catch
                {
                    base.Dispose();
                    throw;
                }
                finally
                {
                    CFRelease(cfName);
                }
                KeepId(endpoint, "source " + name);
            }

            public uint Ref => endpoint;

            protected override int Deliver(void* packets) => endpoint == 0 ? 0 : MIDIReceived(endpoint, packets);

            public override void Dispose()
            {
                base.Dispose();
                Disown(ref endpoint);
            }
        }
    }

    /// <summary>
    /// A MIDI byte stream to short messages (status | data1 &lt;&lt; 8 | data2 &lt;&lt; 16): running
    /// status, real-time bytes anywhere (even inside SysEx), SysEx and stray data skipped.
    /// </summary>
    public struct MidiParser
    {
        int status, needed, have, data1;
        bool sysex;

        /// <summary>Data bytes after a status byte: 2 for notes, CCs, pitch bend, song position; 1 for program, pressure, MTC, song select; 0 for the rest.</summary>
        public static int DataBytes(int status) =>
            status < 0x80 ? -1 :
            status < 0xC0 ? 2 :
            status < 0xE0 ? 1 :
            status < 0xF0 ? 2 :
            status == 0xF1 || status == 0xF3 ? 1 :
            status == 0xF2 ? 2 : 0;

        /// <summary>A short message's length in bytes, or 0 for none (no status, or SysEx).</summary>
        public static int Length(int status) => status < 0x80 || status == 0xF0 || status == 0xF7 ? 0 : 1 + DataBytes(status);

        /// <summary>Feeds one byte; a complete message, or 0.</summary>
        public int Feed(byte b)
        {
            if (b >= 0xF8) return b;                         // real time: at once
            if (b >= 0x80)
            {
                have = 0;
                sysex = b == 0xF0;
                if (b == 0xF0 || b == 0xF7)
                {
                    status = 0;
                    return 0;
                }
                needed = DataBytes(b);
                if (needed == 0)
                {
                    status = 0;                              // tune request: no data, no running status
                    return b;
                }
                status = b;
                return 0;
            }
            if (sysex || status == 0) return 0;
            if (have == 0)
            {
                data1 = b;
                have = 1;
                if (needed == 2) return 0;
                return Complete(status | b << 8);
            }
            return Complete(status | data1 << 8 | b << 16);
        }

        int Complete(int message)
        {
            have = 0;
            if (status >= 0xF0) status = 0;                  // system common messages don't run
            return message;
        }
    }
}
