// Fork (docs/specs/ui-gallery.md): the demo song both UI galleries capture, built through the engine alone so the
// Mac studio's headless gallery (ReBuzz.Studio.Tests, GalleryTests) and ReBuzz.exe's (--ui-shots, UiShots) show the
// same song: Neo generators and effects wired up, patterns with notes (most moved onto the Pattern View's note lanes,
// one left in its clip for the piano roll), an arrangement, Session View clips, scenes, colours and a return, song
// notes. Native machines join it where Gear has them (ReBuzz.exe); elsewhere they are left out. Compiled into
// ReBuzz.exe and linked into ReBuzz.Studio.Tests; engine APIs only.

using System;
using System.Collections.Generic;
using System.Linq;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;

namespace ReBuzz.Gallery
{
    internal static class GallerySong
    {
        public const string Title = "Night Drive";
        public const int Bar = 16;                  // ticks a bar at 4 ticks a beat
        const int TB = PatternEvent.TimeBase;

        /// <summary>What was built, for the galleries to point at.</summary>
        internal sealed class Song
        {
            public MachineCore Drums, Bass, Lead, Pad, Drive, Comp, Delay, Verb, Eq, Limiter, Master;
            public MachineCore NativeGenerator, NativeEffect;      // null without the natives
            public IPattern Beat, Fill, Break, LineA, LineB, Hook, Answer, Arp, Chords;
            public readonly List<string> Problems = new List<string>();
            public IEnumerable<MachineCore> Generators => new[] { Drums, Bass, Lead, Pad, NativeGenerator }.Where(m => m != null);
        }

        /// <summary>The song's parts in the open song, by their names (the galleries save it first, so the title names it).</summary>
        public static Song Find(ReBuzzCore core)
        {
            var s = new Song();
            var song = core.SongCore;
            MachineCore M(string name) => song.MachinesList.FirstOrDefault(m => m.Name == name);
            IPattern P(MachineCore m, string name) => m?.Patterns.FirstOrDefault(p => p.Name == name);
            s.Master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            s.Drums = M("Drums"); s.Bass = M("Bass"); s.Lead = M("Lead"); s.Pad = M("Pad");
            s.Drive = M("Drive"); s.Comp = M("Drum Bus"); s.Delay = M("Delay"); s.Verb = M("Verb"); s.Eq = M("EQ"); s.Limiter = M("Limiter");
            s.NativeGenerator = M("Infector"); s.NativeEffect = M("Jeskola Delay");
            s.Beat = P(s.Drums, "Beat"); s.Fill = P(s.Drums, "Fill"); s.Break = P(s.Drums, "Break");
            s.LineA = P(s.Bass, "Line A"); s.LineB = P(s.Bass, "Line B");
            s.Hook = P(s.Lead, "Hook"); s.Answer = P(s.Lead, "Answer"); s.Arp = P(s.Lead, "Arp");
            s.Chords = P(s.Pad, "Chords");
            return s;
        }

        /// <summary>
        /// Builds the song into the open (new, empty) song of <paramref name="core"/>, on the UI thread. Song end and
        /// loop cover the 16-bar arrangement; the song is left with changes (the galleries save it).
        /// </summary>
        public static Song Build(ReBuzzCore core)
        {
            var s = new Song();
            var song = core.SongCore;
            core.BPM = 124;
            s.Master = song.MachinesList.First(m => m.DLL.Info.Type == MachineType.Master);
            s.Master.Position = Tuple.Create(0.92f, 0f);

            MachineCore Add(string dll, string name, float x, float y, int tracks = -1)
            {
                if (!core.MachineDLLs.ContainsKey(dll))
                {
                    s.Problems.Add($"{dll} isn't in Gear here");
                    return null;
                }
                song.CreateMachine(dll, null, name, null, null, null, tracks, x, y);
                return song.MachinesList.LastOrDefault(m => m.Name == name);
            }

            s.Drums = Add("Neo Drums", "Drums", -0.82f, -0.62f);
            s.Bass = Add("Neo Poly", "Bass", -0.82f, -0.22f, 2);
            s.Lead = Add("Neo FM6", "Lead", -0.82f, 0.18f, 2);
            s.Pad = Add("Neo Wave", "Pad", -0.82f, 0.58f, 4);
            s.Drive = Add("Neo Drive", "Drive", -0.4f, -0.22f);
            s.Comp = Add("Neo Comp", "Drum Bus", -0.02f, -0.5f);
            s.Delay = Add("Neo Delay", "Delay", -0.4f, 0.18f);
            s.Verb = Add("Neo Verb", "Verb", -0.02f, 0.5f);
            s.Eq = Add("Neo EQ", "EQ", 0.34f, 0f);
            s.Limiter = Add("Neo Limiter", "Limiter", 0.64f, 0f);
            // Natives, where Gear has them (ReBuzz.exe): an arpeggio through a delay, as Buzz songs have.
            if (core.MachineDLLs.ContainsKey("FSM Infector") && core.MachineDLLs.ContainsKey("Jeskola Delay"))
            {
                s.NativeGenerator = Add("FSM Infector", "Infector", -0.82f, 0.9f);
                s.NativeEffect = Add("Jeskola Delay", "Jeskola Delay", -0.4f, 0.9f);
            }

            void Wire(MachineCore from, MachineCore to, int amp = 0x4000, int pan = 0x4000)
            {
                if (from == null || to == null) return;
                song.ConnectMachines(from, to, new[] { 0 }, new[] { 0 }, amp, pan);
            }
            Wire(s.Drums, s.Comp);
            Wire(s.Bass, s.Drive);
            Wire(s.Drive, s.Comp, 0x3000);
            Wire(s.Comp, s.Eq);
            Wire(s.Lead, s.Delay);
            Wire(s.Delay, s.Eq, 0x3400, 0x5000);
            Wire(s.Delay, s.Verb, 0x1C00);                  // a send
            Wire(s.Pad, s.Eq, 0x2C00, 0x3000);
            Wire(s.Pad, s.Verb, 0x2400);
            Wire(s.Verb, s.Eq, 0x3000);
            Wire(s.Eq, s.Limiter);
            Wire(s.Limiter, s.Master);
            Wire(s.NativeGenerator, s.NativeEffect, 0x3000);
            Wire(s.NativeEffect, s.Eq, 0x2800);

            foreach (var g in s.Generators)
                if (!song.SequencesList.Any(q => q.Machine == g)) song.AddSequence(g, song.SequencesList.Count);

            // Patterns and notes (beats of 4 ticks; a tick is a row of the pattern editor).
            var layout = core.SessionLayout;
            IPattern Pattern(MachineCore m, string name, int ticks, params (double At, double Length, int Note, int Velocity)[] notes)
            {
                if (m == null) return null;
                m.CreatePattern(name, ticks);
                var p = m.PatternsList.Single(x => x.Name == name);
                if (notes.Length > 0)
                    layout.EditNotes(p, null, notes.Select(n => new ClipNote((int)Math.Round(n.At * TB), Math.Max(1, (int)Math.Round(n.Length * TB)), n.Note, n.Velocity)).ToArray());
                return p;
            }

            var beat = new List<(double, double, int, int)>();
            for (int i = 0; i < 16; i += 4) beat.Add((i, 1, 36, 120));
            foreach (int i in new[] { 4, 12 }) { beat.Add((i, 1, 38, 110)); beat.Add((i, 1, 39, 80)); }
            for (int i = 2; i < 16; i += 4) beat.Add((i, 1, i == 14 ? 46 : 42, i % 8 == 2 ? 90 : 70));
            s.Beat = Pattern(s.Drums, "Beat", Bar, beat.ToArray());
            s.Fill = Pattern(s.Drums, "Fill", Bar, (0, 1, 36, 120), (4, 1, 38, 100), (8, 1, 36, 120), (10, 1, 36, 100),
                (12, 1, 38, 90), (13, 1, 38, 100), (14, 1, 38, 110), (15, 1, 38, 127));
            var brk = new List<(double, double, int, int)>();
            for (int i = 0; i < 32; i += 2) brk.Add((i, 1, 42, i % 4 == 0 ? 85 : 55));
            brk.Add((0, 1, 36, 110));
            brk.Add((24, 1, 39, 90));
            s.Break = Pattern(s.Drums, "Break", 2 * Bar, brk.ToArray());

            // A minor: A1 C2 E2 G2.
            (double, double, int, int)[] Line(int[] notes) => notes.Select((n, i) => ((double)i * 2, 1.5, n, i % 4 == 0 ? 115 : 95)).Where(n => n.Item3 > 0).ToArray();
            s.LineA = Pattern(s.Bass, "Line A", 2 * Bar, Line(new[] { 33, 33, 45, 33, 36, 33, 43, 40, 33, 33, 45, 33, 31, 31, 43, 38 }));
            s.LineB = Pattern(s.Bass, "Line B", 2 * Bar, Line(new[] { 29, 29, 41, 29, 31, 31, 43, 31, 36, 36, 48, 36, 31, 31, 43, 35 }));

            s.Hook = Pattern(s.Lead, "Hook", 2 * Bar, (0, 3, 69, 110), (4, 2, 72, 100), (6, 2, 76, 100), (8, 6, 74, 105),
                (16, 3, 72, 110), (20, 2, 71, 95), (22, 2, 67, 95), (24, 8, 69, 100));
            s.Answer = Pattern(s.Lead, "Answer", 2 * Bar, (0, 2, 76, 100), (2, 2, 74, 90), (4, 4, 72, 100), (12, 2, 71, 90),
                (16, 2, 72, 100), (18, 2, 74, 90), (20, 8, 76, 105));
            var arp = new List<(double, double, int, int)>();
            int[][] chords = { new[] { 57, 60, 64, 69 }, new[] { 53, 57, 60, 65 }, new[] { 48, 52, 55, 60 }, new[] { 55, 59, 62, 67 } };
            for (int i = 0; i < 128; i++) arp.Add((i, 1, chords[i / 32 % 4][i % 4] + (i / 16 % 2 == 1 ? 12 : 0), 70 + i % 4 * 10));
            s.Arp = Pattern(s.Lead, "Arp", 8 * Bar, arp.ToArray());

            var pad = new List<(double, double, int, int)>();
            for (int c = 0; c < 4; c++)
                foreach (int n in chords[c].Take(3)) pad.Add((c * Bar, Bar, n, 80));
            s.Chords = Pattern(s.Pad, "Chords", 4 * Bar, pad.ToArray());

            // Natives: an Infector pattern, as old songs have (no notes written: its tracker columns take them).
            if (s.NativeGenerator != null) s.NativeGenerator.CreatePattern("00", Bar);

            // Onto the Pattern View's note lanes, where the pattern editor shows them; Hook stays in its clip (the piano roll's).
            foreach (var p in new[] { s.Beat, s.Fill, s.Break, s.LineA, s.LineB, s.Answer, s.Arp, s.Chords })
            {
                if (p == null) continue;
                var track = song.SequencesList.FirstOrDefault(q => q.Machine == p.Machine);
                string problem = track == null ? "no track" : layout.MoveNotesToPatternView(track, p);
                if (problem != null) s.Problems.Add($"{p.Machine.Name} {p.Name}: {problem}");
            }

            // The arrangement: 16 bars.
            void Place(IPattern p, params int[] bars)
            {
                if (p == null) return;
                var seq = song.SequencesList.First(q => q.Machine == p.Machine);
                foreach (int b in bars) seq.SetEvent(b * Bar, new SequenceEvent(SequenceEventType.PlayPattern, p));
            }
            Place(s.Arp, 0);
            Place(s.Chords, 0, 4, 8, 12);
            Place(s.Beat, 4, 5, 6, 10, 11, 12, 13, 14);
            Place(s.Fill, 7, 15);
            Place(s.Break, 8);
            Place(s.LineA, 4, 6, 12);
            Place(s.LineB, 10, 14);
            Place(s.Hook, 8, 12);
            Place(s.Answer, 10, 14);
            song.SongEnd = 16 * Bar;
            song.LoopStart = 0;
            song.LoopEnd = 16 * Bar;

            // The Session View: scenes of the song's parts, colours, a return.
            layout.Ensure();
            var doc = layout.Document;
            int Track(MachineCore m) => m == null ? -1 : doc.Tracks.FindIndex(t => t.Machine == m.Name);
            for (int t = 0; t < doc.Tracks.Count; t++)
                for (int sc = 0; sc < 12; sc++) layout.ClearSlot(t, sc);
            void Slot(MachineCore m, int scene, IPattern p)
            {
                int t = Track(m);
                if (t >= 0 && p != null) layout.SetSlot(t, scene, p);
            }
            Slot(s.Lead, 0, s.Arp);
            Slot(s.Pad, 0, s.Chords);
            Slot(s.Drums, 1, s.Beat);
            Slot(s.Bass, 1, s.LineA);
            Slot(s.Pad, 1, s.Chords);
            Slot(s.Drums, 2, s.Beat);
            Slot(s.Bass, 2, s.LineB);
            Slot(s.Lead, 2, s.Hook);
            Slot(s.Pad, 2, s.Chords);
            Slot(s.Drums, 3, s.Break);
            Slot(s.Lead, 3, s.Answer);
            Slot(s.Drums, 4, s.Fill);
            Slot(s.Bass, 4, s.LineA);
            string[] scenes = { "Intro", "Verse", "Drop", "Break", "Fill" };
            for (int i = 0; i < scenes.Length; i++) layout.RenameScene(i, scenes[i]);
            layout.SetSceneTempo(2, 128);
            layout.SetSceneColor(2, 10);
            if (s.Fill != null) layout.SetClipColor(s.Fill, 10);        // orange
            if (s.LineB != null) layout.SetClipColor(s.LineB, 14);     // sky blue
            if (s.Hook != null) layout.SetClipColor(s.Hook, 13);
            if (Track(s.Drums) >= 0) layout.SetTrackColor(Track(s.Drums), 9);
            if (s.Verb != null) layout.AddReturn(s.Verb);

            core.SongNotes?.Write(
                "# Night Drive\n\n" +
                "A minor, 124 BPM. Intro (arp and pad), verse, drop at bar 9, break, fill.\n\n" +
                "## To do\n" +
                "- Bass: more drive in the drop\n" +
                "- Lead: try the Answer an octave down\n" +
                "- Mix: the pad is a little loud against the verb\n\n" +
                "## Session\n" +
                "Scene **Drop** runs at 128 BPM; the Fill scene leads back to the verse.\n");
            return s;
        }
    }
}
