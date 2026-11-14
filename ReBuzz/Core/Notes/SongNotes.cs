// Fork (docs/specs/daw-modes.md AR2): the song's notes of a pattern, in the store the machine plays. The arrangement's piano roll
// and the Pattern tab's both open this source, so they edit the same notes the Session View's clip plays:
//  - a machine that takes its notes in its tracks (a tracker, a native synth: its Note track parameter only, no MidiNote; the
//    Pedal synths took notes that way until U6 gave them MIDI) has them in the pattern's engine-played Note columns
//    (TrackerColumnNotes, D3 A);
//  - any other generator, and a machine with a manifest note input, plays the engine MIDI column (MidiColumnNotes, D2 B).
// Both put each edit on the song's history. Also here: the context ghosts (other clips' notes, placed in this pattern's time).

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Core.Keyboards;
using ReBuzz.Core.Session;

namespace ReBuzz.Core.Notes
{
    internal static class SongNotes
    {
        /// <summary>The pattern's note source as the machine plays it, or null when the machine takes no notes (an effect, Master).</summary>
        public static INoteSource For(ReBuzzCore buzz, IPattern pattern)
        {
            if (buzz == null || pattern?.Machine is not MachineCore machine || machine.DLL?.Info == null) return null;
            if (machine.DLL.Info.Type == MachineType.Master) return null;
            // How the machine takes notes is what its keyboard decides (KeyboardRoute, the agent's rule): a managed machine with
            // MidiNote takes MIDI whatever an older manifest says; one that has a Note track parameter and no MIDI plays its tracks.
            var route = KeyboardRoute.For(machine);
            if (route.Input == KeyboardInput.Tracks && TrackerColumnNotes.For(buzz, pattern) is { } tracker) return tracker;
            if (route.Input == KeyboardInput.Midi) return new MidiColumnNotes(buzz, pattern);
            var manifest = SongModel.Manifest(machine.DLL);
            if (machine.DLL.Info.Type == MachineType.Generator || manifest?.Notes != null) return new MidiColumnNotes(buzz, pattern);
            return null;
        }

        /// <summary>Whether the pattern's notes are in the engine MIDI column (the controller lanes read that column).</summary>
        public static bool IsMidi(INoteSource source) => source is MidiColumnNotes;

        /// <summary>
        /// The notes of the other clips around a pattern's placement, in the pattern's own time (1/240 tick; the roll draws them
        /// faintly). <paramref name="anchor"/> is the song tick of the pattern's first tick. Only what overlaps the pattern's
        /// length is kept, cut to it. With <paramref name="otherTracks"/> the clips on the other machines' lanes count too.
        /// </summary>
        public static List<ClipNote> Ghosts(ReBuzzCore buzz, IPattern pattern, int anchor, bool otherTracks, int limit = 4000)
        {
            var ghosts = new List<ClipNote>();
            if (buzz?.Song == null || pattern == null) return ghosts;
            int length = SessionLayout.ClipLengthOf(pattern);
            var cache = new Dictionary<IPattern, List<ClipNote>>();
            foreach (var seq in buzz.Song.Sequences)
            {
                bool same = seq.Machine == pattern.Machine;
                if (!same && !otherTracks) continue;
                foreach (var kv in seq.Events)
                {
                    var e = kv.Value;
                    if (e.Type != SequenceEventType.PlayPattern || e.Pattern == null || ReferenceEquals(e.Pattern, pattern)) continue;
                    // The clip's span in this pattern's time.
                    long from = (long)(kv.Key - anchor) * ClipNotes.TimeBase, to = from + (long)Math.Max(1, e.Span) * ClipNotes.TimeBase;
                    if (to <= 0 || from >= length) continue;
                    if (!cache.TryGetValue(e.Pattern, out var notes))
                        cache[e.Pattern] = notes = For(buzz, e.Pattern)?.Notes() ?? new List<ClipNote>();
                    // Pattern tick t of the other pattern is at song tick key - PlayedOffset + t.
                    long shift = (long)(kv.Key - e.PlayedOffset - anchor) * ClipNotes.TimeBase;
                    foreach (var n in notes)
                    {
                        long start = n.Time + shift, end = start + Math.Max(1, n.Duration);
                        if (end <= from || start >= to) continue;
                        start = Math.Max(start, Math.Max(0, from));
                        end = Math.Min(end, Math.Min(to, length));
                        if (end <= start) continue;
                        ghosts.Add(n with { Time = (int)start, Duration = (int)(end - start) });
                        if (ghosts.Count >= limit) return ghosts;
                    }
                }
            }
            ghosts.Sort((a, b) => a.Time != b.Time ? a.Time.CompareTo(b.Time) : a.Note.CompareTo(b.Note));
            return ghosts;
        }
    }
}
