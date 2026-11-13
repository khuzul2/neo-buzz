// Fork (docs/specs/history-snapshots.md HS1): a MIDI controller's moves of one parameter, while the transport is stopped,
// as one step of the song's history (a gesture: moves within an idle gap of each other merge). While the song plays a
// controller move is performance and isn't recorded.

using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Actions
{
    internal sealed class ControllerMoveAction : IAction, ICoalescingAction, IHistoryAction
    {
        /// <summary>A controller's moves within this idle gap are one step.</summary>
        public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(500);

        /// <summary>SetValue's flag: not recorded into patterns while recording (undo and redo aren't playing).</summary>
        const int DoNotRecord = 1 << 16;

        readonly ReBuzzCore core;
        readonly string machine;
        readonly string parameterName;
        readonly int group, index, track;
        readonly int before;
        int after;

        public ControllerMoveAction(ReBuzzCore core, IParameter p, int track, int before, int after)
        {
            this.core = core;
            machine = p.Group.Machine.Name;
            parameterName = p.Name;
            group = p.Group.Machine.ParameterGroups.IndexOf(p.Group);
            index = p.IndexInGroup;
            this.track = track;
            this.before = before;
            this.after = after;
        }

        public string Machine => machine;
        public int Before => before;
        public int After => after;

        string IHistoryAction.HistoryName => "MIDI: " + machine + " " + parameterName;
        long IHistoryAction.HistoryBytes => 0;
        IEnumerable<KeyValuePair<object, long>> IHistoryAction.HistoryBlobs => null;

        public void Do() => Set(after);

        public void Undo() => Set(before);

        public bool TryMerge(IAction later)
        {
            if (later is not ControllerMoveAction m || m.machine != machine || m.group != group || m.index != index || m.track != track) return false;
            after = m.after;
            return true;
        }

        void Set(int v)
        {
            var m = core.SongCore.MachinesList.FirstOrDefault(x => x.Name == machine);
            if (m == null || group < 0 || group >= m.ParameterGroups.Count) return;
            var ps = m.ParameterGroups[group].Parameters;
            if (index >= ps.Count) return;
            ps[index].SetValue(track | DoNotRecord, v);
            if (m.DLL.Info.Version >= 42) m.SendControlChanges();
        }
    }
}
