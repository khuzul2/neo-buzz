// Fork (audio hygiene, docs/specs/daw-modes.md §8.1): events from the audio thread to the UI thread
// without the audio thread allocating. Upstream posted a closure (and the dispatcher made its
// operation) for each: every value a tick sent to a parameter with a listener (a parameter
// window's rows, the studio's parameter rows, the pattern editor's columns, ConnectionMixer, the
// master volume), every tick's play position, every change of a machine's activity (twice a
// buffer for some machines with the default work algorithm). And a description listener added a
// closure to its parameter's timer on every change, where they piled up.
//
// Here whoever raises an event keeps a pending flag for it; raising sets the flag and, if it was
// clear, puts the target on this queue, made once with room for every event that can be pending
// (reserved when a listener subscribes or a machine is made). The UI thread drains the queue when
// signalled (IUiDispatcher.CreateSignal: the drain posted as a cached delegate, or polled by a
// timer where posts allocate). Each target clears its flag before it tells its listeners, who read
// the state as it is then: listeners see every final value, and changes before a drain coalesce,
// as a tick's changes to one parameter always did (it sends the last).

using System;
using System.Threading;

namespace ReBuzz.Core
{
    internal sealed class UiEventQueue
    {
        /// <summary>Something with events on this queue (UI thread: clear its flag, then tell its listeners).</summary>
        internal interface ITarget
        {
            void Deliver(int arg, int kind);
        }

        struct Entry
        {
            public ITarget Target;
            public int Arg;
            public int Kind;
        }

        readonly Lock gate = new();
        Entry[] queue = new Entry[64];
        Entry[] draining = new Entry[64];
        int count;
        int reserved;
        readonly IUiSignal signal;

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IUiDispatcher, UiEventQueue> all = new();

        /// <summary>The queue of the engine with <paramref name="dispatcher"/> (ReBuzzCore makes it, on the UI thread).</summary>
        internal static UiEventQueue For(IUiDispatcher dispatcher) => all.GetValue(dispatcher, d => new UiEventQueue(d));

        UiEventQueue(IUiDispatcher dispatcher)
        {
            signal = dispatcher.CreateSignal(Drain);
        }

        /// <summary>Room for <paramref name="events"/> more pending events (any thread but the audio thread).</summary>
        internal void Reserve(int events)
        {
            lock (gate)
            {
                reserved += events;
                Ensure(reserved + 16);
            }
        }

        void Ensure(int capacity)
        {
            if (queue.Length >= capacity) return;
            int size = Math.Max(capacity, queue.Length * 2);
            var grown = new Entry[size];
            Array.Copy(queue, grown, count);
            queue = grown;
            draining = new Entry[size];
        }

        /// <summary>
        /// Queues an event of <paramref name="target"/>, whose pending flag for it the caller has
        /// just set (it was clear). Any thread; allocates nothing within the room reserved.
        /// </summary>
        internal void Enqueue(ITarget target, int arg, int kind)
        {
            bool first;
            lock (gate)
            {
                if (count == queue.Length)
                    Ensure(count + 1);                  // more pending than reserved: not expected
                queue[count++] = new Entry { Target = target, Arg = arg, Kind = kind };
                first = count == 1;
            }
            if (first)
                signal.Request();
        }

        bool inDrain;

        /// <summary>Delivers the events queued (UI thread). Tests call it to deliver at once.</summary>
        internal void Drain()
        {
            if (inDrain) return;                        // a listener's events: the loop below takes them
            inDrain = true;
            try
            {
                while (true)
                {
                    Entry[] taken;
                    int n;
                    lock (gate)
                    {
                        n = count;
                        if (n == 0) return;
                        taken = queue;
                        queue = draining;
                        draining = taken;
                        count = 0;
                    }
                    // Events raised from here on go on the other array (and request the signal again).
                    for (int i = 0; i < n; i++)
                    {
                        var e = taken[i];
                        taken[i] = default;
                        e.Target.Deliver(e.Arg, e.Kind);
                    }
                }
            }
            finally
            {
                inDrain = false;
            }
        }
    }
}
