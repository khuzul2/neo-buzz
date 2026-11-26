// Fork (docs/specs/ux-overhaul.md P6b/P6c, agent-knowledge.md K3b): what the studio's machine view does to machines that is not
// drawing: clone (with or without clips) and machine groups. The studio's MachineEdits calls these, and so do the agent's
// machine_clone and machine_group tools, so a person and an agent get the same steps. Each is one undo step.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    internal static class MachineEditing
    {
        /// <summary>How far a copy lands from its machine, in song coordinates.</summary>
        public const float DuplicateOffset = 0.06f;

        /// <summary>Positions stay on the canvas (it spans -2 to 2: MachineGeometry.CanvasSize).</summary>
        public const float CanvasSize = 2;

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

        static float Clamp(float v) => Math.Clamp(v, -CanvasSize, CanvasSize);

        /// <summary>
        /// Each machine cloned with its settings (SongCore.CloneMachine, as WPF's Clone), offset down and to the right, one undo step.
        /// The copies, in the order given.
        /// </summary>
        public static List<IMachine> Duplicate(ReBuzzCore core, IReadOnlyList<IMachine> machines)
        {
            var song = core.SongCore;
            var before = song.Machines.ToHashSet();
            using (Group(core))
                foreach (var m in machines)
                    song.CloneMachine(m, Clamp(m.Position.Item1 + DuplicateOffset), Clamp(m.Position.Item2 + DuplicateOffset));
            return song.Machines.Where(m => !before.Contains(m) && !(m is MachineCore mc && mc.Hidden)).ToList();
        }

        /// <summary>
        /// Clone with clips (BuzzGUI.MachineView's CloneSelectedMachines(true)): each machine again with its settings (as Duplicate does),
        /// its clips and its rows in the sequence (the same events, on the copy's clips), and the wires between the machines
        /// cloned together; one undo step. The copies, in the order given. (The WPF app builds a template for this; the template code
        /// is WPF-only, so this does the same steps with the engine's own operations.)
        /// </summary>
        public static List<IMachine> CloneWithClips(ReBuzzCore core, IReadOnlyList<IMachine> machines)
        {
            var song = core.SongCore;
            var copies = new Dictionary<IMachine, IMachine>();
            using (Group(core))
            {
                foreach (var m in machines)
                {
                    var before = song.Machines.ToHashSet();
                    song.CloneMachine(m, Clamp(m.Position.Item1 + DuplicateOffset), Clamp(m.Position.Item2 + DuplicateOffset));
                    var copy = song.Machines.FirstOrDefault(x => !before.Contains(x) && !(x is MachineCore mc && mc.Hidden));
                    if (copy == null) continue;
                    copies[m] = copy;
                    foreach (var p in m.Clips.ToList()) copy.CloneClip(p.Name, p);
                    foreach (var seq in song.Sequences.Where(q => q.Machine == m).ToList())
                    {
                        song.AddSequence(copy, song.Sequences.Count);
                        var made = song.Sequences[song.Sequences.Count - 1];
                        foreach (var e in seq.Events.OrderBy(k => k.Key))
                        {
                            var clip = e.Value.Type == SequenceEventType.PlayClip ? copy.Clips.FirstOrDefault(q => q.Name == e.Value.Clip?.Name) : null;
                            if (e.Value.Type == SequenceEventType.PlayClip && clip == null) continue;
                            made.SetEvent(e.Key, new SequenceEvent(e.Value.Type, clip) { Span = e.Value.Span, Offset = e.Value.Offset, End = e.Value.End, Gain = e.Value.Gain });
                        }
                    }
                }
                foreach (var m in machines)
                    foreach (var c in m.Outputs.ToList())
                        if (copies.TryGetValue(m, out var from) && copies.TryGetValue(c.Destination, out var to) && song.CanConnectMachines(from, to))
                            song.ConnectMachines(from, to, c.SourceChannels.ToList(), c.DestinationChannels.ToList(), c.Amp, c.Pan);
            }
            return machines.Where(copies.ContainsKey).Select(m => copies[m]).ToList();
        }

        // ---- machine groups (BuzzGUI.MachineView's Group menu). As there, making and deleting a group are undo steps and moving a
        // machine in or out of one is not (SongCore.AddMachineToGroup); a group is drawn as a frame round its members.

        /// <summary>"New Group": a group named New Group a little beyond the selection, with the machines in it (the Master never).</summary>
        public static IMachineGroup NewGroup(ReBuzzCore core, IEnumerable<IMachine> machines, string name = "New Group")
        {
            var members = machines.Where(m => m.DLL.Info.Type != MachineType.Master).ToList();
            if (members.Count == 0) return null;
            var song = core.SongCore;
            float x = members.Max(m => m.Position.Item1) + 0.2f, y = members.Max(m => m.Position.Item2) + 0.2f;
            var before = song.MachineGroups.ToHashSet();
            song.CreateMachineGroup(name, x, y);
            var group = song.MachineGroups.LastOrDefault(g => !before.Contains(g));
            if (group != null) AddToGroup(core, members, group);
            return group;
        }

        /// <summary>The machines into <paramref name="group"/> (out of the one they were in), their places kept for when it is unfolded.</summary>
        public static void AddToGroup(ReBuzzCore core, IEnumerable<IMachine> machines, IMachineGroup group)
        {
            var song = core.SongCore;
            foreach (var m in machines.Where(m => m.DLL.Info.Type != MachineType.Master).ToList())
            {
                song.AddMachineToGroup(m, group);
                song.UpdateGroupedMachinesPositions(new[] { Tuple.Create(m, m.Position) });
                // Into a folded group: onto its box (GroupSelectedMachines).
                if (group.IsGrouped) song.MoveMachines(new[] { Tuple.Create(m, group.Position) });
            }
        }

        /// <summary>The machines out of their groups.</summary>
        public static void RemoveFromGroup(ReBuzzCore core, IEnumerable<IMachine> machines)
        {
            var song = core.SongCore;
            foreach (var m in machines.ToList())
            {
                // Out of a folded group: back to its own place, not left under the box.
                if (song.MachineToGroupDict.TryGetValue(m, out var g) && g.IsGrouped)
                    song.MoveMachines(new[] { Tuple.Create(m, g.GetMachinePosition(m)) });
                song.RemoveMachineFromGroup(m);
            }
        }

        /// <summary>Deletes a group, not its machines (a folded one opens first, so they are not left stacked).</summary>
        public static void DeleteGroup(ReBuzzCore core, IMachineGroup group)
        {
            using (Group(core))
            {
                Unfold(core, group);
                core.SongCore.DeleteMachineGroups(new[] { group });
            }
        }

        /// <summary>Folds an open group: its members onto the group's position, hidden by the canvas; one undo step. Does nothing when it is folded.</summary>
        public static void Fold(ReBuzzCore core, IMachineGroup group)
        {
            if (group == null || group.IsGrouped) return;
            var song = core.SongCore;
            var members = group.Machines.ToList();
            var at = group.Position;
            using (Group(core))
            {
                song.GroupMachines(group, true);
                // The places to come back to, then the move onto the group.
                song.UpdateGroupedMachinesPositions(members.Select(m => Tuple.Create(m, m.Position)));
                if (members.Count > 0) song.MoveMachines(members.Select(m => Tuple.Create(m, at)).ToList());
            }
        }

        /// <summary>Opens a folded group: its members back to the places they had; one undo step. Does nothing when it is open.</summary>
        public static void Unfold(ReBuzzCore core, IMachineGroup group)
        {
            if (group == null || !group.IsGrouped) return;
            var song = core.SongCore;
            var back = group.Machines.Select(m => Tuple.Create(m, group.GetMachinePosition(m))).ToList();
            using (Group(core))
            {
                song.GroupMachines(group, false);
                if (back.Count > 0) song.MoveMachines(back);
            }
        }

        /// <summary>Renames a group (an undo step); a name another group has gets Buzz's " | 2". False for an empty name or none to change.</summary>
        public static bool RenameGroup(ReBuzzCore core, IMachineGroup group, string name)
        {
            name = name?.Trim();
            if (string.IsNullOrEmpty(name) || name == group.Name || group is not MachineGroupCore mg) return false;
            core.SongCore.RenameMachineGroupUndoable(mg, core.SongCore.GetNewGroupName(name));
            return true;
        }

        // ---- hot-swap (docs/specs/browser.md, BR1)

        /// <summary>Makes sure the machine has clips of these names and lengths (an undo step of its own, inside a group: the machine is made again by the replace's redo, then this runs).</summary>
        sealed class EnsureClips : IAction
        {
            readonly ReBuzzCore core;
            readonly string machine;
            readonly List<(string Name, int Length)> clips;

            public EnsureClips(ReBuzzCore core, string machine, List<(string Name, int Length)> clips)
            {
                this.core = core;
                this.machine = machine;
                this.clips = clips;
            }

            public void Do()
            {
                var m = core.SongCore.Machines.FirstOrDefault(x => x.Name == machine);
                if (m == null) return;
                foreach (var (name, length) in clips)
                    if (!m.Clips.Any(p => p.Name == name)) m.CreateClip(name, length);
            }

            public void Undo() { /* the replace's undo removes the machine, and its clips with it */ }
        }

        /// <summary>
        /// Other gear in a machine's place, as one undo step: the song's Replace (the connections with their gains, the position; the same
        /// machine with another plug-in swaps the plug-in in place), then the old name back and the old clips' names and lengths made on the new
        /// machine (empty: a clip's notes and values belong to the old machine's columns). The arrangement's tracks are made again, not their clips.
        /// Returns the machine now in its place.
        /// </summary>
        public static IMachine HotSwap(ReBuzzCore core, IMachine machine, string dll, string instrument)
        {
            string name = machine.Name;
            var clips = machine.Clips.Select(p => (p.Name, p.Length)).ToList();
            bool inPlace = machine.DLL.Name == dll && !string.IsNullOrEmpty(instrument);
            using (Group(core))
            {
                core.SongCore.ReplaceMachine(machine, dll, string.IsNullOrEmpty(instrument) ? null : instrument, machine.Position.Item1, machine.Position.Item2);
                if (inPlace) return core.SongCore.Machines.FirstOrDefault(m => m.Name == name);
                var made = core.SongCore.Machines.Last() as MachineCore;
                if (made != null && made.Name != name) core.SongCore.RenameMachineUndoable(made, name);
                core.SongCore.Do(new EnsureClips(core, name, clips));
            }
            return core.SongCore.Machines.FirstOrDefault(m => m.Name == name);
        }
    }
}
