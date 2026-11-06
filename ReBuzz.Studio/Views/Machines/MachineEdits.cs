// Fork (docs/specs/studio-mvp.md S3): what the machine view does to the song, each through the
// engine's undoable operations, as BuzzGUI.MachineView does (MachineView, MachineControl,
// AmpControl). Mute, solo, bypass and a wire's amp and pan, which ReBuzz doesn't make undoable,
// use the engine's own undo steps for them (ReBuzz.Agent.AgentActions, the MCP server's).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Machines
{
    /// <summary>An installed machine (or a loader's instrument) as the machine list shows it (MachineListItemVM).</summary>
    internal sealed class GearItem
    {
        public GearItem(IInstrument instrument) => Instrument = instrument;

        public IInstrument Instrument { get; }

        public string DisplayName
        {
            get
            {
                if (Instrument.Name.Length > 0)
                {
                    string name = Path.GetFileName(Instrument.Name);
                    if (Instrument.Name.StartsWith("VST3/")) name += "(vst3)";
                    return name;
                }
                var dll = Instrument.MachineDLL.Name;
                return Instrument.MachineDLL.IsOutOfProcess ? dll + " (32-bit)" : dll;
            }
        }
    }

    internal static class MachineEdits
    {
        /// <summary>One undo step for everything done inside (BuzzGUI's ActionGroup).</summary>
        public static IDisposable Group(ReBuzzCore core) => new ActionGroupScope(core.SongCore);

        sealed class ActionGroupScope : IDisposable
        {
            readonly SongCore song;

            public ActionGroupScope(SongCore song)
            {
                this.song = song;
                song.BeginActionGroup();
            }

            public void Dispose() => song.EndActionGroup();
        }

        /// <summary>
        /// The installed machines by kind, as the machine list's Generator / Effect / Control
        /// filters show them, sorted by name. Not Master, nor pattern editors.
        /// </summary>
        public static IReadOnlyList<(string Kind, IReadOnlyList<GearItem> Items)> GearByKind(ReBuzzCore core)
        {
            var usable = core.Instruments.Where(i => i.MachineDLL?.Info != null
                                                     && i.MachineDLL.Info.Type != MachineType.Master
                                                     && !i.MachineDLL.Info.Flags.HasFlag(MachineInfoFlags.PATTERN_EDITOR));
            IReadOnlyList<GearItem> Of(InstrumentType t) =>
                usable.Where(i => i.Type == t).Select(i => new GearItem(i)).OrderBy(g => g.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
            return new (string, IReadOnlyList<GearItem>)[]
            {
                ("Generators", Of(InstrumentType.Generator)),
                ("Effects", Of(InstrumentType.Effect)),
                ("Control", Of(InstrumentType.Control)),
            };
        }

        /// <summary>
        /// A new machine at a song position (MachineView.CreateInstrument): a pattern if it has
        /// parameters; a generator gets a sequence track, is wired to Master while the song has no
        /// effects, and plays its pattern at the start of an empty song. One undo step.
        /// </summary>
        public static IMachine Create(ReBuzzCore core, IInstrument instrument, float x, float y)
        {
            var song = core.SongCore;
            using (Group(core))
            {
                bool songWasEmpty = song.Machines.Count == 1;
                var before = song.Machines.ToHashSet();
                (x, y) = MachinePlacement.Free(song.Machines, x, y);      // not on top of another (the Master, gallery mac-510)
                song.CreateMachine(instrument.MachineDLL.Name, instrument.Name, null, null, null, null, -1, x, y);
                var machine = song.Machines.FirstOrDefault(m => !before.Contains(m));
                if (machine == null) return null;

                if (machine.AllNonInputParameters().Any())
                    machine.CreatePattern("00", Global.GeneralSettings.PatternLength > 0 ? Global.GeneralSettings.PatternLength : 16);

                if (machine.DLL.Info.Type == MachineType.Generator)
                {
                    var master = song.Machines.FirstOrDefault(m => m.DLL.Info.Type == MachineType.Master);
                    if (!machine.IsControlMachine && master != null && !song.Machines.Any(m => m.DLL.Info.Type == MachineType.Effect))
                        song.ConnectMachines(machine, master, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);

                    song.AddSequence(machine, song.Sequences.Count);
                    if (songWasEmpty && machine.Patterns.Count > 0)
                        song.Sequences.First(s => s.Machine == machine).SetEvent(0, new SequenceEvent(SequenceEventType.PlayPattern, machine.Patterns[0]));
                }
                return machine;
            }
        }

        /// <summary>Machines to their new positions (MachineView.EndMoveSelectedMachines).</summary>
        public static void Move(ReBuzzCore core, IEnumerable<(IMachine Machine, float X, float Y)> moves)
        {
            var mm = moves.Select(m => Tuple.Create(m.Machine, Tuple.Create(m.X, m.Y))).ToList();
            if (mm.Count > 0) core.SongCore.MoveMachines(mm);
        }

        /// <summary>A new wire, at 0 dB and centred (MachineControl's connect drag); false if Buzz's rules say no.</summary>
        public static bool Connect(ReBuzzCore core, IMachine src, IMachine dst)
        {
            if (src == null || dst == null || !core.SongCore.CanConnectMachines(src, dst)) return false;
            core.SongCore.ConnectMachines(src, dst, new[] { 0 }, new[] { 0 }, 0x4000, 0x4000);
            return true;
        }

        public static void Disconnect(ReBuzzCore core, IMachineConnection c) => core.SongCore.DisconnectMachines(c);

        /// <summary>
        /// A dragged effect with no wires, dropped on a wire, goes into it (MachineControl's move
        /// drag, EndDrag): the move and both new wires are one undo step.
        /// </summary>
        public static void MoveIntoWire(ReBuzzCore core, IMachine machine, float x, float y, IMachineConnection wire)
        {
            using (Group(core))
            {
                Move(core, new[] { (machine, x, y) });
                var src = wire.Source;
                var dst = wire.Destination;
                int srcChannel = wire.SourceChannel, dstChannel = wire.DestinationChannel, amp = wire.Amp;
                core.SongCore.DisconnectMachines(wire);
                core.SongCore.ConnectMachines(src, machine, new[] { srcChannel }, new[] { 0 }, amp, 0x4000);
                core.SongCore.ConnectMachines(machine, dst, new[] { 0 }, new[] { dstChannel }, 0x4000, 0x4000);
            }
        }

        /// <summary>
        /// Deletes machines (MachineView.DeleteMachines). One machine with wires on both sides has
        /// its sources rewired to its destinations, gains multiplied and pans added, in the same
        /// undo step. Master stays.
        /// </summary>
        public static void Delete(ReBuzzCore core, IEnumerable<IMachine> machines)
        {
            var song = core.SongCore;
            var list = machines.Where(m => m.DLL.Info.Type != MachineType.Master).ToList();
            if (list.Count == 0) return;
            var m = list[0];
            if (list.Count == 1 && (m.Inputs.Count >= 1 || m.Outputs.Count >= 1))
            {
                using (Group(core))
                {
                    var oldInputs = m.Inputs.Select(x => (Source: x.Source, Channels: x.SourceChannels.ToArray(), Amp: (double)x.Amp / 0x4000, Pan: x.Pan)).ToArray();
                    var oldOutputs = m.Outputs.Select(x => (Destination: x.Destination, Channels: x.DestinationChannels.ToArray(), Amp: (double)x.Amp / 0x4000, Pan: x.Pan)).ToArray();
                    song.DeleteMachines(list);
                    foreach (var i in oldInputs)
                        foreach (var o in oldOutputs)
                            if (song.CanConnectMachines(i.Source, o.Destination))
                            {
                                int amp = Math.Min(Math.Max((int)(i.Amp * o.Amp * 0x4000), 0), 0xfffe);
                                int pan = Math.Min(Math.Max(i.Pan + o.Pan - 0x4000, 0), 0x8000);
                                song.ConnectMachines(i.Source, o.Destination, i.Channels, o.Channels, amp, pan);
                            }
                }
            }
            else
            {
                song.DeleteMachines(list);
            }
        }

        /// <summary>Mutes or unmutes machines, not Master or control machines (MachineView.MuteMachines).</summary>
        public static void Mute(ReBuzzCore core, IEnumerable<IMachine> machines, bool on) =>
            SetFlag(core, machines.Where(m => m.DLL.Info.Type != MachineType.Master && !m.IsControlMachine), AgentActions.Flag.Mute, on);

        /// <summary>Solo applies to generators (MachineView.SoloMachines).</summary>
        public static void Solo(ReBuzzCore core, IEnumerable<IMachine> machines, bool on) =>
            SetFlag(core, machines.Where(m => m.DLL.Info.Type == MachineType.Generator && !m.IsControlMachine), AgentActions.Flag.Solo, on);

        /// <summary>Bypass applies to effects (MachineView.BypassMachines).</summary>
        public static void Bypass(ReBuzzCore core, IEnumerable<IMachine> machines, bool on) =>
            SetFlag(core, machines.Where(m => m.DLL.Info.Type == MachineType.Effect && !m.IsControlMachine), AgentActions.Flag.Bypass, on);

        /// <summary>Unmute all machines and clear solo (Ctrl+U).</summary>
        public static void UnmuteAll(ReBuzzCore core)
        {
            using (Group(core))
            {
                SetFlag(core, core.SongCore.Machines.Where(m => m.IsMuted), AgentActions.Flag.Mute, false);
                SetFlag(core, core.SongCore.Machines.Where(m => m.IsSoloed), AgentActions.Flag.Solo, false);
            }
        }

        static void SetFlag(ReBuzzCore core, IEnumerable<IMachine> machines, AgentActions.Flag flag, bool on)
        {
            var list = machines.OfType<MachineCore>().ToList();
            if (list.Count == 0) return;
            using (Group(core))
                foreach (var m in list)
                    AgentActions.SetFlag(core, m, flag, on);
        }

        /// <summary>
        /// A wire's amp and pan after the amp control (AmpControl.Deactivate). The control set them
        /// live while dragging; this puts the old values back and makes the change one undo step.
        /// </summary>
        public static void SetWire(ReBuzzCore core, IMachineConnection c, int oldAmp, int oldPan, int newAmp, int newPan)
        {
            if (oldAmp == newAmp && oldPan == newPan) return;
            core.SongCore.SetConnectionParameter(c, 0, newAmp, oldAmp);
            if (c.HasPan) core.SongCore.SetConnectionParameter(c, 1, newPan, oldPan);
            AgentActions.SetConnection(core, c, newAmp, newPan);
        }

        /// <summary>Asks for a new name (IMachineUiHost.AskName) and renames, undoably (MachineDialog.Rename).</summary>
        public static void Rename(IMachine machine) => machine.ShowDialog(MachineDialog.Rename, 0, 0);

        /// <summary>The machine's parameter window (MachineDialog.Parameters).</summary>
        public static void OpenParameters(IMachine machine) => machine.ShowDialog(MachineDialog.Parameters, 0, 0);

        /// <summary>A double click: the machine's own GUI (its parameter window when it has none here).</summary>
        public static void OpenGui(ReBuzzCore core, IMachine machine) => core.UiHost?.OpenGui(machine, null);

        /// <summary>
        /// U5 (mcp-server.md §12.5): the machine onto its Neo successor, as machine_replace does it (settings in units, patterns,
        /// arrangement, Session View), one undo step; the comparison of before and after in a message. Returns that text, or null.
        /// </summary>
        public static string ReplaceWithSuccessor(ReBuzzCore core, MachineCore machine, string gear) =>
            MachineReplace.FromMenu(core, machine, gear);

        public static void Replace(ReBuzzCore core, IMachine machine, IInstrument with) =>
            core.SongCore.ReplaceMachine(machine, with.MachineDLL.Name, with.Name, machine.Position.Item1, machine.Position.Item2);

        /// <summary>A new effect in a wire, halfway between its machines (the amp control's insert button).</summary>
        public static void InsertIntoWire(ReBuzzCore core, IMachineConnection wire, IInstrument effect)
        {
            var p1 = wire.Source.Position;
            var p2 = wire.Destination.Position;
            core.SongCore.InsertMachine(wire, effect.MachineDLL.Name, effect.Name, 0.5f * (p1.Item1 + p2.Item1), 0.5f * (p1.Item2 + p2.Item2));
        }

        /// <summary>Machines onto the snap grid (MachineView.ArrangeView, "Snap Machines"); one undo step.</summary>
        public static void Snap(ReBuzzCore core, IEnumerable<IMachine> machines, int gridSize)
        {
            float step = gridSize * 10;
            using (Group(core))
                Move(core, machines.Select(m => (m,
                    step * (float)Math.Floor(m.Position.Item1 * 1000 / step + 0.5) / 1000,
                    step * (float)Math.Floor(m.Position.Item2 * 1000 / step + 0.5) / 1000)).ToList());
        }
    }
}
