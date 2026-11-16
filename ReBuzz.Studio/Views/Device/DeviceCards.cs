// Fork (docs/specs/ux-overhaul.md, the parameter panel, layout D): Knobs mode. A section is a card of knobs (the Neo editors' Section), a
// choice a strip or a toggle in it. Docked, the cards sit in one row that scrolls sideways at the panel's height; floating, they wrap and
// scroll down. The track table is the last card. Cells are made once per parameter and moved between rebuilds (a search keeps typing fast
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

        public DeviceCards(IMachine machine, NeoColor accent)
        {
            this.machine = machine;
            this.accent = accent;
            core = DeviceEdits.CoreOf(machine);
            table = new DeviceList(machine) { Embedded = true };
            table.FocusChanged += p => FocusChanged?.Invoke(p);
            host = new WrapPanel { Orientation = Orientation.Horizontal };
            scroller = new ScrollViewer
            {
                Content = host,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false,
            };
            Child = scroller;
            Avalonia.Automation.AutomationProperties.SetName(this, "Knobs");
        }

        public ScrollViewer Scroller => scroller;

        /// <summary>The cards in view, in order (tests).</summary>
        public IReadOnlyList<(DeviceCard Card, Border Control)> Shown => shown;

        /// <summary>The track table card, or null.</summary>
        public Border TableCard => tableCard;

        public DeviceList Table => table;

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
            Panel next = on ? new StackPanel { Orientation = Orientation.Horizontal } : new WrapPanel { Orientation = Orientation.Horizontal };
            scroller.Content = next;
            host = next;
            foreach (var c in items) next.Children.Add(c);
            scroller.HorizontalScrollBarVisibility = on ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            scroller.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        Panel Host => host;

        /// <summary>Shows these cards, and the track table's columns after them.</summary>
        public void SetCards(IReadOnlyList<DeviceCard> cards, DeviceSection tableSection, List<int> tableColumns)
        {
            foreach (var cell in cells.Values) if (cell.Parent is Panel p) p.Children.Remove(cell);
            Host.Children.Clear();
            shown.Clear();
            foreach (var card in cards)
            {
                var grid = new WrapPanel { Orientation = Orientation.Horizontal, Width = DeviceCard.Columns * DeviceCell.CellWidth };
                foreach (var param in card.Params) grid.Children.Add(CellOf(param));
                var section = new Section(card.Title, accent, grid) { VerticalAlignment = VerticalAlignment.Top };
                Host.Children.Add(section);
                shown.Add((card, section));
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
}
