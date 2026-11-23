// Fork (docs/specs/native-format.md, NF2): "Export for Buzz/ReBuzz (.bmx)". A separate, explicitly lossy save that writes the
// song as upstream ReBuzz can read it, and tells what upstream cannot hold. The song itself is never touched: no name, no
// dirty flag, no undo or history change (the same rule as SaveSongCopy).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using BuzzGUI.Common;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using ReBuzz.Core;

namespace ReBuzz.FileOps
{
    /// <summary>One thing the export loses or changes: its kind, what it is, and what happens in upstream ReBuzz.</summary>
    internal sealed record ExportLoss(string Category, string Item, string Upstream);

    /// <summary>What an export loses: sorted, with a stable text form (one line a loss).</summary>
    internal sealed class ExportReport
    {
        public static readonly ExportReport Empty = new ExportReport(Array.Empty<ExportLoss>());

        public ExportReport(IEnumerable<ExportLoss> losses)
        {
            Losses = losses
                .Distinct()
                .OrderBy(l => l.Category, StringComparer.Ordinal)
                .ThenBy(l => l.Item, StringComparer.Ordinal)
                .ThenBy(l => l.Upstream, StringComparer.Ordinal)
                .ToList();
        }

        public IReadOnlyList<ExportLoss> Losses { get; }
        public bool IsEmpty => Losses.Count == 0;

        /// <summary>"Category | Item | Upstream", a line each, in order; empty when nothing is lost.</summary>
        public string ToText() => string.Join("\n", Losses.Select(l => $"{l.Category} | {l.Item} | {l.Upstream}"));

        public override string ToString() => ToText();
    }

    /// <summary>Names as Latin-1 text that still reads: "→" is "->", accents Latin-1 has stay, "?" only when nothing else is left.</summary>
    internal static class Latin1Names
    {
        static readonly Dictionary<int, string> Table = new()
        {
            ['→'] = "->", ['←'] = "<-", ['↔'] = "<->", ['⇒'] = "=>", ['⇐'] = "<=", ['↑'] = "^", ['↓'] = "v",
            ['–'] = "-", ['—'] = "-", ['‐'] = "-", ['‑'] = "-", ['−'] = "-", ['‘'] = "'", ['’'] = "'", ['‚'] = ",",
            ['“'] = "\"", ['”'] = "\"", ['„'] = "\"", ['…'] = "...", ['•'] = "*", ['·'] = ".", ['‹'] = "<", ['›'] = ">",
            ['€'] = "EUR", ['™'] = "(TM)", ['≈'] = "~", ['≤'] = "<=", ['≥'] = ">=", ['≠'] = "!=", ['∞'] = "inf",
            ['✓'] = "v", ['✔'] = "v", ['✗'] = "x", ['✘'] = "x", ['♯'] = "#", ['♭'] = "b", ['♮'] = "n", ['♪'] = "*", ['♫'] = "*",
            ['★'] = "*", ['☆'] = "*", ['●'] = "*", ['○'] = "o", ['■'] = "#", ['□'] = "o", ['▲'] = "^", ['▼'] = "v",
            ['►'] = ">", ['▶'] = ">", ['◄'] = "<", ['◀'] = "<", ['Δ'] = "Delta", ['Ω'] = "Ohm", ['μ'] = "u", ['π'] = "pi",
            ['Σ'] = "Sum", ['α'] = "alpha", ['β'] = "beta", ['γ'] = "gamma", ['λ'] = "lambda",
            ['ł'] = "l", ['Ł'] = "L", ['đ'] = "d", ['Đ'] = "D", ['ħ'] = "h", ['ı'] = "i", ['œ'] = "oe", ['Œ'] = "OE",
            ['ŋ'] = "ng", ['ſ'] = "s", ['ǆ'] = "dz", [' '] = " ", [' '] = " ", ['​'] = "", [' '] = " ", [' '] = " ",
        };

        public static string Transliterate(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            bool plain = true;
            foreach (char c in text) if (c > 0xFF) { plain = false; break; }
            if (plain) return text;
            var sb = new StringBuilder(text.Length + 8);
            foreach (var rune in text.EnumerateRunes())
            {
                if (rune.Value <= 0xFF) { sb.Append((char)rune.Value); continue; }
                if (Table.TryGetValue(rune.Value, out var s)) { sb.Append(s); continue; }
                // A letter with a mark Latin-1 hasn't: its base letters ("ś" is "s"). Compatibility forms too ("ﬁ" is "fi").
                var mapped = new StringBuilder();
                bool ok = true;
                foreach (char d in rune.ToString().Normalize(NormalizationForm.FormKD))
                {
                    var cat = CharUnicodeInfo.GetUnicodeCategory(d);
                    if (cat == UnicodeCategory.NonSpacingMark || cat == UnicodeCategory.EnclosingMark) continue;
                    if (d <= 0xFF) mapped.Append(d);
                    else if (Table.TryGetValue(d, out var t)) mapped.Append(t);
                    else { ok = false; break; }
                }
                sb.Append(ok && mapped.Length > 0 ? mapped.ToString() : "?");
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// The export in progress on this thread: what BMXFile's writers ask (names as Latin-1, which events the core data leaves
    /// out) and what the stores keep in the "ExportedLanes" subsection instead. Not set outside an export.
    /// </summary>
    internal sealed class BuzzExport
    {
        [ThreadStatic] static BuzzExport current;
        public static BuzzExport Current => current;

        /// <summary>The highest tick a Buzz sequence holds in the position size upstream writes (two bytes).</summary>
        public const int MaxTick = ushort.MaxValue;

        public static IDisposable Begin(ReBuzzCore buzz)
        {
            var before = current;
            current = new BuzzExport(buzz);
            return new Scope(() => current = before);
        }

        sealed class Scope : IDisposable
        {
            readonly Action end;
            public Scope(Action end) { this.end = end; }
            public void Dispose() => end();
        }

        readonly Dictionary<string, string> machineNames = new(StringComparer.Ordinal);

        BuzzExport(ReBuzzCore buzz)
        {
            // Machine names must stay unique after the change (connections and groups refer to them by name).
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in buzz.SongCore.MachinesList)
            {
                string name = Latin1Names.Transliterate(m.Name);
                if (name != m.Name)
                {
                    string candidate = name;
                    for (int n = 2; used.Contains(candidate); n++) candidate = $"{name} ({n})";
                    name = candidate;
                    machineNames[m.Name] = name;
                }
                used.Add(name);
            }
        }

        /// <summary>A name or text as it is written into the file.</summary>
        public string Text(string s) => s != null && machineNames.TryGetValue(s, out var m) ? m : Latin1Names.Transliterate(s);

        public IReadOnlyDictionary<string, string> RenamedMachines => machineNames;

        /// <summary>Whether the core sequence data leaves this event out (the stores keep it in the "ExportedLanes" subsection).</summary>
        public static bool Omits(SequenceCore lane, int time) => lane.TakeLane || time < 0 || time > MaxTick;

        /// <summary>Why <paramref name="path"/> can't be an export target (a message), or null: a .bmx (or .bmw), and not the open song's own file.</summary>
        public static string TargetProblem(string songFile, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "Give a path for the exported .bmx.";
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".nbl")
                return "Export for Buzz/ReBuzz writes a .bmx. A .nbl is the native format: save it with Save or Save As.";
            if (ext != ".bmx" && ext != ".bmw")
                return "Export for Buzz/ReBuzz writes a .bmx (or .bmw); " + (ext.Length == 0 ? "the path has no extension." : "\"" + ext + "\" is not one.");
            string full = Path.GetFullPath(path);
            if (Directory.Exists(full)) return "That path is a folder.";
            if (!string.IsNullOrEmpty(songFile) && string.Equals(full, Path.GetFullPath(songFile),
                    OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
                return "That is the open song's own file. Export writes a separate copy for Buzz/ReBuzz: choose another name.";
            return null;
        }

        /// <summary>Written to a temporary file beside the target, flushed, then renamed over it: a failure leaves the old file whole.</summary>
        public static void WriteAtomic(string path, byte[] bytes)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(path));
            string temp = Path.Combine(dir, "." + Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var f = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    f.Write(bytes, 0, bytes.Length);
                    f.Flush(true);
                }
                File.Move(temp, path, overwrite: true);
            }
            catch
            {
                try { File.Delete(temp); } catch (IOException) { }
                throw;
            }
        }

        // ------------------------------------------------------------------ the loss report

        static readonly HashSet<string> UpstreamSubSections = new(StringComparer.Ordinal) { "SequenceEditor", "ModernSequenceEditor", "ModernSequenceEditorHorizontal" };

        static readonly Dictionary<string, string> ForkLabels = new(StringComparer.Ordinal)
        {
            ["SessionView"] = "Session View (clips, scenes, layout)",
            ["SongMarkers"] = "song markers",
            ["SongNotes"] = "song notes",
            ["Arrangement"] = "arrangement lane roles, folded tracks and parameter lanes",
            ["ArrangementEvents"] = "clip trims, gain, ids and loops",
            ["TrackColours"] = "track colours",
        };

        public static ExportReport Analyze(ReBuzzCore buzz, IEnumerable<string> subSections, SaveSongCore audio = null)
        {
            var losses = new List<ExportLoss>();
            var song = buzz.SongCore;
            var lanes = song.SequencesList;

            string Lane(int i) => $"Lane {i + 1} ({lanes[i].Machine?.Name})";

            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                var events = lane.EventsList;
                if (lane.TakeLane)
                    losses.Add(new ExportLoss("Takes", $"{Lane(i)}: {events.Count} events",
                        "Written empty in the sequence, so upstream plays nothing from it and has no take lane; NeoBuzz Live restores it when it opens this file."));
                else
                {
                    int late = events.Count(e => Omits(lane, e.Key));
                    if (late > 0)
                        losses.Add(new ExportLoss("Sequence length", $"{Lane(i)}: {late} events past tick {MaxTick}",
                            "Left out of the sequence (Buzz keeps positions in two bytes); NeoBuzz Live restores them when it opens this file."));
                    var kept = events.Where(e => !Omits(lane, e.Key) && e.Value.Type == SequenceEventType.PlayPattern && e.Value.Pattern != null).Select(e => e.Value).ToList();
                    int trims = kept.Count(e => e.Offset != 0 || e.End != 0);
                    int gains = kept.Count(e => e.Gain != 1.0);
                    int clips = kept.Count(e => e.Clip != 0);
                    if (trims > 0)
                        losses.Add(new ExportLoss("Clip trims", $"{Lane(i)}: {trims} clips", "Play the whole pattern from its first row."));
                    if (gains > 0)
                        losses.Add(new ExportLoss("Clip gain", $"{Lane(i)}: {gains} clips", "Play at full volume."));
                    if (clips > 0)
                        losses.Add(new ExportLoss("Clip ids", $"{Lane(i)}: {clips} events", "Each event plays on its own; clip ids and loop regions are ignored."));
                }
                var role = buzz.Arrangement?.RoleOf(lane) ?? LaneRole.Main;
                if (role == LaneRole.Automation)
                    losses.Add(new ExportLoss("Lane roles", $"{Lane(i)}: automation lane", "Plays as an ordinary sequence."));
            }

            foreach (var machine in song.MachinesList)
            {
                if (machine.MachineDLL?.Info?.Author == "neo-buzz")
                {
                    losses.Add(new ExportLoss("Machines", $"{machine.Name} ({machine.DLL.Name})",
                        "Not in upstream Buzz/ReBuzz: shows as a missing machine; its connections, parameters and data stay in the file."));
                    if (machine.DLL.Name.StartsWith("Neo Plug", StringComparison.Ordinal))
                        losses.Add(new ExportLoss("Plug-in state", machine.Name, "The hosted plug-in and its state are not loaded without the machine."));
                }
                foreach (var pattern in machine.PatternsList)
                    foreach (var column in pattern.ColumnsList.OfType<PatternColumnCore>())
                    {
                        if (!column.EnginePlays) continue;
                        int n = column.GetEvents(0, int.MaxValue).Count();
                        if (n == 0) continue;
                        string target = $"{column.Machine?.Name}.{column.Parameter?.Name}" + (column.Track > 0 ? $" track {column.Track + 1}" : "");
                        losses.Add(new ExportLoss(column.HasRamps ? "Automation ramps" : "Automation", $"{machine.Name} / {pattern.Name}: {target}, {n} events",
                            column.HasRamps ? "Kept in the pattern data but never played upstream; ramps would play as steps." : "Kept in the pattern data but never played upstream."));
                    }
            }

            int midi = 0;
            foreach (var c in buzz.MidiControllerAssignments.ContollerBindings)
            {
                var machine = c.Parameter.Group.Machine;
                var all = machine.AllParameters().Where(p => p.Flags.HasFlag(ParameterFlags.State));
                int index = all.FindIndex(m => m == c.Parameter);
                if (index < 0) continue;
                int group = machine.ParameterGroups.IndexOf(c.Parameter.Group);
                if (group > 255 || c.Track > 255 || index > 255 || c.MidiChannel > 255 || c.MidiController > 255) midi++;
            }
            if (midi > 0)
                losses.Add(new ExportLoss("MIDI bindings", $"{midi} bindings past index 255",
                    "Upstream's MIDI section keeps the bindings that fit in a byte; the full list is in the MID2 section, which only NeoBuzz Live reads."));

            foreach (var m in song.MachinesList)
                if (Latin1Names.Transliterate(m.Name) != m.Name)
                    losses.Add(new ExportLoss("Names", $"Machine \"{m.Name}\"", "Written as Latin-1 text (\"" + Latin1Names.Transliterate(m.Name) + "\"); NeoBuzz Live shows the new name, and data kept by the old name (Session View, folds) is not matched."));
            foreach (var m in song.MachinesList)
                foreach (var p in m.PatternsList)
                    if (Latin1Names.Transliterate(p.Name) != p.Name)
                        losses.Add(new ExportLoss("Names", $"Pattern \"{p.Name}\" of {m.Name}", "Written as Latin-1 text (\"" + Latin1Names.Transliterate(p.Name) + "\"). The Modern Pattern Editor's own note data names patterns in ASCII (as upstream writes it) and may not find it by that name."));

            // Audio clips (neo-audio.md): their assets go into waves of the wavetable while slots last; upstream has no player for them.
            if (audio != null)
            {
                if (audio.AudioWaves > 0)
                    losses.Add(new ExportLoss("Audio", $"{audio.AudioWaves} audio assets written as waves", "Upstream has no Neo Audio machine, so the clips are not played; the waves are in the wavetable."));
                foreach (var name in audio.AudioLosses)
                    losses.Add(new ExportLoss("Audio", name, "Not written: the wavetable has no free slot. Its clips are silent when the file is opened again."));
            }

            int snapshots = buzz.Snapshots?.List().Count ?? 0;
            if (snapshots > 0)
                losses.Add(new ExportLoss("History", $"{snapshots} snapshots", "Not written: .bmx files do not hold the song history."));

            foreach (var name in subSections.Where(n => !UpstreamSubSections.Contains(n) && n != ArrangementEventStore.ExportedLanesName))
                losses.Add(new ExportLoss("NeoBuzz Live data", ForkLabels.TryGetValue(name, out var label) ? label : name,
                    "Kept in the file for NeoBuzz Live; upstream ignores it and drops it the next time it saves."));

            return new ExportReport(losses);
        }
    }
}
