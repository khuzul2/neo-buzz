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

        public string DisplayName => ReBuzz.Core.Drop.GearNames.Display(Instrument);
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

        /// <summary>Why Buzz's rules refuse a wire from src to dst, in plain words, or null if they allow it (P3d: shown when a drag is refused).</summary>
        public static string WhyNotConnect(ReBuzzCore core, IMachine src, IMachine dst)
        {
            if (src == null || dst == null || core.SongCore.CanConnectMachines(src, dst)) return null;
            if (src == dst) return "A machine can't connect to itself";
            if (src.DLL.Info.Type == MachineType.Master) return "The Master has no output";
            if (src.IsControlMachine || dst.IsControlMachine) return "Control machines work through parameters, not audio wires";
            if (dst.DLL.Info.Type == MachineType.Generator) return dst.Name + " is an instrument: it has no input";
            if (src.Outputs.Any(o => o.Destination == dst)) return "Already connected";
            if (dst.Outputs.Any(o => o.Destination == src)) return dst.Name + " already feeds " + src.Name + ": that would make a loop";
            return "That would make a loop";
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

        /// <summary>Duplicate (⌘D): each machine cloned with its settings, one undo step (ReBuzz.Core.MachineEditing, as the agent's machine_clone).</summary>
        public static List<IMachine> Duplicate(ReBuzzCore core, IReadOnlyList<IMachine> machines) => MachineEditing.Duplicate(core, machines);

        /// <summary>Clone with patterns (BuzzGUI.MachineView's CloneSelectedMachines(true)): see <see cref="MachineEditing.CloneWithPatterns"/>.</summary>
        public static List<IMachine> CloneWithPatterns(ReBuzzCore core, IReadOnlyList<IMachine> machines) => MachineEditing.CloneWithPatterns(core, machines);

        // ---- machine groups (BuzzGUI.MachineView's Group menu): the steps are ReBuzz.Core.MachineEditing's, which the agent's machine_group also calls.

        /// <summary>"New Group": a group named New Group a little beyond the selection, with the machines in it (the Master never).</summary>
        public static IMachineGroup NewGroup(ReBuzzCore core, IEnumerable<IMachine> machines) => MachineEditing.NewGroup(core, machines);

        /// <summary>The machines into <paramref name="group"/> (out of the one they were in), their places kept for when it is unfolded.</summary>
        public static void AddToGroup(ReBuzzCore core, IEnumerable<IMachine> machines, IMachineGroup group) => MachineEditing.AddToGroup(core, machines, group);

        /// <summary>The machines out of their groups.</summary>
        public static void RemoveFromGroup(ReBuzzCore core, IEnumerable<IMachine> machines) => MachineEditing.RemoveFromGroup(core, machines);

        /// <summary>Deletes a group, not its machines (a folded one opens first, so they are not left stacked).</summary>
        public static void DeleteGroup(ReBuzzCore core, IMachineGroup group) => MachineEditing.DeleteGroup(core, group);

        /// <summary>Folds an open group: its members onto the group's position; one undo step. Does nothing when it is folded.</summary>
        public static void Fold(ReBuzzCore core, IMachineGroup group) => MachineEditing.Fold(core, group);

        /// <summary>Opens a folded group: its members back to the places they had; one undo step. Does nothing when it is open.</summary>
        public static void Unfold(ReBuzzCore core, IMachineGroup group) => MachineEditing.Unfold(core, group);

        /// <summary>A double click on a group: folds it, or opens it. The members of a folded group leave the canvas's selection.</summary>
        public static void ToggleFold(ReBuzzCore core, IMachineGroup group, MachineCanvas canvas = null)
        {
            if (group.IsGrouped) Unfold(core, group);
            else
            {
                Fold(core, group);
                if (canvas != null && group.Machines.Any(canvas.Selection.Contains))
                    canvas.Select(canvas.Selection.Where(m => !group.Machines.Contains(m)).ToList());
            }
            canvas?.Redraw();
        }

        /// <summary>
        /// A group moved to a song position, with its members to theirs (a drag): one undo step. A folded group's saved places move
        /// by the same distance, so it opens where it was, relative to the box.
        /// </summary>
        public static void MoveGroup(ReBuzzCore core, IMachineGroup group, float x, float y, IReadOnlyList<(IMachine Machine, float X, float Y)> members)
        {
            var song = core.SongCore;
            float dx = x - group.Position.Item1, dy = y - group.Position.Item2;
            var saved = group.IsGrouped
                ? group.Machines.Select(m => { var p = group.GetMachinePosition(m); return Tuple.Create(m, Tuple.Create(p.Item1 + dx, p.Item2 + dy)); }).ToList()
                : null;
            using (Group(core))
            {
                song.MoveMachineGroups(new[] { Tuple.Create(group, Tuple.Create(x, y)) });
                Move(core, members);
                if (saved != null) song.UpdateGroupedMachinesPositions(saved);
            }
        }

        /// <summary>Renames a group (an undo step); a name another group has gets Buzz's " | 2". False for an empty name or none to change.</summary>
        public static bool RenameGroup(ReBuzzCore core, IMachineGroup group, string name) => MachineEditing.RenameGroup(core, group, name);

        /// <summary>How far a copy lands from its machine, in song coordinates (the canvas spans −1 to 1).</summary>
        public const float DuplicateOffset = MachineEditing.DuplicateOffset;

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
