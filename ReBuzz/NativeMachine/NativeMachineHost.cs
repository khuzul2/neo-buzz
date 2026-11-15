using BuzzGUI.Common;
using BuzzGUI.Common.Settings;
using ReBuzz.Common;
using ReBuzz.Core;
using System;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Threading;
using BuzzGUI.Interfaces;
using System.Runtime.InteropServices;

namespace ReBuzz.NativeMachine
{
    internal class NativeMachineHost : IDisposable
    {
        /// <summary>
        /// Fork (docs/specs/mac-engine.md M2): native machines are Windows DLLs run in Windows host
        /// processes, over named shared memory. Elsewhere there is none: they load as placeholders.
        /// Fork (plugin-host.md §8.3): REBUZZ_NATIVE_MACHINES=off runs a Windows engine without them, as
        /// elsewhere (rebuzz-headless, which has no ReBuzzEngine32/64.exe beside it).
        /// </summary>
        /// <remarks>
        /// Fork (ux-overhaul.md P6, the studio's Windows preview): and only where the host processes are beside the app
        /// (bin32 or bin64, as the Windows package ships them): the studio's test runs and a studio folder without them
        /// start no hosts, rather than failing the machine scan.
        /// </remarks>
        /// <remarks>
        /// Fork (docs/specs/cross-platform.md R1): on macOS and Linux the native machines are the 17 open-source classics
        /// built as .dylib/.so (ExternalMachines/native), run by the portable host (ReBuzzEngine/portable) in a process
        /// of its own, over the same messages: it sits beside the app. Without it they stay placeholders.
        /// </remarks>
        internal static bool Supported => Environment.GetEnvironmentVariable("REBUZZ_NATIVE_MACHINES") != "off" && HostsInstalled;

        /// <summary>The portable host's executable (beside the app), off Windows.</summary>
        internal static string PortableHostPath => System.IO.Path.Combine(Global.BuzzPath, "rebuzz-native-host");

        static bool HostsInstalled => OperatingSystem.IsWindows() ?
            System.IO.File.Exists(System.IO.Path.Combine(Global.BuzzPath, "bin64", "ReBuzzEngine64.exe")) ||
            System.IO.File.Exists(System.IO.Path.Combine(Global.BuzzPath, "bin32", "ReBuzzEngine32.exe")) :
            System.IO.File.Exists(PortableHostPath);

        /// <summary>Fork (R1): off Windows, the folder holding the shared memory file and the channels' FIFOs.</summary>
        private string sessionFolder;

        private readonly string sharedId;
        internal Process childProcess;
        private MemoryMappedFile mappedFile;
        private MemoryMappedViewAccessor accessor;
        private ChannelListener channelListenerAudio;
        private ChannelListener channelListenerMIDI;
        private ChannelListener channelListenerUI;
        private ChannelListener channelListenerHost;
        private readonly string buzzPath;
        private readonly IUiDispatcher dispatcher;
        private readonly EngineSettings engineSettings;

        public bool Host64 { get; private set; }
        public HostMessage HostMessage { get; private set; }
        public UIMessage UIMessage { get; private set; }
        public AudioMessage AudioMessage { get; private set; }
        public MidiMessage MidiMessage { get; private set; }

        public event EventHandler<EventArgs> Connected;

        public bool IsConnected { get; set; }

        public NativeMachineHost(
            string sharedId,
            string buzzPath,
            IUiDispatcher dispatcher,
            EngineSettings engineSettings)
        {
            this.sharedId = sharedId + DateTime.Now.Ticks;
            this.buzzPath = buzzPath;
            this.dispatcher = dispatcher;
            this.engineSettings = engineSettings;
        }

        public void InitHost(ReBuzzCore buzz, bool host64)
        {
            if (IsConnected)
                return;

            bool windows = OperatingSystem.IsWindows();
            Host64 = host64 || !windows;      // the portable host is 64-bit
            string path = !windows ? PortableHostPath :
                host64 ? System.IO.Path.Combine(buzzPath, "bin64", "ReBuzzEngine64.exe") : System.IO.Path.Combine(buzzPath, "bin32", "ReBuzzEngine32.exe");

            int mapSize = IPC.GetSharedPageSize();
            if (windows)
            {
                mappedFile = MemoryMappedFile.CreateNew(sharedId, mapSize);
            }
            else
            {
                // No named maps or events off Windows: a folder with the map's file and a FIFO for each event.
                sessionFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "neobuzz-native-" + Environment.ProcessId + "-" + sharedId);
                System.IO.Directory.CreateDirectory(sessionFolder);
                foreach (var channelType in new[] { ChannelType.HostChannel, ChannelType.UIChannel, ChannelType.MidiChannel, ChannelType.AudioChannel })
                    foreach (var suffix in new[] { ".ping", ".pong" })
                        if (Posix.mkfifo(System.IO.Path.Combine(sessionFolder, ChannelListener.ChannelFileName(channelType) + suffix), Convert.ToUInt32("600", 8)) != 0)
                            throw new System.IO.IOException("mkfifo failed in " + sessionFolder);
                mappedFile = MemoryMappedFile.CreateFromFile(System.IO.Path.Combine(sessionFolder, "shm"), System.IO.FileMode.Create, null, mapSize);
            }
            accessor = mappedFile.CreateViewAccessor();

            HostMessage = new HostMessage(ChannelType.HostChannel, accessor, this, engineSettings);
            UIMessage = new UIMessage(ChannelType.UIChannel, accessor, this, dispatcher, engineSettings);
            AudioMessage = new AudioMessage(ChannelType.AudioChannel, accessor, this, engineSettings);
            MidiMessage = new MidiMessage(ChannelType.MidiChannel, accessor, this, engineSettings);

            // Host messages. Only this one listens to incoming messages from native machine.
            channelListenerHost = new ChannelListener(ChannelType.HostChannel,
                ThreadPriority.Normal, "Host" + sharedId, HostMessage, buzz, sessionFolder);
            HostMessage.ChannelListener = channelListenerHost;
            channelListenerHost.Start();

            channelListenerMIDI = new ChannelListener(ChannelType.MidiChannel,
                ThreadPriority.Normal, "MIDI" + sharedId, null, buzz, sessionFolder);
            MidiMessage.ChannelListener = channelListenerMIDI;

            // Audio messages
            channelListenerAudio = new ChannelListener(ChannelType.AudioChannel,
                ThreadPriority.Normal, "Audio" + sharedId, AudioMessage, buzz, sessionFolder);
            AudioMessage.ChannelListener = channelListenerAudio;

            // UI Messages
            channelListenerUI = new ChannelListener(ChannelType.UIChannel,
                ThreadPriority.Normal, "UI" + sharedId, UIMessage, buzz, sessionFolder);
            UIMessage.ChannelListener = channelListenerUI;

            ProcessStartInfo processInfo = new ProcessStartInfo(path, windows ? sharedId : "\"" + sessionFolder + "\"");
            if (!windows) processInfo.UseShellExecute = false;

            childProcess = Process.Start(processInfo);
            if (windows)
            {
                childProcess.PriorityClass = ProcessAndThreadProfile.ProcessPriorityClassNativeHostProcess;
            }
            else
            {
                try { childProcess.PriorityClass = ProcessAndThreadProfile.ProcessPriorityClassNativeHostProcess; } catch { }    // raising it needs rights here
            }
            childProcess.EnableRaisingEvents = true;

            if (windows)
                childProcess.WaitForInputIdle(); // Wait for the process to be ready for input
            IsConnected = true;

            childProcess.Exited += (sender, e) =>
            {
                // Fork: say why a host went away (crash codes like 0xC0000005 are telling).
                Serilog.Log.Warning("Native machine host {Path} exited with code 0x{ExitCode:X8}", path, childProcess.ExitCode);
                if (childProcess.ExitCode != 0)
                {
                    if (buzz.MachineManager.IsSingleProcessMode)
                    {
                        foreach (var machine in buzz.Song.Machines)
                        {
                            if (!machine.DLL.IsManaged)
                            {
                                var machineCore = machine as MachineCore;
                                machineCore.MachineDLL.IsCrashed = true;
                                machineCore.Ready = false;
                                buzz.ReportMachineCrashed(machineCore, $"its host process exited (0x{childProcess.ExitCode:X8})");
                            }
                        }
                    }
                    else
                    {
                        var kv = buzz.MachineManager.NativeMachines.FirstOrDefault(nm => nm.Value == this);

                        var machine = kv.Key;
                        if (machine != null)
                        {
                            machine.MachineDLL.IsCrashed = true;
                            machine.Ready = false;
                            buzz.ReportMachineCrashed(machine, $"its host process exited (0x{childProcess.ExitCode:X8})");
                        }
                    }

                    Dispose();
                }
            };

            // Track child processes and close them is main app crashes/closes (off Windows the host watches its parent)
            if (windows)
                ChildProcessTracker.AddProcess(childProcess);

            UIMessage.SendMessageBuzzInitSync(buzz.MainWindowHandle, host64);
            UIMessage.UIDSPInitSync(ReBuzzCore.masterInfo.SamplesPerSec);
        }

        /// <summary>
        /// Fork (NativeHangReport): a channel's state in the shared memory (whose turn it is: "call" and "send…" wait
        /// for the host, "reply…" for this side), its callback flag and message size, and what this side's message
        /// handler for it was doing.
        /// </summary>
        internal string DescribeChannel(ChannelType channel)
        {
            if (accessor == null) return "no shared memory";
            int state = accessor.ReadInt32(IPC.GetChannelSharedMemStateOffset(channel));
            int callback = accessor.ReadInt32(IPC.GetChannelSharedMemCallbackOffset(channel));
            int size = accessor.ReadInt32(IPC.GetChannelSharedMemSizeOffset(channel));
            NativeMessage msg = channel switch
            {
                ChannelType.AudioChannel => AudioMessage,
                ChannelType.UIChannel => UIMessage,
                ChannelType.MidiChannel => MidiMessage,
                _ => HostMessage,
            };
            string state1 = Enum.IsDefined(typeof(ChannelState), state) ? ((ChannelState)state).ToString() : state.ToString();
            return $"{state1}, callback {callback & 0xFF}, size {size}, {msg?.DescribeHandling() ?? "no handler"}";
        }

        public void Dispose()
        {
            if (IsConnected)
            {
                channelListenerUI.StopAndJoin();
                channelListenerMIDI.StopAndJoin();
                channelListenerAudio.StopAndJoin();
                channelListenerHost.StopAndJoin();

                try
                {
                    childProcess.Kill();
                }
                catch { }

                childProcess.Dispose();
                accessor.Dispose();
                mappedFile.Dispose();
                IsConnected = false;

                if (sessionFolder != null)
                {
                    try { System.IO.Directory.Delete(sessionFolder, true); } catch { }
                }
            }
        }
    }
}
