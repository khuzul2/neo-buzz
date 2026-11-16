// Fork (docs/specs/ux-overhaul.md, the parameter panel, layout D): Knobs mode. A section is a card (the Neo editors' Section): its choices
// (strips, toggles, drop-downs) on top, two to a row where they fit, then its knobs in one grid. Docked, the cards sit in one row that
// scrolls sideways at the panel's height, and a card is never taller than the panel: a section that doesn't fit splits ("Voice 1/2"), and
// nothing scrolls down unless the panel is shorter than one row of knobs. Floating, the cards are sized to their content and flow to fill the
// width (CardFlow: as many columns as fit). The track table is the last card. Cells are made once per parameter and moved between rebuilds (a search keeps typing fast
// on a machine with hundreds of parameters).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Core;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal sealed class DeviceCards : Border
    {
        readonly IMachine machine;
        readonly ReBuzzCore core;
        readonly NeoColor accent;
        readonly Dictionary<DeviceParam, DeviceCell> cells = new();
        readonly List<(DeviceCard Card, Border Control)> shown = new();
        readonly ScrollViewer scroller;
        Panel host;
        readonly DeviceList table;
        Border tableCard, tableCardControl;
        DeviceSection tableSection;
        bool sideways;
        // What the cards were last asked to show, for a split that follows the panel's height.
        IReadOnlyList<DeviceCard> lastCards = Array.Empty<DeviceCard>();
        List<int> lastColumns = new();
        double splitHeight = -1;

        /// <summary>The height of a card's frame around its content: the section's margin, border, padding and title.</summary>
        public const double CardChrome = 44, CardChromeWidth = 30, ScrollBarRoom = 12;

        public DeviceCards(IMachine machine, NeoColor accent)
        {
            this.machine = machine;
            this.accent = accent;
            core = DeviceEdits.CoreOf(machine);
            table = new DeviceList(machine) { Embedded = true };
            table.FocusChanged += p => FocusChanged?.Invoke(p);
            host = new CardFlow();
            scroller = new ScrollViewer
            {
                Content = host,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            };
            Child = scroller;
            SizeChanged += (s, e) => { if (sideways && Math.Abs(e.NewSize.Height - splitHeight) > 1 && lastCards.Count > 0) SetCards(lastCards, tableSection, lastColumns); };
            Avalonia.Automation.AutomationProperties.SetName(this, "Knobs");
        }

        public ScrollViewer Scroller => scroller;

        /// <summary>The cards in view, in order (tests).</summary>
        public IReadOnlyList<(DeviceCard Card, Border Control)> Shown => shown;

        /// <summary>The track table card, or null.</summary>
        public Border TableCard => tableCard;

        public DeviceList Table => table;

        /// <summary>The menu opened last (tests).</summary>
        public ContextMenu LastMenu { get; private set; }

        public bool Sideways => sideways;

        /// <summary>A control was touched: the explain strip speaks of its parameter.</summary>
        public event Action<DeviceParam> FocusChanged;

        /// <summary>The menu changed something the view shows (a pin, a reset).</summary>
        public event Action Changed;

        /// <summary>Docked: one row of cards, scrolling sideways. Else they wrap.</summary>
        public void SetSideways(bool on)
        {
            if (sideways == on) return;
            sideways = on;
            var items = host.Children.ToList();
            host.Children.Clear();
            Panel next = on ? new StackPanel { Orientation = Orientation.Horizontal } : new CardFlow();
            scroller.Content = next;
            host = next;
            foreach (var c in items) next.Children.Add(c);
            scroller.HorizontalScrollBarVisibility = on ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            scroller.VerticalScrollBarVisibility = on ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
            splitHeight = -1;
        }

        /// <summary>Docked, the panel is shorter than a card with one row of knobs: only then does it scroll down.</summary>
        public bool ScrollsDown => scroller.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sideways;

        Panel Host => host;

        /// <summary>The room a docked card has: the panel's height less the sideways scroll bar's; infinity when it isn't docked or isn't laid out yet.</summary>
        double RoomHeight => sideways && Bounds.Height > 1 ? Math.Max(0, Bounds.Height - ScrollBarRoom) : double.PositiveInfinity;

        /// <summary>Shows these cards, and the track table's columns after them.</summary>
        public void SetCards(IReadOnlyList<DeviceCard> cards, DeviceSection tableSection, List<int> tableColumns)
        {
            lastCards = cards;
            lastColumns = tableColumns;
            splitHeight = sideways ? Bounds.Height : -1;
            foreach (var cell in cells.Values) if (cell.Parent is Panel p) p.Children.Remove(cell);
            Host.Children.Clear();
            shown.Clear();
            double room = RoomHeight;
            foreach (var card in double.IsInfinity(room) ? cards : Fit(cards, room))
            {
                var section = Build(card);
                Host.Children.Add(section);
                shown.Add((card, section));
            }
            if (sideways)
            {
                // Scrolls down only where the panel can't hold even a card with one row of knobs.
                double least = CardChrome + KnobRowHeight(cards.SelectMany(c => c.Knobs).Take(1).ToList());
                scroller.VerticalScrollBarVisibility = !double.IsInfinity(room) && room < least ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }
            this.tableSection = tableSection;
            tableCard = null;
            if (tableSection != null && tableColumns.Count > 0)
            {
                table.SetItems(new DeviceItem[] { new TableItem(tableSection, tableColumns) });
                tableCardControl ??= new Section(tableSection.Name, accent, table) { VerticalAlignment = VerticalAlignment.Top };
                tableCard = tableCardControl;
                Host.Children.Add(tableCard);
            }
            scroller.Offset = default;
        }

        double KnobRowHeight(IReadOnlyList<DeviceParam> knobs) => knobs.Count == 0 ? DeviceKnob.Diameter + 34 : knobs.Max(k => CellOf(k).Height);

        /// <summary>The height of a card holding these choices and knobs, in <paramref name="cols"/> columns.</summary>
        double HeightOf(IReadOnlyList<DeviceParam> choices, IReadOnlyList<DeviceParam> knobs, int cols, double knobRow)
        {
            double h = CardChrome, x = 0, row = 0;
            foreach (var c in choices)
            {
                var cell = CellOf(c);
                if (x > 0 && x + cell.Width > cols * DeviceCell.CellWidth + 0.5) { h += row; x = 0; row = 0; }
                x += cell.Width;
                row = Math.Max(row, cell.Height);
            }
            h += row;
            if (knobs.Count > 0) h += ((knobs.Count + cols - 1) / cols) * knobRow;
            return h;
        }

        /// <summary>
        /// A docked section's cards, each no taller than the room: a section is cut into as few as it takes, its choices first, then its
        /// knobs (a card holds at least one thing; a panel too short for one row of knobs scrolls instead).
        /// </summary>
        List<DeviceCard> Fit(IReadOnlyList<DeviceCard> cards, double room)
        {
            var result = new List<DeviceCard>();
            foreach (var group in cards.GroupBy(c => c.Section))
            {
                var all = group.SelectMany(c => c.Params).ToList();
                var choices = all.Where(p => p.IsChoice).ToList();
                var knobs = all.Where(p => !p.IsChoice).ToList();
                double knobRow = KnobRowHeight(knobs);
                var chunks = new List<(List<DeviceParam> Choices, List<DeviceParam> Knobs)>();
                var cc = new List<DeviceParam>();
                var kk = new List<DeviceParam>();
                foreach (var c in choices)
                {
                    cc.Add(c);
                    if (cc.Count > 1 && HeightOf(cc, kk, DeviceCard.Columns, knobRow) > room)
                    {
                        cc.RemoveAt(cc.Count - 1);
                        chunks.Add((cc, kk));
                        cc = new List<DeviceParam> { c };
                        kk = new List<DeviceParam>();
                    }
                }
                foreach (var k in knobs)
                {
                    kk.Add(k);
                    if (kk.Count > 1 && (kk.Count - 1) % DeviceCard.Columns == 0 && HeightOf(cc, kk, DeviceCard.Columns, knobRow) > room)
                    {
                        kk.RemoveAt(kk.Count - 1);
                        chunks.Add((cc, kk));
                        cc = new List<DeviceParam>();
                        kk = new List<DeviceParam> { k };
                    }
                }
                if (cc.Count > 0 || kk.Count > 0 || chunks.Count == 0) chunks.Add((cc, kk));
                int n = chunks.Count;
                for (int i = 0; i < n; i++)
                    result.Add(new DeviceCard
                    {
                        Section = group.Key,
                        Index = i,
                        Count = n,
                        Title = n > 1 ? $"{group.Key.Name} {i + 1}/{n}" : group.Key.Name,
                        Params = chunks[i].Choices.Concat(chunks[i].Knobs).ToList(),
                    });
            }
            return result;
        }

        /// <summary>One card: its choices on top (flowing, so two share a row where they fit), then its knobs in a grid.</summary>
        Section Build(DeviceCard card)
        {
            var choices = card.Choices.ToList();
            var knobs = card.Knobs.ToList();
            var top = new WrapPanel { Orientation = Orientation.Horizontal };
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var p in choices) top.Children.Add(CellOf(p));
            foreach (var p in knobs) grid.Children.Add(CellOf(p));
            var stack = new StackPanel { Children = { top, grid } };
            if (choices.Count == 0) top.IsVisible = false;
            if (knobs.Count == 0) grid.IsVisible = false;
            foreach (var p in card.Params) CellOf(p).RefreshMarks();
            double widest = choices.Count == 0 ? 0 : choices.Max(p => CellOf(p).Width);
            int minCols = Math.Max(1, Math.Max(Math.Min(knobs.Count, DeviceCard.Columns), (int)Math.Ceiling(widest / DeviceCell.CellWidth - 0.001)));
            if (knobs.Count == 0 && choices.Count > 0) minCols = Math.Max(minCols, 2);
            int maxCols = sideways ? minCols : Math.Max(minCols, Math.Min(MaxColumns, knobs.Count));
            var info = new CardInfo
            {
                MinCols = minCols,
                MaxCols = maxCols,
                SetCols = n => { top.Width = grid.Width = n * DeviceCell.CellWidth; },
            };
            info.SetCols(minCols);
            return new Section(card.Title, accent, stack) { VerticalAlignment = VerticalAlignment.Top, Tag = info };
        }

        /// <summary>The widest a floating card grows (its knobs in this many columns).</summary>
        public const int MaxColumns = 8;

        DeviceCell CellOf(DeviceParam p)
        {
            if (!cells.TryGetValue(p, out var cell))
            {
                cell = new DeviceCell(machine, p, core, accent, Menu, q => FocusChanged?.Invoke(q));
                cells[p] = cell;
            }
            return cell;
        }

        /// <summary>The cell of a parameter (made when asked), for tests and the jump.</summary>
        public DeviceCell Cell(DeviceParam p) => cells.TryGetValue(p, out var c) ? c : null;

        void Menu(DeviceCell cell)
        {
            var menu = DeviceMenus.ForParam(machine, cell.Param, cell.BeginType, () => Changed?.Invoke());
            LastMenu = menu;
            menu.Open(cell);
        }

        /// <summary>Scrolls to the section's first card: its left edge when sideways, its top else. False if no card shows it.</summary>
        public bool JumpTo(DeviceSection section)
        {
            Control target = section == tableSection && tableCard != null ? tableCard : shown.FirstOrDefault(s => s.Card.Section == section).Control;
            if (target == null) return false;
            var at = target.TranslatePoint(new Point(0, 0), Host);
            if (at == null) return false;
            scroller.Offset = sideways ? new Vector(Math.Max(0, at.Value.X - 4), scroller.Offset.Y) : new Vector(scroller.Offset.X, Math.Max(0, at.Value.Y - 4));
            return true;
        }

        /// <summary>The card whose left (sideways) or top edge is the scroll position: what a chip jump landed on (tests).</summary>
        public DeviceCard LeadingCard()
        {
            double edge = sideways ? scroller.Offset.X : scroller.Offset.Y;
            foreach (var (card, control) in shown)
            {
                var at = control.TranslatePoint(new Point(0, 0), Host);
                if (at != null && (sideways ? at.Value.X : at.Value.Y) >= edge - 8) return card;
            }
            return null;
        }

        /// <summary>A gesture or an editor is open somewhere in the cards.</summary>
        public bool Busy => cells.Values.Any(c => c.Parent != null && c.Busy) || table.Dragging || table.IsTyping;

        /// <summary>Shows what moved outside the controls (automation, a preset, the machine itself).</summary>
        public void Poll()
        {
            foreach (var (card, _) in shown)
                foreach (var p in card.Params)
                    if (cells.TryGetValue(p, out var cell) && !cell.Busy) cell.Sync();
            table.Poll();
        }
    }

    /// <summary>How wide a card goes: its columns of knobs, from what its content needs to what its knobs can use.</summary>
    internal sealed class CardInfo
    {
        public int MinCols, MaxCols, Cols;
        /// <summary>Width a row's leftover gives this card's frame beyond its columns.</summary>
        public double Extra;
        public Action<int> SetCols;

        public double Width(int cols) => cols * DeviceCell.CellWidth + DeviceCards.CardChromeWidth;
    }

    /// <summary>
    /// The floating cards: each is as wide as its content needs and they flow in rows; a row's leftover width goes to its cards as extra
    /// columns (up to what their knobs can use), so the width of the window is filled, not left empty at the right.
    /// </summary>
    internal sealed class CardFlow : Panel
    {
        public IReadOnlyList<(int First, int Count)> Rows { get; private set; } = Array.Empty<(int, int)>();

        static CardInfo InfoOf(Control c) => c.Tag as CardInfo;

        protected override Size MeasureOverride(Size available)
        {
            double width = double.IsInfinity(available.Width) ? 1e6 : available.Width;
            var kids = Children.OfType<Control>().Where(c => c.IsVisible).ToList();
            var rows = new List<List<Control>>();
            var row = new List<Control>();
            double used = 0;
            foreach (var c in kids)
            {
                var info = InfoOf(c);
                double w = info != null ? info.Width(info.MinCols) : c.DesiredSize.Width;
                if (info == null) { c.Measure(Size.Infinity); w = c.DesiredSize.Width; }
                if (row.Count > 0 && used + w > width) { rows.Add(row); row = new List<Control>(); used = 0; }
                row.Add(c);
                used += w;
                if (info != null) info.Cols = info.MinCols;
            }
            if (row.Count > 0) rows.Add(row);
            foreach (var r in rows)
            {
                double left = width - r.Sum(c => InfoOf(c) is { } i ? i.Width(i.Cols) : c.DesiredSize.Width);
                bool grew = true;
                while (left >= DeviceCell.CellWidth && grew)
                {
                    grew = false;
                    foreach (var c in r.Where(c => InfoOf(c) is { } i && i.Cols < i.MaxCols).OrderByDescending(c => InfoOf(c).MaxCols - InfoOf(c).Cols))
                    {
                        if (left < DeviceCell.CellWidth) break;
                        InfoOf(c).Cols++;
                        left -= DeviceCell.CellWidth;
                        grew = true;
                    }
                }
            }
            // What a full row still has left (less than a column) is shared by its cards' frames, so the rows end at the right edge.
            for (int r = 0; r < rows.Count; r++)
            {
                double left = width - rows[r].Sum(c => InfoOf(c) is { } i ? i.Width(i.Cols) : c.DesiredSize.Width);
                bool full = r < rows.Count - 1 && left > 0 && rows[r].All(c => InfoOf(c) != null);
                foreach (var c in rows[r])
                    if (InfoOf(c) is { } info) info.Extra = full ? left / rows[r].Count : 0;
            }
            double height = 0, widest = 0;
            var spans = new List<(int, int)>();
            int at = 0;
            foreach (var r in rows)
            {
                double rowH = 0, rowW = 0;
                foreach (var c in r)
                {
                    if (InfoOf(c) is { } info)
                    {
                        info.SetCols(info.Cols);
                        c.Width = info.Extra > 0.5 ? info.Width(info.Cols) + info.Extra - 8 : double.NaN;     // 8: the card's margin
                    }
                    c.Measure(Size.Infinity);
                    rowH = Math.Max(rowH, c.DesiredSize.Height);
                    rowW += c.DesiredSize.Width;
                }
                height += rowH;
                widest = Math.Max(widest, rowW);
                spans.Add((at, r.Count));
                at += r.Count;
            }
            Rows = spans;
            return new Size(Math.Min(widest, width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var kids = Children.OfType<Control>().Where(c => c.IsVisible).ToList();
            double y = 0;
            foreach (var (first, count) in Rows)
            {
                double x = 0, rowH = 0;
                for (int i = first; i < first + count && i < kids.Count; i++)
                {
                    var c = kids[i];
                    c.Arrange(new Rect(x, y, c.DesiredSize.Width, c.DesiredSize.Height));
                    x += c.DesiredSize.Width;
                    rowH = Math.Max(rowH, c.DesiredSize.Height);
                }
                y += rowH;
            }
            return finalSize;
        }
    }
}
