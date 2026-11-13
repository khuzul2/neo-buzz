// Fork (docs/specs/history-snapshots.md HS1): the tempo and the loop switch as steps of the song's history. The studio's
// transport bar calls these; the engine's own setters (a scene's tempo on its launch tick, a song opening, the agent's
// transport tool, which has its own step) stay as they are.

using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using System;

namespace ReBuzz.Core.Actions
{
    internal static class TransportEdits
    {
        /// <summary>A tempo field's changes within this idle gap are one step.</summary>
        public static readonly TimeSpan TempoGap = TimeSpan.FromMilliseconds(500);

        /// <summary>Sets the tempo (16-500), as one history step per gesture: changes in a run merge.</summary>
        public static void SetTempo(ReBuzzCore core, int bpm)
        {
            int old = core.BPM;
            if (bpm == old) return;
            core.BPM = bpm;
            if (core.BPM == old) return;                  // out of range: nothing changed
            var stack = core.SongCore?.ActionStack;
            if (stack == null || stack.Applying) return;
            stack.RecordCoalesced(new TempoStepAction(core, old, core.BPM), "tempo", TempoGap);
        }

        /// <summary>Turns looping on or off, as a history step.</summary>
        public static void SetLooping(ReBuzzCore core, bool on)
        {
            bool old = core.Looping;
            if (on == old) return;
            core.Looping = on;
            var stack = core.SongCore?.ActionStack;
            if (stack == null || stack.Applying) return;
            stack.Record(new LoopingStepAction(core, on));
        }
    }

    [HistoryName("Set tempo")]
    internal sealed class TempoStepAction : IAction, ICoalescingAction
    {
        readonly ReBuzzCore core;
        readonly int before;
        int after;

        public TempoStepAction(ReBuzzCore core, int before, int after)
        {
            this.core = core;
            this.before = before;
            this.after = after;
        }

        public void Do() => core.BPM = after;

        public void Undo() => core.BPM = before;

        public bool TryMerge(IAction later)
        {
            if (later is not TempoStepAction t || !ReferenceEquals(t.core, core)) return false;
            after = t.after;
            return true;
        }
    }

    [HistoryName("Switch loop")]
    internal sealed class LoopingStepAction : IAction
    {
        readonly ReBuzzCore core;
        readonly bool after;

        public LoopingStepAction(ReBuzzCore core, bool after)
        {
            this.core = core;
            this.after = after;
        }

        public void Do() => core.Looping = after;

        public void Undo() => core.Looping = !after;
    }
}
