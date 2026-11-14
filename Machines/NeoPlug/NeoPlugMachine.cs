#nullable disable
// Neo Plug and Neo Plug FX (docs/specs/plugin-host.md §3 to §6): managed machines that host a CLAP or VST3 plug-in in
// process, through the native library neoplug. One source, two machines: the instrument (a generator with tracks of Note,
// Velocity, Delay and a Param/Value pair) and, with FX defined, the effect (a signal in, a sidechain on the second input).
// The Gear menu lists the scanned plug-ins by name ("Vendor/Name (VST3)"); a song keeps the plug-in's identity and state.
//
// The 64 slot parameters are the plug-in's first automatable parameters, named once it's loaded (IBuzzMachineHostRuntime) and
// kept equal to the plug-in's own values, so ReBuzz re-sending them, as it does when it loads a song, changes nothing.

using System;
using System.Collections.Generic;
using Buzz.MachineInterface;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;

namespace NeoBuzz.Plug
{
#if FX
    [MachineDecl(Name = "Neo Plug FX", ShortName = "PlugFX", Author = "neo-buzz", MaxTracks = 0, InputCount = 2, OutputCount = 1)]
#else
    [MachineDecl(Name = "Neo Plug", ShortName = "Plug", Author = "neo-buzz", MaxTracks = 16, InputCount = 0, OutputCount = 1)]
#endif
    public partial class NeoPlugMachine : IBuzzMachine, IBuzzMachineInstrument, IBuzzMachineManifest, IDisposable
    {
#if FX
        const bool Effect = true;
        const string Gear = "Neo Plug FX";
#else
        const bool Effect = false;
        const string Gear = "Neo Plug";
#endif
        readonly IBuzzMachineHost host;
        readonly PlugCore core;
        string requested = "";
        string manifestJson;
        int manifestRevision = -1;

        public NeoPlugMachine(IBuzzMachineHost host)
        {
            this.host = host;
            core = new PlugCore(host, Effect);
        }

        /// <summary>The Gear menu's plug-ins: the first run waits for a scan, later ones read the cache and rescan behind it.</summary>
        public static IEnumerable<string> GetInstrumentList()
        {
            try
            {
                if (!PlugNative.Available(out _)) return Array.Empty<string>();
                if (!PlugLibrary.Exists) PlugLibrary.Scan();
                else PlugLibrary.ScanInBackground();
                return PlugLibrary.Names(Effect);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("[neoplug] the plug-in list failed: " + e.Message);
                return Array.Empty<string>();
            }
        }

        // --- Loading -----------------------------------------------------------------------------------

        public void SetInstrument(string instrument)
        {
            requested = instrument ?? "";
            if (requested.Length == 0) return;
            var entry = PlugLibrary.Find(requested);
            if (entry == null)
            {
                core.Missing = true;
                core.Problem = "plug-in not found: " + requested;
                core.State.Display = requested;
                return;
            }
            core.State = new PlugState { SlotIds = core.State.SlotIds };
            core.Open(entry, entry.Display, false);
            core.PushToEngine();
        }

        public PlugState MachineState
        {
            get => core.SaveState();
            set
            {
                if (value == null || (string.IsNullOrEmpty(value.Path) && string.IsNullOrEmpty(value.Id))) return;
                if (!core.Missing && core.State.Id == value.Id && core.State.Path == value.Path && core.IsOpen)
                {
                    core.LoadState(value);
                    core.PushToEngine();
                    return;
                }
                var keep = value.Copy();
                var entry = PlugLibrary.Find(keep.Path, keep.Id, keep.Name, keep.Format);
                if (entry == null && (System.IO.File.Exists(keep.Path ?? "") || System.IO.Directory.Exists(keep.Path ?? "")))
                    entry = new PlugEntry { Format = keep.Format, Path = keep.Path, Id = keep.Id, Name = keep.Name, Vendor = keep.Vendor, Display = keep.Display };
                core.State = keep;
                if (entry == null)
                {
                    core.Missing = true;
                    core.Problem = $"plug-in not found: {keep.Display ?? keep.Name} ({keep.Path})";
                    Console.Error.WriteLine("[neoplug] " + core.Problem + "; its state is kept as it is");
                    core.LoadState(keep);
                    core.PushToEngine();
                    return;
                }
                if (core.Open(entry, keep.Display, true)) core.LoadState(keep);
                else core.State = keep;
                core.PushToEngine();
            }
        }

        public void Dispose() => core.Dispose();

        // --- Parameters (track parameters of the instrument) -------------------------------------------------

#if !FX
        [ParameterDecl(Name = "Note", IsStateless = true, Description = "Note (off releases the track's note)")]
        public void SetNote(Note value, int track) => core.SetNote(track, value.Value);

        [ParameterDecl(Name = "Velocity", IsStateless = true, MinValue = 1, MaxValue = 127, DefValue = 100, Description = "Velocity of the note on the same row (100 when empty)")]
        public void SetVelocity(int value, int track) => core.SetVelocity(track, value);

        [ParameterDecl(Name = "Delay", IsStateless = true, MinValue = 0, MaxValue = 127, DefValue = 0, Description = "Starts the note this many 1/128ths of a tick late")]
        public void SetDelay(int value, int track) => core.SetDelay(track, value);

        [ParameterDecl(Name = "Param", IsStateless = true, MinValue = 1, MaxValue = 64, DefValue = 1, Description = "Slot a Value on the same row goes to (1..64)")]
        public void SetParam(int value, int track) => core.SetParam(track, value);

        [ParameterDecl(Name = "Value", IsStateless = true, MinValue = 0, MaxValue = 65534, DefValue = 0, Description = "Sets the slot named by Param (0..65534 over its range), while the song plays")]
        public void SetValue(int value, int track) => core.SetValue(track, value);
#endif

        // --- Work -------------------------------------------------------------------------------------------

#if FX
        public bool Work(IList<Sample[]> output, IList<Sample[]> input, int n, WorkModes mode)
        {
            var outBuf = output != null && output.Count > 0 ? output[0] : null;
            if (outBuf == null) return false;
            var main = input != null && input.Count > 0 ? input[0] : null;
            var side = input != null && input.Count > 1 ? input[1] : null;
            return core.Render(main, side, outBuf, n);
        }

        public string GetChannelName(bool input, int index) => input ? (index == 0 ? "Input" : "Sidechain") : "Output";
#else
        public bool Work(Sample[] output, int n, WorkModes mode) => core.Render(null, null, output, n);
#endif

        public void MidiNote(int channel, int value, int velocity) => core.MidiNote(channel, value, velocity);
        public void MidiControlChange(int ctrl, int channel, int value) => core.MidiControlChange(ctrl, channel, value);
        public void Stop() => core.StopAll();
        public int GetLatency() => core.Latency;

        // --- Offline rendering (RenderTools calls these when a machine has them) ----------------------------

        public void ResetForRender() => core.BeginRender();
        public void EndRender() => core.EndRender();

        // --- Text and manifest ------------------------------------------------------------------------------

        public string DescribeValue(IParameter parameter, int value)
        {
            try
            {
                if (parameter?.Group == null || parameter.Group.Type != ParameterGroupType.Global) return null;
                int i = parameter.IndexInGroup;
                return core.IsBound(i) ? core.SlotTextSafe(i, value) : null;
            }
            catch (Exception) { return null; }
        }

        public int ManifestRevision => core.Revision;

        public string ManifestJson
        {
            get
            {
                if (core.Missing || !core.IsOpen) return null;
                int rev = core.Revision;
                if (manifestRevision != rev || manifestJson == null)
                {
                    try
                    {
                        manifestJson = PlugMain.Invoke(() => PlugManifest.Build(core, Gear, Effect), 30000);
                        manifestRevision = rev;
                    }
                    catch (Exception) { return null; }
                }
                return manifestJson;
            }
        }

        // --- Menu -------------------------------------------------------------------------------------------

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                var items = new List<IMenuItem>();
                string title = core.Missing ? "Missing: " + (core.State.Display ?? core.State.Name) : core.State.Display ?? core.State.Name ?? "no plug-in";
                items.Add(new MenuItemVM { Text = title, Command = new SimpleCommand { CanExecuteDelegate = _ => false, ExecuteDelegate = _ => { } } });
                var targets = new List<IMenuItem>
                {
                    new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = core.MidiOutName.Length == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => core.SetMidiTarget("") } },
                };
                foreach (var name in core.MidiTargets())
                {
                    string target = name;
                    targets.Add(new MenuItemVM { Text = target, IsCheckable = true, IsChecked = core.MidiOutName == target, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => core.SetMidiTarget(target) } });
                }
                items.Add(new MenuItemVM { Text = "MIDI out to", Children = targets });
                var channels = new List<IMenuItem>();
                for (int c = 0; c < 16; c++)
                {
                    int ch = c;
                    channels.Add(new MenuItemVM { Text = (c + 1).ToString(), IsCheckable = true, IsChecked = core.MidiChannel == c, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => core.MidiChannel = ch } });
                }
                items.Add(new MenuItemVM { Text = "Note channel", Children = channels });
                items.Add(new MenuItemVM { Text = "Rescan plug-ins", Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => PlugLibrary.ScanInBackground(false) } });
                return items;
            }
        }
    }
}
