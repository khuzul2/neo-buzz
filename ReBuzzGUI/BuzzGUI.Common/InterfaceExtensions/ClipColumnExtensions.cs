using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;

namespace BuzzGUI.Common.InterfaceExtensions
{
    public static class ClipColumnExtensions
    {
        public static IEnumerable<ClipEvent> GetGridMIDINoteOnEvents(this IClipColumn pc, int columncount, int step, IEnumerable<int> notelist)
        {
            return pc.GetEvents(0, columncount * step).Where(e => e.Time % step == 0 && MIDI.IsNoteOn(e.Value, notelist));
        }

        public static void RemapValues(this IClipColumn pc, IDictionary<int, int> map)
        {
            var oldvalues = pc.GetEvents(int.MinValue, int.MaxValue).ToArray();
            pc.SetEvents(oldvalues, false);
            pc.SetEvents(oldvalues.Select(e => new ClipEvent(e.Time, map.ContainsKey(e.Value) ? map[e.Value] : e.Value, e.Duration)), true);
        }

        public static IEnumerable<int> GetRowTimes(this IClipColumn pc, int beatIndex, int subdivision)
        {
            return Enumerable.Range(0, subdivision).Select(t => beatIndex * ClipEvent.TimeBase * 4 + ClipEvent.TimeBase * 4 * t / subdivision);
        }

        public static IEnumerable<int> GetRowTimes(this IClipColumn pc, int beatIndex)
        {
            int subdiv;
            if (!pc.BeatSubdivision.TryGetValue(beatIndex, out subdiv))
                subdiv = 4;

            return pc.GetRowTimes(beatIndex, subdiv);
        }

    }
}
