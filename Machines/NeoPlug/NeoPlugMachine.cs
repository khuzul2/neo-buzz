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
using System.Threading;
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
    public partial class NeoPlugMachine : IBuzzMachine, IBuzzMachineInstrument, IBuzzMachineManifest, IBuzzMachineNativeEditor, IDisposable, INoteCapable, INoteReceiver, INoteLegacyTarget
#if !FX
        , INoteMapProvider
#endif
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
        bool manifestCrashed;

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
            // A song is loading: its state follows, and the engine's writes before it (starting zeros, the song's values) wait for it.
            if ((host as IBuzzMachineHostRuntime)?.SongLoading == true) core.AwaitState();
        }

        public PlugState MachineState
        {
            get => core.SaveState();
            set
            {
                if (value == null || (string.IsNullOrEmpty(value.Path) && string.IsNullOrEmpty(value.Id))) { core.StateArrived(); return; }
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

        // --- Placement and crashes (H2) -----------------------------------------------------------------------

        /// <summary>Opens the plug-in again from <paramref name="keep"/> (identity, slot map, placement and the plug-in's own state).</summary>
        void ReopenFrom(PlugState keep)
        {
            var entry = PlugLibrary.Find(keep.Path, keep.Id, keep.Name, keep.Format);
            if (entry == null && (System.IO.File.Exists(keep.Path ?? "") || System.IO.Directory.Exists(keep.Path ?? "")))
                entry = new PlugEntry { Format = keep.Format, Path = keep.Path, Id = keep.Id, Name = keep.Name, Vendor = keep.Vendor, Display = keep.Display };
            core.State = keep;
            if (entry == null)
            {
                core.Missing = true;
                core.Problem = $"plug-in not found: {keep.Display ?? keep.Name} ({keep.Path})";
                core.PushToEngine();
                return;
            }
            if (core.Open(entry, keep.Display, true)) core.LoadState(keep);
            core.PushToEngine();
        }

        /// <summary>Starts a plug-in whose sandbox ended again, from its last state (the one a song save, a load or the background snapshot left).</summary>
        public void Reload()
        {
            if (core.Missing && !core.Crashed) return;
            ReopenFrom(core.LastGoodOrCurrent());
        }

        /// <summary>Moves the plug-in to another placement: it is saved, closed and opened again from that state.</summary>
        public void SetPlacement(string placement)
        {
            placement = PlugPlacement.Normalize(placement);
            if (core.Missing && !core.Crashed) { core.State.Placement = placement; return; }
            var keep = (core.Crashed ? core.LastGoodOrCurrent() : core.SaveState()).Copy();
            keep.Placement = placement;
            ReopenFrom(keep);
        }

        public bool Crashed => core.Crashed;
        public string PlacementText => core.PlacementText();

        // --- The plug-in's editor (H3) ----------------------------------------------------------------------

        public int EditorSupport(string api) => core.EditorSupport(api);

#if !FX
        // --- Note names (docs/specs/daw-modes.md §8.9, NM1): what the hosted plug-in calls its keys, for the piano roll ------------------
        // The core reads them on PlugMain (CLAP's note-name extension, VST3's program pitch names) when the plug-in opens, loads state or
        // says they changed; this hands out the immutable result. The effect has no keys.

        public int NoteMapVersion => core.NoteNameVersion;

        public IReadOnlyList<NoteMapEntry> ReadNoteMap(int channel) => core.ReadNoteMap(channel);

        public event Action NoteMapChanged
        {
            add => core.NoteNamesChanged += value;
            remove => core.NoteNamesChanged -= value;
        }
#endif

        public INativeEditorSession OpenEditor(string api, bool floating, IntPtr parent, double scale, string title, out string error) =>
            PlugEditorSession.Open(core, api, floating, parent, scale, title, out error);

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

        // Note wires (docs/specs/note-wires.md): the instrument takes notes; it sends them once it is known to make some. The effect has no note ports.
        bool INoteCapable.AcceptsNotes => !Effect;
        bool INoteCapable.SendsNotes => !Effect && core.NotesOut;
        void INoteReceiver.ReceiveNotes(ReadOnlySpan<NoteMessage> events) => core.ReceiveNotes(events);
        public string TakeLegacyTarget() => core.TakeLegacyTarget();
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
                if (i == PlugCore.Slots) return value >= 0 && value < core.OutputNames.Count ? core.OutputNames[value] : null;
#if FX
                if (i == PlugCore.Slots + 1) return (value / 10.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + "%";
#endif
                return core.IsBound(i) ? core.SlotTextSafe(i, value) : null;
            }
            catch (Exception) { return null; }
        }

        /// <summary>The engine's manifest cache (SongModel.Manifest) asks this and calls ManifestJson only when it changed: so a death the pump has not
        /// noticed yet (it bumps Revision up to 20 ms later) has to be noticed here, or the cached pre-crash manifest is served.</summary>
        public int ManifestRevision
        {
            get
            {
                if (!core.Missing && core.IsOpen) core.CheckCrashed();
                return Volatile.Read(ref core.Revision);
            }
        }

        /// <summary>The menu's choice of output: through the machine's parameter, so the song, undo and the agent see it.</summary>
        void SetOutput(int bus)
        {
            try
            {
                var list = host.Machine.ParameterGroups[1].Parameters;
                if (list.Count > PlugCore.Slots) { list[PlugCore.Slots].SetValue(0, bus); return; }
            }
            catch (Exception) { }
            core.SetOutBus(bus);
        }

        public string ManifestJson
        {
            get
            {
                if (core.Missing || !core.IsOpen) return null;
                // The cached manifest is keyed by Revision, which the pump bumps when it notices a dead sandbox (20 ms later): ask first.
                core.CheckCrashed();
                bool crashedNow = core.Crashed;
                int rev = Volatile.Read(ref core.Revision);
                if (manifestRevision != rev || manifestJson == null || manifestCrashed != crashedNow)
                {
                    try
                    {
                        // A crashed plug-in can't be asked for its texts: the manifest it had stays, with what happened added.
                        if (core.Crashed) { if (manifestJson == null) return null; manifestJson = PlugManifest.Patch(manifestJson, core); }
                        else manifestJson = PlugMain.Invoke(() => PlugManifest.Build(core, Gear, Effect), 30000);
                        manifestRevision = rev;
                        manifestCrashed = crashedNow;
                    }
                    catch (Exception) { return null; }
                }
                return manifestJson;
            }
        }

        // --- Menu -------------------------------------------------------------------------------------------

        List<IMenuItem> PlacementItems()
        {
            string forced = core.State.Path == null ? null : PlugPlacement.Forced(core.State.Path);
            string chosen = PlugPlacement.Normalize(core.State.Placement);
            var list = new List<IMenuItem>();
            void Add(string text, string value, bool allowed = true) => list.Add(new MenuItemVM
            {
                Text = text,
                IsCheckable = true,
                IsChecked = chosen == value,
                Command = new SimpleCommand { CanExecuteDelegate = _ => allowed, ExecuteDelegate = _ => SetPlacement(value) },
            });
            Add("Default", PlugPlacement.Auto);
            Add("In process", PlugPlacement.InProcess, forced == null);
            Add("Sandbox per plug-in", PlugPlacement.PerPlugin);
            Add("Sandbox per instance", PlugPlacement.PerInstance);
            Add("Sandbox shared", PlugPlacement.Shared);
            list.Add(new MenuItemVM
            {
                Text = "Remember for this plug-in",
                Command = new SimpleCommand
                {
                    CanExecuteDelegate = _ => core.State.Name != null,
                    ExecuteDelegate = _ => PlugPlacement.SetForPlugin(core.State.Format, core.State.Id, core.State.Name, chosen.Length > 0 ? chosen : core.Placement),
                },
            });
            return list;
        }

        public IEnumerable<IMenuItem> Commands
        {
            get
            {
                var items = new List<IMenuItem>();
                string title = core.Missing ? "Missing: " + (core.State.Display ?? core.State.Name) : core.State.Display ?? core.State.Name ?? "no plug-in";
                items.Add(new MenuItemVM { Text = title, Command = new SimpleCommand { CanExecuteDelegate = _ => false, ExecuteDelegate = _ => { } } });
                items.Add(new MenuItemVM { Text = "Runs: " + core.PlacementText(), Command = new SimpleCommand { CanExecuteDelegate = _ => false, ExecuteDelegate = _ => { } } });
                items.Add(new MenuItemVM
                {
                    Text = "Placement",
                    Children = PlacementItems(),
                });
                items.Add(new MenuItemVM
                {
                    Text = "Reload plug-in",
                    Command = new SimpleCommand { CanExecuteDelegate = _ => core.Crashed || core.IsOpen, ExecuteDelegate = _ => Reload() },
                });
                if (core.OutputCount > 1)
                {
                    var outs = new List<IMenuItem>();
                    for (int o = 0; o < core.OutputCount; o++)
                    {
                        int bus = o;
                        outs.Add(new MenuItemVM { Text = core.OutputNames[o], IsCheckable = true, IsChecked = core.OutBus == o, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => SetOutput(bus) } });
                    }
                    items.Add(new MenuItemVM { Text = "Output", Children = outs });
                }
#if !FX
                var wires = host.Machine?.Graph?.Buzz as INoteWireHost;
                if (wires != null)
                {
                    var wired = new HashSet<string>(wires.NoteTargetsOf(host.Machine), StringComparer.Ordinal);
                    var targets = new List<IMenuItem>
                    {
                        new MenuItemVM { Text = "(none)", IsCheckable = true, IsChecked = wired.Count == 0, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => wires.SetNoteTarget(host.Machine, "") } },
                    };
                    foreach (var name in wires.NoteTargetCandidates(host.Machine))
                    {
                        string target = name;
                        bool on = wired.Contains(target);
                        targets.Add(new MenuItemVM { Text = target, IsCheckable = true, IsChecked = on, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => { if (on) wires.DisconnectNotes(host.Machine, target); else wires.ConnectNotes(host.Machine, target); } } });
                    }
                    items.Add(new MenuItemVM { Text = "Send notes to", Children = targets });
                    items.Add(new MenuItemVM { Text = "Plug-in makes notes", IsCheckable = true, IsChecked = core.NotesOut, Command = new SimpleCommand { CanExecuteDelegate = _ => true, ExecuteDelegate = _ => core.NotesOut = !core.NotesOut } });
                }
#endif
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
