// Fork (docs/specs/studio-mvp.md S2): the parameter window's changes as undo steps on the song's
// undo stack (Edit → Undo, and the window's own undo and redo buttons):
//  - a slider gesture (one step per drag, page press or value typed; wheel and arrow steps on one
//    parameter merge while they follow each other);
//  - a preset chosen or pasted, and the edit functions (randomize, morph), as the WPF window's
//    SelectPresetAction and ApplyEditFunctionAction do: the machine's state before and after.
// Each finds its machine by name when it runs, as BuzzGUI's actions and the agent's do, so undo
// works after the machine is deleted and made again. The WPF window keeps presets and edit
// functions on a stack of its own and leaves slider moves out of undo.

using System;
using System.Linq;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Parameters
{
    internal static class ParameterEdits
    {
        /// <summary>SetValue's flag: not recorded into patterns while recording (undo and redo aren't playing).</summary>
        const int DoNotRecord = 1 << 16;

        static IMachine Find(ReBuzzCore core, string name) => core.SongCore.MachinesList.FirstOrDefault(m => m.Name == name);

        /// <summary>Sets a value as the user plays it (recorded while recording, as the WPF window does).</summary>
        public static void SetLive(IParameter p, int track, int value)
        {
            p.SetValue(track, value);
            if (p.Group.Machine.DLL.Info.Version >= 42) p.Group.Machine.SendControlChanges();
        }

        /// <summary>
        /// A value the user has already set live (<paramref name="from"/> → <paramref name="to"/>) as one
        /// undo step. Stepped changes (wheel, arrow keys) join the previous step on the same parameter
        /// while they follow it within a second and nothing was undone.
        /// </summary>
        public static void Changed(ReBuzzCore core, IParameter p, int track, int from, int to, bool stepped)
        {
            if (core == null || from == to) return;
            var stack = core.SongCore.ActionStack;
            if (stepped && !stack.CanRedo && stack.Actions.LastOrDefault() is ParameterChange last && last.Extends(p, track))
            {
                last.Extend(to);
                return;
            }
            stack.Do(new ParameterChange(core, p, track, from, to, stepped));
        }

        /// <summary>The machine's state replaced by a preset (chosen, pasted): one undo step.</summary>
        public static void ApplyPreset(ReBuzzCore core, IMachine machine, Preset preset)
        {
            if (core == null || preset == null) return;
            core.SongCore.ActionStack.Do(new PresetChange(core, machine, () => preset));
        }

        internal sealed class ParameterChange : IAction, BuzzGUI.Common.IHistoryAction
        {
            readonly ReBuzzCore core;
            readonly string parameterName;
            readonly string machine;
            readonly int group, index, track;
            readonly int from;
            readonly bool stepped;
            int to;
            DateTime made = DateTime.UtcNow;

            public ParameterChange(ReBuzzCore core, IParameter p, int track, int from, int to, bool stepped)
            {
                this.core = core;
                machine = p.Group.Machine.Name;
                parameterName = p.Name;
                group = p.Group.Machine.ParameterGroups.IndexOf(p.Group);
                index = p.IndexInGroup;
                this.track = track;
                this.from = from;
                this.to = to;
                this.stepped = stepped;
            }

            string BuzzGUI.Common.IHistoryAction.HistoryName => "Set " + machine + " " + parameterName;
            long BuzzGUI.Common.IHistoryAction.HistoryBytes => 0;
            System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<object, long>> BuzzGUI.Common.IHistoryAction.HistoryBlobs => null;

            public bool Extends(IParameter p, int t) =>
                stepped && t == track && p.IndexInGroup == index && p.Group.Machine.Name == machine
                && p.Group.Machine.ParameterGroups.IndexOf(p.Group) == group && (DateTime.UtcNow - made).TotalSeconds < 1;

            public void Extend(int value)
            {
                to = value;
                made = DateTime.UtcNow;
            }

            public void Do() => Set(to);

            public void Undo() => Set(from);

            void Set(int v)
            {
                var m = Find(core, machine);
                if (m == null || group < 0 || group >= m.ParameterGroups.Count) return;
                var ps = m.ParameterGroups[group].Parameters;
                if (index >= ps.Count) return;
                ps[index].SetValue(track | DoNotRecord, v);
                if (m.DLL.Info.Version >= 42) m.SendControlChanges();
            }
        }

        /// <summary>
        /// The machine's state before and after something rewrites it (a preset, a paste, an edit
        /// function): <paramref name="change"/> runs once, the first time, and returns the state to
        /// apply (or null when it set the values itself); redo and undo apply the snapshots.
        /// </summary>
        [BuzzGUI.Common.HistoryName("Load preset")]
        internal sealed class PresetChange : IAction
        {
            readonly ReBuzzCore core;
            readonly string machine;
            Func<Preset> change;
            Preset before, after;

            public PresetChange(ReBuzzCore core, IMachine machine, Func<Preset> change)
            {
                this.core = core;
                this.machine = machine.Name;
                this.change = change;
            }

            /// <summary>Whether a later change may still replace this one's result (the morph slider).</summary>
            public bool IsUpdatable { get; init; }

            public object Owner { get; init; }

            public void Do()
            {
                var m = Find(core, machine);
                if (m == null) return;
                before = new Preset(m, false, true);
                if (after != null) after.Apply(m, true);
                else Run(m);
            }

            void Run(IMachine m)
            {
                var p = change?.Invoke();
                p?.Apply(m, true);
                after = new Preset(m, false, true);
            }

            /// <summary>Runs the change again on the state before it (the morph slider moving).</summary>
            public void Update(Func<Preset> newChange)
            {
                var m = Find(core, machine);
                if (m == null || before == null) return;
                change = newChange;
                before.Apply(m, true);
                Run(m);
            }

            public void Undo()
            {
                var m = Find(core, machine);
                if (m != null && before != null) before.Apply(m, true);
            }
        }
    }
}
