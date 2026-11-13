// Fork (docs/specs/history-snapshots.md HS1): what the song's one history (ManagedActionStack) knows about its steps:
// a name, a time and a source for the panel and the agents, a size for the memory cap, and a way for a step to
// merge into the one before it (a controller sweep is one step). Nothing here runs on the audio thread.

using BuzzGUI.Interfaces;
using System;
using System.Collections.Generic;

namespace BuzzGUI.Common
{
    /// <summary>Who made a step.</summary>
    public enum HistorySource { Manual, Agent, Midi }

    /// <summary>What an applied group was.</summary>
    public enum HistoryChangeKind { Do, Undo, Redo }

    /// <summary>A group of actions that was done, undone or redone (the stack's <c>Applied</c> event).</summary>
    public sealed class HistoryChange
    {
        public HistoryChange(HistoryChangeKind kind, IReadOnlyList<IAction> actions)
        {
            Kind = kind;
            Actions = actions;
        }

        public HistoryChangeKind Kind { get; }
        public IReadOnlyList<IAction> Actions { get; }
    }

    /// <summary>One step of the history, as the History panel and the agents see it.</summary>
    public sealed class HistoryStep
    {
        public HistoryStep(int id, string name, DateTime time, HistorySource source, long bytes, int actions, bool done)
        {
            Id = id;
            Name = name;
            Time = time;
            Source = source;
            Bytes = bytes;
            Actions = actions;
            Done = done;
        }

        /// <summary>Stable for the life of the stack (the stack counts up); what <c>JumpTo</c> takes.</summary>
        public int Id { get; }
        public string Name { get; }
        public DateTime Time { get; }
        public HistorySource Source { get; }

        /// <summary>The memory the step's own data takes (shared blobs are counted once, in the stack's total).</summary>
        public long Bytes { get; }

        /// <summary>How many actions the step holds.</summary>
        public int Actions { get; }

        /// <summary>True while the step is applied (undo takes it back); false on the redo side.</summary>
        public bool Done { get; }
    }

    /// <summary>An action that says more about itself to the history: a name, a size, the shared blobs it holds.</summary>
    public interface IHistoryAction
    {
        /// <summary>What the step is called in the panel ("Set Cutoff"); null for a name made from the action's type.</summary>
        string? HistoryName { get; }

        /// <summary>Bytes the action itself holds, not counting <see cref="HistoryBlobs"/>; 0 for the default.</summary>
        long HistoryBytes { get; }

        /// <summary>
        /// Immutable data the action holds by reference (a sample, a state object), with its size. A blob that several
        /// steps hold (the same object) counts once against the cap. Null for none.
        /// </summary>
        IEnumerable<KeyValuePair<object, long>>? HistoryBlobs { get; }
    }

    /// <summary>
    /// An action that can take a later one in (<see cref="ManagedActionStack.RecordCoalesced"/>): a controller moved again
    /// within the idle gap keeps the first old value and takes the last new one.
    /// </summary>
    public interface ICoalescingAction
    {
        /// <summary>Takes <paramref name="later"/> (already done) into this one; false if it can't (then it is a step of its own).</summary>
        bool TryMerge(IAction later);
    }

    /// <summary>What a step of this action is called in the history ("Rename clip"), where its type's name would read badly.</summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class HistoryNameAttribute : Attribute
    {
        public HistoryNameAttribute(string name) { Name = name; }
        public string Name { get; }
    }

    /// <summary>The memory cap's setting.</summary>
    public static class HistorySettings
    {
        /// <summary>The default per-song cap in MB.</summary>
        public const int DefaultMegabytes = 256;

        /// <summary>What a new song's history may hold, in bytes (the engine setting <c>HistoryMemoryMB</c>).</summary>
        public static long MaxBytes
        {
            get
            {
                try
                {
                    int mb = Global.EngineSettings?.HistoryMemoryMB ?? DefaultMegabytes;
                    return (mb <= 0 ? DefaultMegabytes : (long)mb) * 1024 * 1024;
                }
                catch (Exception)
                {
                    return DefaultMegabytes * 1024L * 1024L;
                }
            }
        }

        /// <summary>A song's history, with the setting's cap.</summary>
        public static ManagedActionStack NewStack() => new ManagedActionStack { MaxBytes = MaxBytes };
    }
}
