using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common
{
    /// <summary>
    /// The song's undo history. Fork (docs/specs/history-snapshots.md HS1): every view shares this one; a step (a group of
    /// actions) has a name, a time and a source, a size counted against <see cref="MaxBytes"/> (the oldest steps drop
    /// first), and the stack can jump to any step. Not thread-safe: the UI/engine thread only, never the audio thread.
    /// </summary>
    public class ManagedActionStack : IActionStack
    {
        /// <summary>What an action counts for when it doesn't say (<see cref="IHistoryAction.HistoryBytes"/>).</summary>
        public const long DefaultActionBytes = 256;

        sealed class GroupInfo
        {
            public int Id;
            public string? Name;
            public DateTime Time;
            public HistorySource Source;
            public object? Key;
            public long Touched;
        }

        sealed class ActionRef
        {
            public IAction Action { get; set; } = null!;
            public int Group { get; set; }
            public GroupInfo Info { get; set; } = null!;
            public long Bytes { get; set; }
            public KeyValuePair<object, long>[]? Blobs { get; set; }
        }

        sealed class BlobRef
        {
            public long Bytes;
            public int Refs;
        }

        readonly List<ActionRef> actions = new List<ActionRef>();
        readonly Dictionary<object, BlobRef> blobs = new Dictionary<object, BlobRef>(ReferenceEqualityComparer.Instance);
        int position;

        int groupCounter;
        int groupDepth;
        int applying;
        GroupInfo? current;
        string? pendingName;
        long ownBytes;
        long blobBytes;
        long dropped;
        List<HistoryStep>? steps;

        public IEnumerable<IAction> Actions { get { return actions.Select(a => a.Action); } }

        public ManagedActionStack()
        {
            MaxNumberOfActions = int.MaxValue;
        }

        public ManagedActionStack(int maxNumberOfActions)
        {
            MaxNumberOfActions = maxNumberOfActions;
        }

        /// <summary>Who the steps made now are from (the agent host sets Agent for a call).</summary>
        public HistorySource Source { get; set; }

        /// <summary>Milliseconds, for the idle gap of <see cref="RecordCoalesced"/> (tests replace it).</summary>
        public Func<long> Clock { get; set; } = () => Environment.TickCount64;

        /// <summary>The most memory the steps may hold, in bytes (0: no cap). The oldest steps drop first.</summary>
        public long MaxBytes { get; set; }

        /// <summary>Memory the steps hold: each action's own bytes plus each distinct shared blob once.</summary>
        public long UsedBytes => ownBytes + blobBytes;

        /// <summary>How many steps the cap or the action limit dropped.</summary>
        public long DroppedSteps => dropped;

        /// <summary>True while an action's Do or Undo runs: an engine setter that records a step of its own skips it then.</summary>
        public bool Applying => applying > 0;

        /// <summary>Raised after the steps or the position changed (a step, undo, redo, a drop, a clear).</summary>
        public event Action? Changed;

        /// <summary>Raised after a group was done, undone or redone, with its actions (views refresh from it).</summary>
        public event Action<HistoryChange>? Applied;

        public void Do(IAction a)
        {
            Add(a, true, null, 0);
        }

        /// <summary>
        /// Puts an edit that was already made on the stack, without running its Do (a state load, a controller move):
        /// undo takes it back, redo does it again.
        /// </summary>
        public void Record(IAction a)
        {
            Add(a, false, null, 0);
        }

        /// <summary>
        /// <see cref="Record"/>, merged into the newest step when that has the same <paramref name="key"/>, nothing was done
        /// or undone since, and no more than <paramref name="idleGap"/> passed since it was last touched; the action
        /// must be an <see cref="ICoalescingAction"/> that takes it in. A sweep is one step.
        /// </summary>
        public void RecordCoalesced(IAction a, object key, TimeSpan idleGap)
        {
            long now = Clock();
            if (groupDepth == 0 && position == actions.Count && actions.Count > 0)
            {
                var last = actions[actions.Count - 1];
                bool single = actions.Count == 1 || actions[actions.Count - 2].Group != last.Group;
                if (single && last.Info.Key != null && last.Info.Key.Equals(key) && now - last.Info.Touched <= idleGap.TotalMilliseconds &&
                    last.Action is ICoalescingAction merge && merge.TryMerge(a))
                {
                    last.Info.Touched = now;
                    last.Info.Time = DateTime.Now;
                    steps = null;
                    Changed?.Invoke();
                    return;
                }
            }
            Add(a, false, key, now);
        }

        void NewGroup()
        {
            groupCounter++;
            current = new GroupInfo { Id = groupCounter, Name = pendingName, Time = DateTime.Now, Source = Source };
            pendingName = null;
        }

        void Add(IAction a, bool run, object? key, long now)
        {
            if (groupDepth == 0) NewGroup();
            if (key != null) { current!.Key = key; current.Touched = now; }

            if (run)
            {
                applying++;
                try { a.Do(); }
                finally { applying--; }
            }

            while (actions.Count > position)
                Remove(actions.Count - 1);

            var r = new ActionRef { Action = a, Group = groupCounter, Info = current!, Bytes = DefaultActionBytes };
            if (a is IHistoryAction h)
            {
                if (h.HistoryBytes > 0) r.Bytes = h.HistoryBytes;
                r.Blobs = h.HistoryBlobs?.ToArray();
            }
            actions.Add(r);
            position++;
            Count(r, +1);
            steps = null;

            if (PostDoOrUndoGroup != null)
                PostDoOrUndoGroup(new[] { a }, true);
            Applied?.Invoke(new HistoryChange(HistoryChangeKind.Do, new[] { a }));

            Trim();
            Changed?.Invoke();
        }

        void Remove(int index)
        {
            Count(actions[index], -1);
            actions.RemoveAt(index);
        }

        void Count(ActionRef r, int sign)
        {
            ownBytes += sign * r.Bytes;
            if (r.Blobs == null) return;
            foreach (var b in r.Blobs)
            {
                if (b.Key == null) continue;
                if (sign > 0)
                {
                    if (blobs.TryGetValue(b.Key, out var held)) held.Refs++;
                    else
                    {
                        blobs[b.Key] = new BlobRef { Bytes = b.Value, Refs = 1 };
                        blobBytes += b.Value;
                    }
                }
                else if (blobs.TryGetValue(b.Key, out var held) && --held.Refs <= 0)
                {
                    blobs.Remove(b.Key);
                    blobBytes -= held.Bytes;
                }
            }
        }

        /// <summary>Drops the oldest whole groups while over the action limit or the memory cap; the newest step stays.</summary>
        void Trim()
        {
            while (actions.Count > 1 && (actions.Count > MaxNumberOfActions || (MaxBytes > 0 && UsedBytes > MaxBytes)))
            {
                int g = actions[0].Group;
                if (actions[actions.Count - 1].Group == g) break;
                while (actions.Count > 0 && actions[0].Group == g)
                {
                    Remove(0);
                    if (position > 0) position--;
                }
                dropped++;
                steps = null;
            }
        }

        public bool CanUndo { get { return position > 0; } }
        public bool CanRedo { get { return actions.Count > 0 && position < actions.Count; } }

        public int MaxNumberOfActions { get; private set; }

        public void Undo()
        {
            if (position < 1) return;

            int g = actions[position - 1].Group;
            int startpos = position - 1;

            applying++;
            try
            {
                do
                {
                    actions[position - 1].Action.Undo();
                    position--;
                } while (position >= 1 && actions[position - 1].Group == g);
            }
            finally { applying--; }
            steps = null;

            var undone = actions.Skip(position).Take(startpos - position + 1).Reverse().Select(a => a.Action).ToArray();
            if (PostDoOrUndoGroup != null)
                PostDoOrUndoGroup(undone, false);
            Applied?.Invoke(new HistoryChange(HistoryChangeKind.Undo, undone));
            Changed?.Invoke();
        }

        public void Redo()
        {
            if (position >= actions.Count) return;

            int g = actions[position].Group;
            int startpos = position;

            applying++;
            try
            {
                do
                {
                    actions[position].Action.Do();
                    position++;
                } while (position < actions.Count && actions[position].Group == g);
            }
            finally { applying--; }
            steps = null;

            var redone = actions.Skip(startpos).Take(position - startpos).Select(a => a.Action).ToArray();
            if (PostDoOrUndoGroup != null)
                PostDoOrUndoGroup(redone, true);
            Applied?.Invoke(new HistoryChange(HistoryChangeKind.Redo, redone));
            Changed?.Invoke();
        }

        public void BeginActionGroup()
        {
            if (groupDepth == 0)
                NewGroup();

            groupDepth++;
        }

        public void EndActionGroup()
        {
            groupDepth--;

            if (groupDepth <= 0)
                groupDepth = 0;
        }

        /// <summary>
        /// Names the step being made (inside a group) or the next one. Without it the name is the first action's
        /// <see cref="IHistoryAction.HistoryName"/>, or made from its type.
        /// </summary>
        public void Label(string name)
        {
            if (groupDepth > 0 && current != null) current.Name = name;
            else pendingName = name;
        }

        /// <summary>Steps made until the result is disposed are from <paramref name="source"/> (and the next is named <paramref name="label"/>).</summary>
        public IDisposable BeginSource(HistorySource source, string? label = null)
        {
            var scope = new SourceScope(this, Source);
            Source = source;
            if (label != null) Label(label);
            return scope;
        }

        sealed class SourceScope : IDisposable
        {
            readonly ManagedActionStack stack;
            readonly HistorySource before;
            bool disposed;

            public SourceScope(ManagedActionStack stack, HistorySource before)
            {
                this.stack = stack;
                this.before = before;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                stack.Source = before;
                if (stack.groupDepth == 0) stack.pendingName = null;
            }
        }

        /// <summary>The steps, oldest first, with the applied ones marked.</summary>
        public IReadOnlyList<HistoryStep> Steps
        {
            get
            {
                if (steps != null) return steps;
                var list = new List<HistoryStep>();
                for (int i = 0; i < actions.Count;)
                {
                    int g = actions[i].Group;
                    int start = i;
                    long bytes = 0;
                    while (i < actions.Count && actions[i].Group == g) { bytes += actions[i].Bytes; i++; }
                    var info = actions[start].Info;
                    list.Add(new HistoryStep(g, info.Name ?? DefaultName(actions[start].Action), info.Time, info.Source, bytes, i - start, start < position));
                }
                steps = list;
                return list;
            }
        }

        /// <summary>The id of the newest applied step, or 0 when none is applied.</summary>
        public int CurrentStepId => position > 0 ? actions[position - 1].Group : 0;

        /// <summary>
        /// Undoes or redoes to the state right after step <paramref name="stepId"/> (0: before the oldest step kept).
        /// False if there is no such step (it was dropped, or never existed).
        /// </summary>
        public bool JumpTo(int stepId)
        {
            int target;
            if (stepId == 0) target = 0;
            else
            {
                int last = -1;
                for (int i = 0; i < actions.Count; i++) if (actions[i].Group == stepId) last = i;
                if (last < 0) return false;
                target = last + 1;
            }
            while (position > target) { if (!UndoOne()) break; }
            while (position < target) { if (!RedoOne()) break; }
            return true;
        }

        bool UndoOne()
        {
            int before = position;
            Undo();
            return position < before;
        }

        bool RedoOne()
        {
            int before = position;
            Redo();
            return position > before;
        }

        /// <summary>Forgets every step.</summary>
        public void Clear()
        {
            actions.Clear();
            blobs.Clear();
            ownBytes = blobBytes = 0;
            position = 0;
            steps = null;
            Changed?.Invoke();
        }

        static string DefaultName(IAction a)
        {
            if (a is IHistoryAction h && !string.IsNullOrEmpty(h.HistoryName)) return h.HistoryName!;
            var named = (HistoryNameAttribute?)Attribute.GetCustomAttribute(a.GetType(), typeof(HistoryNameAttribute), false);
            if (named != null) return named.Name;
            string t = a.GetType().Name;
            int tick = t.IndexOf('`');
            if (tick > 0) t = t.Substring(0, tick);
            if (t.EndsWith("Action", StringComparison.Ordinal) && t.Length > 6) t = t.Substring(0, t.Length - 6);
            if (t.Length == 0) return "Edit";
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < t.Length; i++)
            {
                if (i > 0 && char.IsUpper(t[i]) && !char.IsUpper(t[i - 1])) sb.Append(' ');
                sb.Append(i == 0 ? t[i] : char.ToLowerInvariant(t[i]));
            }
            return sb.ToString();
        }

        public event Action<IEnumerable<IAction>, bool>? PostDoOrUndoGroup;

    }
}
