#nullable disable
// Neo Patch NP2: the node menu of the inner canvas (docs/specs/neo-patch.md §4.5), drawn on the canvas itself, so it is the same in the Windows app and the
// studio and can be seen in the gallery. A list with an optional search box: typing filters it, Up and Down move, Return picks, Escape closes.
// Used for adding a node (right-click on the canvas), a node's actions (right-click on a node), a choice control's choices, the macro picker and a
// one-line prompt (a macro's name).

using System;
using System.Collections.Generic;
using System.Linq;
using NeoBuzz.Editor;

namespace NeoBuzz.Patch
{
    internal sealed class MenuItemModel
    {
        public string Label, Detail;
        public NeoColor Colour;
        public Action Pick;
        /// <summary>A heading: shown, never picked.</summary>
        public bool Header;
        public bool Checked;
    }

    internal sealed class PatchMenu
    {
        public const int RowHeight = 24, MaxRows = 10, Width = 312, SearchHeight = 32;

        readonly List<MenuItemModel> all;

        public PatchMenu(string title, IEnumerable<MenuItemModel> items, bool search, double x, double y, Action<string> prompt = null, string promptText = "")
        {
            Title = title;
            all = items.ToList();
            Search = search;
            X = x;
            Y = y;
            Prompt = prompt;
            Text = promptText ?? "";
            Refilter();
        }

        public string Title { get; }
        public bool Search { get; }
        public double X { get; set; }
        public double Y { get; set; }
        /// <summary>A prompt: no list, a line of text; Return hands it over.</summary>
        public Action<string> Prompt { get; }
        public string Text { get; private set; }
        public List<MenuItemModel> Visible { get; } = new List<MenuItemModel>();
        public int Selected { get; private set; }
        public int Scroll { get; private set; }

        public bool WantsText => Search || Prompt != null;

        public int Rows => Math.Min(MaxRows, Visible.Count);
        public double Height => 26 + (Search || Prompt != null ? SearchHeight : 0) + (Prompt != null ? 0 : Rows * RowHeight + 6);

        void Refilter()
        {
            Visible.Clear();
            var terms = (Text ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var it in all)
            {
                if (terms.Length == 0) { Visible.Add(it); continue; }
                if (it.Header) continue;
                string hay = (it.Label + " " + it.Detail).ToLowerInvariant();
                if (terms.All(t => hay.Contains(t))) Visible.Add(it);
            }
            if (terms.Length > 0)
            {
                // A name that starts with what was typed first, then a name that contains it, then the nodes only the description matches (stable within each).
                int Rank(MenuItemModel i)
                {
                    string l = i.Label.ToLowerInvariant();
                    return l.StartsWith(terms[0]) ? 0 : terms.All(t => l.Contains(t)) ? 1 : 2;
                }
                var ranked = Visible.Select((it, k) => (it, k)).OrderBy(x => Rank(x.it)).ThenBy(x => x.k).Select(x => x.it).ToList();
                Visible.Clear();
                Visible.AddRange(ranked);
            }
            Selected = Math.Max(0, Visible.FindIndex(i => !i.Header));
            Scroll = 0;
        }

        public void Type(string s)
        {
            foreach (char c in s ?? "") if (!char.IsControl(c) && Text.Length < 40) Text += c;
            if (Prompt == null) Refilter();
        }

        public void Backspace()
        {
            if (Text.Length == 0) return;
            Text = Text.Substring(0, Text.Length - 1);
            if (Prompt == null) Refilter();
        }

        public void Move(int d)
        {
            if (Visible.Count == 0) return;
            int i = Selected;
            do { i = Math.Clamp(i + d, 0, Visible.Count - 1); } while (Visible[i].Header && i > 0 && i < Visible.Count - 1);
            if (Visible[i].Header) return;
            Selected = i;
            if (Selected < Scroll) Scroll = Selected;
            if (Selected >= Scroll + MaxRows) Scroll = Selected - MaxRows + 1;
        }

        public void Wheel(int notches)
        {
            Scroll = Math.Clamp(Scroll - notches, 0, Math.Max(0, Visible.Count - MaxRows));
        }

        /// <summary>Picks the highlighted row (or hands over the prompt's text); true when something was picked.</summary>
        public bool Accept()
        {
            if (Prompt != null) { Prompt(Text); return true; }
            if (Selected < 0 || Selected >= Visible.Count || Visible[Selected].Header) return false;
            Visible[Selected].Pick?.Invoke();
            return true;
        }

        /// <summary>The box, placed so that it stays inside a surface of a size.</summary>
        public NeoRect Box(double w, double h) =>
            new NeoRect(Math.Clamp(X, 4, Math.Max(4, w - Width - 4)), Math.Clamp(Y, 4, Math.Max(4, h - Height - 4)), Width, Height);

        /// <summary>The row at a surface point (-1: not a row).</summary>
        public int RowAt(double x, double y, double w, double h)
        {
            var b = Box(w, h);
            double top = b.Y + 26 + (Search || Prompt != null ? SearchHeight : 0);
            if (Prompt != null || x < b.X || x > b.Right || y < top) return -1;
            int row = (int)((y - top) / RowHeight);
            int i = Scroll + row;
            return row >= 0 && row < Rows && i < Visible.Count ? i : -1;
        }

        public void Draw(INeoCanvas cv, double w, double h)
        {
            var b = Box(w, h);
            for (int i = 3; i >= 1; i--) cv.Rect(new NeoRect(b.X - i, b.Y - i + 3, b.Width + 2 * i, b.Height + 2 * i), NeoColor.FromArgb((byte)(14 * (4 - i)), 0, 0, 0), NeoStroke.None, 8 + i);
            cv.Rect(b, Tokens.SurfacePanel, new NeoStroke(Tokens.SurfaceBorder, 1), 8);
            cv.Text(Title, b.X + 12, b.Y + 6, Tokens.FontCaption, Palette.DimText, true, maxWidth: b.Width - 24);
            double y = b.Y + 26;
            if (Search || Prompt != null)
            {
                var box = new NeoRect(b.X + 8, y, b.Width - 16, 24);
                cv.Rect(box, Tokens.SurfaceBase, new NeoStroke(Tokens.Accent, 1), 4);
                string shown = Text.Length == 0 ? (Prompt != null ? "type a name, Return to accept" : "type to search") : Text;
                cv.Text(shown, box.X + 8, box.Y + 5, Tokens.FontBody, Text.Length == 0 ? Tokens.TextPlaceholder : Palette.Text, maxWidth: box.Width - 24);
                double cx = box.X + 8 + (Text.Length == 0 ? 0 : cv.MeasureText(Text, Tokens.FontBody));
                cv.Line(cx + 1, box.Y + 5, cx + 1, box.Y + 19, new NeoStroke(Palette.Text, 1));
                y += SearchHeight;
            }
            if (Prompt != null) return;
            cv.PushClip(new NeoRect(b.X, y, b.Width, Rows * RowHeight + 6));
            for (int r = 0; r < Rows; r++)
            {
                int i = Scroll + r;
                var it = Visible[i];
                var row = new NeoRect(b.X + 4, y + r * RowHeight, b.Width - 8, RowHeight);
                if (it.Header)
                {
                    cv.Text(it.Label.ToUpperInvariant(), row.X + 8, row.Y + 7, 10, it.Colour.IsVisible ? it.Colour : Palette.DimText, true);
                    continue;
                }
                if (i == Selected) cv.Rect(row, Tokens.SelectionFill, NeoStroke.None, 4);
                if (it.Colour.IsVisible) cv.Ellipse(new NeoPoint(row.X + 12, row.Y + 12), 4, 4, it.Colour, NeoStroke.None);
                cv.Text((it.Checked ? "✓ " : "") + it.Label, row.X + 24, row.Y + 4, Tokens.FontBody, i == Selected ? NeoColor.FromRgb(255, 255, 255) : Palette.Text, maxWidth: row.Width - 36 - (it.Detail != null ? 110 : 0));
                if (!string.IsNullOrEmpty(it.Detail))
                    cv.Text(it.Detail, row.Right - 8, row.Y + 6, 11, i == Selected ? NeoColor.FromRgb(0xD0, 0xE0, 0xF8) : Palette.DimText, align: NeoAlign.Right, maxWidth: 104);
            }
            cv.PopClip();
            if (Visible.Count == 0) cv.Text("Nothing matches.", b.X + 12, y + 6, Tokens.FontBody, Palette.DimText);
            if (Visible.Count > MaxRows)
            {
                double th = Rows * RowHeight, bh = Math.Max(16, th * MaxRows / Visible.Count);
                double by = y + (th - bh) * Scroll / Math.Max(1, Visible.Count - MaxRows);
                cv.Rect(new NeoRect(b.Right - 5, by, 3, bh), Tokens.ControlTrack, NeoStroke.None, 1.5);
            }
        }
    }
}
