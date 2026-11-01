// Agent server (docs/specs/mcp-server.md §3.2): undoable steps for what ReBuzz doesn't already
// make undoable (parameter sets, connection gains, mute/solo/bypass, notes, pattern renames,
// tempo). Each finds its machine and pattern by name when it runs, as BuzzGUI's own actions do,
// so undo and redo work after machines are deleted and made again.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Agent
{
    /// <summary>An undo step from two delegates.</summary>
    internal sealed class DelegateAction : IAction
    {
        readonly Action doIt, undoIt;

        public DelegateAction(Action doIt, Action undoIt)
        {
            this.doIt = doIt;
            this.undoIt = undoIt;
        }

        public void Do() => doIt();
        public void Undo() => undoIt();
    }

    internal static class AgentActions
    {
        static MachineCore Find(ReBuzzCore core, string machine) => core.SongCore.MachinesList.FirstOrDefault(m => m.Name == machine);

        static IPattern Find(ReBuzzCore core, string machine, string pattern) => Find(core, machine)?.Patterns.FirstOrDefault(p => p.Name == pattern);

        public static void Do(ReBuzzCore core, IAction action) => core.SongCore.ActionStack.Do(action);

        /// <summary>Sets a parameter (global: track 0).</summary>
        public static void SetParameter(ReBuzzCore core, MachineCore m, IParameter p, int track, int value)
        {
            int old = p.GetValue(track), group = p.Group.Type == ParameterGroupType.Track ? 2 : 1, index = p.IndexInGroup;
            string name = m.Name;
            void Set(int v)
            {
                var machine = Find(core, name);
                if (machine == null || group >= machine.ParameterGroups.Count) return;
                var ps = machine.ParameterGroups[group].Parameters;
                // Not recorded into patterns or session automation while the user records.
                if (index < ps.Count) ps[index].SetValue(track | (1 << 16), v);
            }
            Do(core, new DelegateAction(() => Set(value), () => Set(old)));
        }

        /// <summary>Sets a connection's amp and pan (Buzz values).</summary>
        public static void SetConnection(ReBuzzCore core, IMachineConnection c, int amp, int pan)
        {
            string from = c.Source.Name, to = c.Destination.Name;
            int oldAmp = c.Amp, oldPan = c.Pan;
            void Set(int a, int p)
            {
                var conn = Find(core, from)?.Outputs.FirstOrDefault(x => x.Destination.Name == to);
                if (conn == null) return;
                core.SongCore.SetConnectionParameter(conn, 0, conn.Amp, a);
                if (conn.HasPan) core.SongCore.SetConnectionParameter(conn, 1, conn.Pan, p);
            }
            Do(core, new DelegateAction(() => Set(amp, pan), () => Set(oldAmp, oldPan)));
        }

        public enum Flag { Mute, Solo, Bypass }

        public static void SetFlag(ReBuzzCore core, MachineCore m, Flag flag, bool on)
        {
            string name = m.Name;
            bool old = flag switch { Flag.Mute => m.IsMuted, Flag.Solo => m.IsSoloed, _ => m.IsBypassed };
            void Set(bool v)
            {
                var machine = Find(core, name);
                if (machine == null) return;
                switch (flag)
                {
                    case Flag.Mute: machine.IsMuted = v; break;
                    case Flag.Solo: machine.IsSoloed = v; break;
                    default: machine.IsBypassed = v; break;
                }
            }
            Do(core, new DelegateAction(() => Set(on), () => Set(old)));
        }

        /// <summary>Replaces notes in a pattern's engine MIDI column: <paramref name="removed"/> go, <paramref name="added"/> come.</summary>
        public static void ReplaceNotes(ReBuzzCore core, IPattern pattern, IReadOnlyList<NoteEvent> removed, IReadOnlyList<NoteEvent> added)
        {
            string machine = pattern.Machine.Name, name = pattern.Name;
            var take = removed.Select(ToEvent).ToArray();
            var put = added.Select(ToEvent).ToArray();
            void Apply(PatternEvent[] gone, PatternEvent[] come)
            {
                var p = Find(core, machine, name);
                if (p == null) return;
                var column = SessionLayout.ColumnFor(p, ColumnKey.Midi, create: come.Length > 0);
                if (column == null) return;
                if (gone.Length > 0) column.SetEvents(gone, false);
                if (come.Length > 0) column.SetEvents(come, true);
            }
            Do(core, new DelegateAction(() => Apply(take, put), () => Apply(put, take)));
        }

        static PatternEvent ToEvent(NoteEvent n) => new PatternEvent(n.Time, n.Value, n.Duration);

        /// <summary>Deletes a pattern; undo makes it again with its engine columns (notes, automation).</summary>
        public static void DeletePattern(ReBuzzCore core, IPattern pattern)
        {
            string machine = pattern.Machine.Name, name = pattern.Name;
            int length = pattern.Length;
            EngineColumns data = null;
            IPattern deleted = null;
            void Delete()
            {
                var m = Find(core, machine);
                var p = m?.Patterns.FirstOrDefault(x => x.Name == name);
                if (p == null) return;
                data = EngineColumns.Capture(p);
                deleted = p;
                m.DeletePattern(p);
            }
            void Restore()
            {
                var m = Find(core, machine);
                if (m == null || data == null || m.Patterns.Any(x => x.Name == name)) return;
                m.CreatePattern(name, length);
                var again = m.Patterns.FirstOrDefault(x => x.Name == name);
                if (again == null) return;
                data.RestoreInto(again, core.SongCore.Machines);
                BuzzGUI.Common.Actions.Restorations.Report(deleted, again);
            }
            Do(core, new DelegateAction(Delete, Restore));
        }

        /// <summary>Copies a pattern: the pattern editor's data (when it has an editor) and the engine columns.</summary>
        public static void DuplicatePattern(ReBuzzCore core, IPattern source, string copyName)
        {
            string machine = source.Machine.Name, from = source.Name;
            void Copy()
            {
                var m = Find(core, machine);
                var src = m?.Patterns.FirstOrDefault(x => x.Name == from);
                if (src == null || m.Patterns.Any(x => x.Name == copyName)) return;
                if (m.EditorMachine != null) m.ClonePattern(copyName, src);
                else m.CreatePattern(copyName, src.Length);
                var copy = m.Patterns.FirstOrDefault(x => x.Name == copyName);
                if (copy != null) EngineColumns.Copy(src, copy, core.SongCore.Machines);
            }
            void Remove()
            {
                var m = Find(core, machine);
                var copy = m?.Patterns.FirstOrDefault(x => x.Name == copyName);
                if (copy != null) m.DeletePattern(copy);
            }
            Do(core, new DelegateAction(Copy, Remove));
        }

        public static void RenamePattern(ReBuzzCore core, IPattern pattern, string newName)
        {
            string machine = pattern.Machine.Name, oldName = pattern.Name;
            void Rename(string from, string to)
            {
                var m = Find(core, machine);
                var p = m?.Patterns.FirstOrDefault(x => x.Name == from);
                if (p != null) m.RenamePattern(p, to);
            }
            Do(core, new DelegateAction(() => Rename(oldName, newName), () => Rename(newName, oldName)));
        }

        /// <summary>Tempo, ticks per beat and looping (the song's markers have their own action).</summary>
        public static void SetTransport(ReBuzzCore core, int bpm, int tpb, bool looping)
        {
            int oldBpm = core.BPM, oldTpb = core.TPB;
            bool oldLooping = core.Looping;
            void Set(int b, int t, bool l)
            {
                if (core.TPB != t) core.TPB = t;
                if (core.BPM != b) core.BPM = b;
                if (core.Looping != l) core.Looping = l;
            }
            Do(core, new DelegateAction(() => Set(bpm, tpb, looping), () => Set(oldBpm, oldTpb, oldLooping)));
        }
    }
}
