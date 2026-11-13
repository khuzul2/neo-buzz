// Fork (docs/specs/neo-learn.md §5.3): what the Explain strip of a Neo editor and the studio's inspector
// share across assemblies (every machine DLL carries its own copy of the editor kit, but they all load
// this one). Two small boards, both plain data:
//  - ParamSource: who is writing a parameter. The agent's param_set marks a write before it makes it; the
//    editor reads the mark when the value arrives, and so can tell Agent from Automation (a hand edit is
//    the editor's own).
//  - ExplainBoard: the line each machine's editor shows now (control name, value, source, How, Sound), for
//    the inspector's Selection tab to show the same card.
using System.Runtime.CompilerServices;

namespace BuzzGUI.Interfaces
{
    /// <summary>Marks parameter writes made by the agent, so editors can show them with the Agent badge.</summary>
    public static class ParamSource
    {
        /// <summary>How long a mark waits for its value to arrive (the machine takes it at its next tick).</summary>
        public const long MarkMs = 3000;

        static readonly object gate = new object();
        static readonly List<(object Parameter, int Track, long At)> marks = new List<(object, int, long)>();

        /// <summary>Test hook: the clock, in milliseconds.</summary>
        public static Func<long> Clock { get; set; } = () => Environment.TickCount64;

        /// <summary>Notes that the agent is about to write <paramref name="parameter"/> on <paramref name="track"/>.</summary>
        public static void MarkAgent(object parameter, int track = 0)
        {
            if (parameter == null) return;
            long now = Clock();
            lock (gate)
            {
                marks.RemoveAll(m => now - m.At > MarkMs || (ReferenceEquals(m.Parameter, parameter) && m.Track == track));
                marks.Add((parameter, track, now));
            }
        }

        /// <summary>Whether the agent wrote <paramref name="parameter"/> lately; the mark is used up.</summary>
        public static bool TakeAgent(object parameter, int track = 0)
        {
            if (parameter == null) return false;
            long now = Clock();
            lock (gate)
            {
                marks.RemoveAll(m => now - m.At > MarkMs);
                int i = marks.FindIndex(m => ReferenceEquals(m.Parameter, parameter) && m.Track == track);
                if (i < 0) return false;
                marks.RemoveAt(i);
                return true;
            }
        }

        /// <summary>Whether a mark waits (without using it up).</summary>
        public static bool HasAgentMark(object parameter, int track = 0)
        {
            if (parameter == null) return false;
            long now = Clock();
            lock (gate) return marks.Exists(m => ReferenceEquals(m.Parameter, parameter) && m.Track == track && now - m.At <= MarkMs);
        }
    }

    /// <summary>What an editor's Explain strip shows: the control, its value, who wrote it, and the two lines.</summary>
    public sealed record ExplainCard(string Name, string Value, string Source, string How, string Sound);

    /// <summary>The card each machine's editor shows now.</summary>
    public static class ExplainBoard
    {
        static readonly ConditionalWeakTable<object, ExplainCard> cards = new ConditionalWeakTable<object, ExplainCard>();
        static readonly ConditionalWeakTable<object, object> registered = new ConditionalWeakTable<object, object>();

        /// <summary>An editor with Explain text opened for <paramref name="machine"/> (the inspector shows a card for it).</summary>
        public static void Register(object machine)
        {
            if (machine == null) return;
            registered.Remove(machine);
            registered.Add(machine, machine);
        }

        public static bool IsRegistered(object machine) => machine != null && registered.TryGetValue(machine, out _);

        /// <summary>Sets what <paramref name="machine"/>'s strip shows (null: nothing focused).</summary>
        public static void Publish(object machine, ExplainCard card)
        {
            if (machine == null) return;
            cards.Remove(machine);
            if (card != null) cards.Add(machine, card);
        }

        public static ExplainCard Get(object machine) => machine != null && cards.TryGetValue(machine, out var c) ? c : null;
    }
}
