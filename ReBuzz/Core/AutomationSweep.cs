// Fork (docs/specs/daw-modes.md section 4.3, docs/specs/session-view.md 0.7, SA1): the one rule both recorders of automation
// share. A knob move replaces what its pass sweeps over: from the pass's last point written (or the move itself, for the first
// move of a pass) to the move; points less than a quarter tick apart are one point. The Arrangement's recorder
// (ArrangementAutomationRecorder) and the Session's (SessionLayout, takes) both clear the swept range, then write. Plain
// functions on a list of points; nothing here knows a clip or a thread.

using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    internal static class AutomationSweep
    {
        /// <summary>Points closer than this (1/240 tick) are one point.</summary>
        public const int Quarter = ClipEvent.TimeBase / 4;

        /// <summary>"No point written yet in this pass".</summary>
        public const int None = int.MinValue;

        /// <summary>The first time a move at <paramref name="t"/> replaces, given the time of the pass's last point (<see cref="None"/> for the first move).</summary>
        public static int From(int last, int t) => last == None ? t : (t - last < Quarter ? last : last + 1);

        /// <summary>
        /// Clears the points timed <paramref name="from"/> to <paramref name="to"/> (both included) from <paramref name="points"/>,
        /// putting what it took out into <paramref name="removed"/> when that is given. Returns how many it took out.
        /// </summary>
        public static int Clear<T>(List<T> points, Func<T, int> timeOf, int from, int to, List<T> removed = null)
        {
            int count = 0;
            for (int i = points.Count - 1; i >= 0; i--)
            {
                int time = timeOf(points[i]);
                if (time < from || time > to) continue;
                removed?.Insert(0, points[i]);
                points.RemoveAt(i);
                count++;
            }
            return count;
        }

        /// <summary>
        /// A move at <paramref name="t"/>: clears what the pass sweeps over (from <see cref="From"/> to <paramref name="t"/>), then writes
        /// <paramref name="point"/>. <paramref name="last"/> is the time of the pass's last point (<see cref="None"/> for its first move).
        /// </summary>
        public static void Write<T>(List<T> points, Func<T, int> timeOf, int last, int t, T point, List<T> removed = null)
        {
            Clear(points, timeOf, From(last, t), t, removed);
            points.Add(point);
        }
    }
}
