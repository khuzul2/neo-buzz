// Fork (docs/specs/ux-overhaul.md P6c, §9 row 14): Cut, Copy and Paste of machines, as BuzzGUI.MachineView has them (CopySelectedMachines,
// PasteMachines). The WPF app writes a template (XML, with machine presets compressed by LZMA: WPF-only code) to the system clipboard;
// the studio keeps the machines themselves, in this process, and pastes with the engine's own operations, the ones Clone with clips
// uses: each machine again with its settings, its clips and its rows in the sequence, and the wires between the machines copied
// together. So a paste keeps everything a clone does, in the song the machines are in; it does not reach another song, another
// window or the WPF app, and never the system clipboard.
//
//  - Copy: remembers the selected machines (not the Master).
//  - Cut: the same, and marks them as cut. Nothing is deleted until the paste, so cutting loses nothing if you change your mind; the paste
//    then puts the copies where you point and deletes the originals, the copies taking their names, one undo step.
//  - Paste: the copies land with their middle at the point (the last place pointed at on the canvas, a menu's click), or a little below and
//    to the right of the originals when there is none; they are selected.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Machines
{
    internal static class MachineClipboard
    {
        sealed record Held(ReBuzzCore Core, IReadOnlyList<IMachine> Machines, bool Cut);

        static Held held;

        /// <summary>How many machines the clipboard holds (0 when empty).</summary>
        public static int Count => held?.Machines.Count ?? 0;

        /// <summary>Whether the machines held are cut ones, waiting for their paste.</summary>
        public static bool IsCut => held?.Cut == true;

        public static void Clear() => held = null;

        static List<IMachine> Copyable(IEnumerable<IMachine> machines) =>
            machines.Where(m => m.DLL.Info.Type != MachineType.Master && !(m is MachineCore mc && mc.Hidden)).ToList();

        public static bool Copy(ReBuzzCore core, IEnumerable<IMachine> selection) => Hold(core, selection, false);

        public static bool Cut(ReBuzzCore core, IEnumerable<IMachine> selection) => Hold(core, selection, true);

        static bool Hold(ReBuzzCore core, IEnumerable<IMachine> selection, bool cut)
        {
            var list = Copyable(selection);
            if (list.Count == 0) return false;
            held = new Held(core, list, cut);
            return true;
        }

        /// <summary>Whether Paste has something to paste in this song: the machines held are all still in it.</summary>
        public static bool CanPaste(ReBuzzCore core)
        {
            if (held == null || held.Core != core || core.SongCore == null) return false;
            var here = core.SongCore.Machines.ToHashSet();
            return held.Machines.All(here.Contains);
        }

        /// <summary>
        /// Pastes the machines held, one undo step; the copies, in the order held. <paramref name="at"/> is a song position
        /// (the canvas spans -1 to 1) for the middle of the copies.
        /// </summary>
        public static List<IMachine> Paste(ReBuzzCore core, Point? at)
        {
            if (!CanPaste(core)) return new List<IMachine>();
            var from = held.Machines.ToList();
            bool cut = held.Cut;
            var names = from.Select(m => m.Name).ToList();
            List<IMachine> copies;
            using (MachineEdits.Group(core))
            {
                copies = MachineEdits.CloneWithClips(core, from);
                if (copies.Count == 0) return copies;
                if (at is Point p)
                {
                    double cx = from.Average(m => (double)m.Position.Item1), cy = from.Average(m => (double)m.Position.Item2);
                    var moves = new List<(IMachine Machine, float X, float Y)>();
                    for (int i = 0; i < copies.Count; i++)
                    {
                        var src = from[i];
                        var to = CanvasMap.Clamp(new Point(p.X + (src.Position.Item1 - cx), p.Y + (src.Position.Item2 - cy)));
                        moves.Add((copies[i], (float)to.X, (float)to.Y));
                    }
                    MachineEdits.Move(core, moves);
                }
                if (cut)
                {
                    // The originals go, and the copies take their names (a clone has a free one while the original exists).
                    MachineEdits.Delete(core, from);
                    var song = core.SongCore;
                    for (int i = 0; i < copies.Count && i < names.Count; i++)
                        if (copies[i] is MachineCore mc && mc.Name != names[i] && !song.Machines.Any(m => m != mc && m.Name == names[i]))
                            song.RenameMachineUndoable(mc, names[i]);
                }
            }
            if (cut) held = null;      // a cut is pasted once; a copy again and again
            return copies;
        }
    }
}
