using System.Collections.Generic;
using System.Linq;

namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Fork (docs/specs/mcp-server.md §12.5, U5): machine_replace puts a successor in a machine's place.
    /// Its sequences are made beside the old machine's (the layout gives each a track, as for any new
    /// sequence); here those tracks take the old ones' slots and colours, track for track, and the
    /// successor's patterns (the same names) take the old clips' settings, return, cue and crossfader
    /// side. The old machine is deleted next (or kept, muted), as any machine is. Undo puts the layout's
    /// entries for the successor back as they were.
    /// </summary>
    internal sealed partial class SessionLayout
    {
        internal sealed class Adoption
        {
            public string From = "", To = "";
            public List<(int Index, int Color, SortedDictionary<int, string> Slots)> TracksBefore = new();
            public List<string> ClipsAdded = new();
            public bool ReturnAdded;
            public string CueBefore = "";
            public bool CrossfadeAdded;
        }

        /// <summary>The successor <paramref name="to"/> takes <paramref name="from"/>'s place in the layout; null if the song has no layout.</summary>
        internal Adoption Adopt(string from, string to)
        {
            var doc = Document;
            if (doc == null || from == to) return null;
            var a = new Adoption { From = from, To = to };
            var olds = doc.Tracks.Where(t => t.Machine == from).ToList();
            var news = doc.Tracks.Select((t, i) => (t, i)).Where(x => x.t.Machine == to).ToList();
            for (int k = 0; k < news.Count && k < olds.Count; k++)
            {
                var (t, i) = news[k];
                var slots = new SortedDictionary<int, string>(t.Slots);
                a.TracksBefore.Add((i, t.ColorIndex, slots));
                t.Slots.Clear();
                foreach (var s in olds[k].Slots) t.Slots[s.Key] = s.Value;
                t.ColorIndex = olds[k].ColorIndex;
            }
            foreach (var key in doc.Clips.Keys.Where(k => k.Machine == from).ToList())
            {
                if (doc.Clips.ContainsKey((to, key.Pattern))) continue;
                doc.Clips[(to, key.Pattern)] = doc.Clips[key].Clone();
                a.ClipsAdded.Add(key.Pattern);
            }
            if (doc.Returns.Contains(from) && !doc.Returns.Contains(to))
            {
                doc.Returns.Insert(doc.Returns.IndexOf(from) + 1, to);
                a.ReturnAdded = true;
            }
            a.CueBefore = doc.CueMachine;
            if (doc.CueMachine == from) doc.CueMachine = to;
            if (doc.Crossfade.TryGetValue(from, out var side) && !doc.Crossfade.ContainsKey(to))
            {
                doc.Crossfade[to] = side;
                a.CrossfadeAdded = true;
            }
            dirty = true;
            ContentChanged();
            return a;
        }

        /// <summary>Takes back an <see cref="Adopt"/> (the successor's entries as they were before it).</summary>
        internal void Unadopt(Adoption a)
        {
            var doc = Document;
            if (doc == null || a == null) return;
            foreach (var (index, color, slots) in a.TracksBefore)
            {
                if (index >= doc.Tracks.Count || doc.Tracks[index].Machine != a.To) continue;
                var t = doc.Tracks[index];
                t.Slots.Clear();
                foreach (var s in slots) t.Slots[s.Key] = s.Value;
                t.ColorIndex = color;
            }
            foreach (var p in a.ClipsAdded) doc.Clips.Remove((a.To, p));
            if (a.ReturnAdded) doc.Returns.Remove(a.To);
            if (doc.CueMachine == a.To) doc.CueMachine = a.CueBefore;
            if (a.CrossfadeAdded) doc.Crossfade.Remove(a.To);
            dirty = true;
            ContentChanged();
        }
    }
}
