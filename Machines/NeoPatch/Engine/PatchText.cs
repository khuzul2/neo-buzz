#nullable disable
// Neo Patch NP1 (docs/specs/neo-patch.md §4.1): a patch as one line of text, what the machine keeps with the song (MachineState)
// and what presets carry. Records are separated by ';', fields by spaces, and nothing in it is a quote or a newline, so it sits in an
// XML property and in a Presets.txt line alike:
//
//   neopatch 1;name Warm%20Pad;n 1 Keys 0 0 0;n 2 Osc 0 0 0 0 0 0 0.5 1 0 0;w 1 0 2 0 1;m 0 5 1 200 4000
//
// n = node: id, kind, global (0 or 1), x, y, then the controls in order (a control missing from an older patch keeps its default).
// w = wire: from node, from port, to node, to port, amount. m = macro mapping: macro (0 … 15), node, control, min, max.
// Anything unreadable is skipped, never thrown: a damaged song loads as much of its patch as is good.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NeoBuzz.Patch
{
    internal static class PatchText
    {
        const string Header = "neopatch 1";

        static string F(float x) => x.ToString("R", CultureInfo.InvariantCulture);

        public static string Save(PatchGraph g)
        {
            var sb = new StringBuilder(Header);
            if (!string.IsNullOrEmpty(g.Name)) sb.Append(";name ").Append(Uri.EscapeDataString(g.Name));
            foreach (var n in g.Nodes)
            {
                sb.Append(";n ").Append(n.Id).Append(' ').Append(n.Kind).Append(' ').Append(n.Global ? 1 : 0).Append(' ').Append(F(n.X)).Append(' ').Append(F(n.Y));
                foreach (float c in n.Controls) sb.Append(' ').Append(F(c));
            }
            foreach (var w in g.Wires)
                sb.Append(";w ").Append(w.FromNode).Append(' ').Append(w.FromPort).Append(' ').Append(w.ToNode).Append(' ').Append(w.ToPort).Append(' ').Append(F(w.Amount));
            foreach (var m in g.Macros)
                sb.Append(";m ").Append(m.Macro).Append(' ').Append(m.NodeId).Append(' ').Append(m.Control).Append(' ').Append(F(m.Min)).Append(' ').Append(F(m.Max));
            for (int i = 0; i < g.MacroLabels.Length; i++)
                if (!string.IsNullOrEmpty(g.MacroLabels[i])) sb.Append(";l ").Append(i).Append(' ').Append(Uri.EscapeDataString(g.MacroLabels[i]));
            return sb.ToString();
        }

        /// <summary>The patch the text holds; an empty graph for empty or unrecognised text (null: not a Neo Patch text).</summary>
        public static PatchGraph Load(string text)
        {
            var g = new PatchGraph();
            if (string.IsNullOrWhiteSpace(text)) return g;
            var records = text.Split(';');
            if (records[0].Trim() != Header) return null;
            var wires = new List<Wire>();
            for (int r = 1; r < records.Length; r++)
            {
                var f = records[r].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length == 0) continue;
                try
                {
                    switch (f[0])
                    {
                        case "name": g.Name = f.Length > 1 ? Uri.UnescapeDataString(f[1]) : ""; break;
                        case "n":
                        {
                            if (f.Length < 6 || !int.TryParse(f[1], out int id) || !NodeInfo.TryParseKind(f[2], out var kind)) break;
                            var n = g.AddWithId(id, kind);
                            if (n == null) break;
                            n.Global = f[3] == "1";
                            n.X = Num(f[4], 0); n.Y = Num(f[5], 0);
                            for (int i = 0; i < n.Controls.Length && 6 + i < f.Length; i++)
                                if (float.TryParse(f[6 + i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v)) n.Set(i, v);
                            break;
                        }
                        case "w":
                            if (f.Length >= 6) wires.Add(new Wire(Int(f[1]), Int(f[2]), Int(f[3]), Int(f[4]), Num(f[5], 1f)));
                            break;
                        case "l":
                            if (f.Length >= 3 && int.TryParse(f[1], out int li) && li >= 0 && li < g.MacroLabels.Length) g.MacroLabels[li] = Uri.UnescapeDataString(f[2]);
                            break;
                        case "m":
                            if (f.Length >= 6) g.Macros.Add(new MacroMapping(Int(f[1]), Int(f[2]), Int(f[3]), Num(f[4], 0), Num(f[5], 1)));
                            break;
                    }
                }
                catch (FormatException) { }
            }
            foreach (var w in wires) g.Connect(g.Find(w.FromNode), w.FromPort, g.Find(w.ToNode), w.ToPort, w.Amount);
            g.Macros.RemoveAll(m => g.Find(m.NodeId) == null || m.Macro < 0 || m.Macro > 15 || m.Control < 0 || m.Control >= g.Find(m.NodeId).Controls.Length);
            return g;
        }

        static int Int(string s) => int.TryParse(s, out int v) ? v : -1;
        static float Num(string s, float def) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && float.IsFinite(v) ? v : def;
    }
}
