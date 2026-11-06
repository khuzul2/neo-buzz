#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ReBuzz.Core.Notes
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): Song Notes' formatting commands on the Markdown text
    /// (<see cref="NotesMarkdown"/>), shared by both apps' Song Notes views (the toolbar and the
    /// formatting keys). Each takes the text and the selection (from ≤ to) and gives the new text
    /// and the selection to show; the views write it as one step on the song's history.
    /// </summary>
    internal static class NotesFormatting
    {
        internal readonly record struct Result(string Text, int From, int To);

        public static Result Bold(string t, int a, int b) => Emphasis(t, a, b, 2);

        public static Result Italic(string t, int a, int b) => Emphasis(t, a, b, 1);

        public static Result Strike(string t, int a, int b) => Wrap(t, a, b, "~~", "~~");

        public static Result Underline(string t, int a, int b) => Wrap(t, a, b, "<u>", "</u>");

        /// <summary>The word at the caret when nothing is selected.</summary>
        static (int From, int To) Word(string t, int a, int b)
        {
            if (a != b) return (a, b);
            int from = a, to = a;
            while (from > 0 && char.IsLetterOrDigit(t[from - 1])) from--;
            while (to < t.Length && char.IsLetterOrDigit(t[to])) to++;
            return (from, to);
        }

        static (string, int, int) Clamp(string t, int a, int b)
        {
            t ??= "";
            int from = Math.Clamp(Math.Min(a, b), 0, t.Length), to = Math.Clamp(Math.Max(a, b), 0, t.Length);
            return (t, from, to);
        }

        /// <summary>Stars around the selection: <paramref name="stars"/> 2 for bold, 1 for italic; again takes them off.</summary>
        static Result Emphasis(string t, int a, int b, int stars)
        {
            (t, a, b) = Clamp(t, a, b);
            (a, b) = Word(t, a, b);
            int before = 0, after = 0;
            while (a - before > 0 && t[a - before - 1] == '*') before++;
            while (b + after < t.Length && t[b + after] == '*') after++;
            int inside = 0;
            while (inside < (b - a) / 2 && t[a + inside] == '*' && t[b - 1 - inside] == '*') inside++;
            bool has(int n) => stars == 1 ? n % 2 == 1 : n >= 2;
            if (has(before) && has(after))
                return new Result(t.Remove(b, stars).Remove(a - stars, stars), a - stars, b - stars);
            if (has(inside) && b - a > 2 * stars)
                return new Result(t.Remove(b - stars, stars).Remove(a, stars), a, b - 2 * stars);
            string mark = new string('*', stars);
            return new Result(t.Insert(b, mark).Insert(a, mark), a + stars, b + stars);
        }

        static Result Wrap(string t, int a, int b, string open, string close)
        {
            (t, a, b) = Clamp(t, a, b);
            (a, b) = Word(t, a, b);
            if (a >= open.Length && b + close.Length <= t.Length && t.Substring(a - open.Length, open.Length) == open && t.Substring(b, close.Length) == close)
                return new Result(t.Remove(b, close.Length).Remove(a - open.Length, open.Length), a - open.Length, b - open.Length);
            if (b - a >= open.Length + close.Length && t.Substring(a, open.Length) == open && t.Substring(b - close.Length, close.Length) == close)
                return new Result(t.Remove(b - close.Length, close.Length).Remove(a, open.Length), a, b - open.Length - close.Length);
            return new Result(t.Insert(b, close).Insert(a, open), a + open.Length, b + open.Length);
        }

        /// <summary>[selection](https://) with the address selected, to type over.</summary>
        public static Result Link(string t, int a, int b)
        {
            (t, a, b) = Clamp(t, a, b);
            (a, b) = Word(t, a, b);
            string label = t.Substring(a, b - a);
            string url = "https://";
            if (Regex.IsMatch(label, @"^(https?://|www\.)\S+$", RegexOptions.IgnoreCase))
            {
                url = label;
                label = "link";
            }
            if (label.Length == 0) label = "link";
            string link = "[" + label + "](" + url + ")";
            int urlAt = a + label.Length + 3;
            return new Result(t.Substring(0, a) + link + t.Substring(b), urlAt, urlAt + url.Length);
        }

        static readonly Regex Marker = new(@"^(\s*)(#{1,6}\s+|[-*+]\s+|\d{1,9}[.)]\s+)?", RegexOptions.Compiled);

        /// <summary>Changes the start of each line the selection touches.</summary>
        static Result Lines(string t, int a, int b, Func<IReadOnlyList<string>, IReadOnlyList<string>> change)
        {
            (t, a, b) = Clamp(t, a, b);
            int from = t.LastIndexOf('\n', Math.Max(0, a - 1)) + 1;
            if (a == 0) from = 0;
            int end = b > a && b > 0 && t[b - 1] == '\n' ? b - 1 : b;
            int to = t.IndexOf('\n', end);
            if (to < 0) to = t.Length;
            var lines = t.Substring(from, to - from).Split('\n');
            var changed = change(lines);
            string joined = string.Join("\n", changed);
            return new Result(t.Substring(0, from) + joined + t.Substring(to), from, from + joined.Length);
        }

        /// <summary>Heading: a larger line; again for a smaller heading, a fourth time back to text.</summary>
        public static Result Heading(string t, int a, int b) => Lines(t, a, b, lines =>
        {
            var m = Regex.Match(lines[0], @"^\s*(#{1,6})\s");
            int level = m.Success ? m.Groups[1].Length : 0;
            int next = level >= 3 ? 0 : level + 1;
            return lines.Select(l =>
            {
                string rest = Marker.Replace(l, "", 1).TrimStart();
                return next == 0 ? rest : new string('#', next) + " " + rest;
            }).ToList();
        });

        public static Result Bullets(string t, int a, int b) => Lines(t, a, b, lines =>
        {
            bool all = lines.Where(l => l.Trim().Length > 0).All(l => Regex.IsMatch(l, @"^\s*[-*+]\s"));
            return lines.Select(l =>
            {
                if (l.Trim().Length == 0) return l;
                var m = Marker.Match(l);
                string rest = l.Substring(m.Length);
                return all ? m.Groups[1].Value + rest : m.Groups[1].Value + "- " + rest;
            }).ToList();
        });

        public static Result Numbers(string t, int a, int b) => Lines(t, a, b, lines =>
        {
            bool all = lines.Where(l => l.Trim().Length > 0).All(l => Regex.IsMatch(l, @"^\s*\d{1,9}[.)]\s"));
            int n = 0;
            return lines.Select(l =>
            {
                if (l.Trim().Length == 0) return l;
                var m = Marker.Match(l);
                string rest = l.Substring(m.Length);
                return all ? m.Groups[1].Value + rest : m.Groups[1].Value + (++n) + ". " + rest;
            }).ToList();
        });

        /// <summary>A colour from the notes made light enough to read on a dark page (as ARGB, opaque).</summary>
        public static uint Readable(uint argb)
        {
            byte r = (byte)(argb >> 16), g = (byte)(argb >> 8), bl = (byte)argb;
            double luminance = (0.2126 * r + 0.7152 * g + 0.0722 * bl) / 255;
            if (luminance >= 0.35) return argb | 0xFF000000;
            double k = (0.35 - luminance) / (1 - luminance);
            byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * k);
            return 0xFF000000 | (uint)Mix(r) << 16 | (uint)Mix(g) << 8 | Mix(bl);
        }
    }
}
