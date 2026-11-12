// Fork (docs/specs/studio-mvp.md S1): pages for an editor whose controls don't fit one screen with
// the machine keyboard under them (Dye Essence, docs/specs/dye-essence.md §8), on Avalonia, ported
// from the WPF kit's Pages.cs with the same API: tabs (Core/PageStrip.cs) over the page shown; every
// page has the size the editor gives, so the window keeps its size from page to page; the page shown
// last comes back when the editor opens again. Each tab is named for screen readers ("Page: …").

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace NeoBuzz.Editor
{
    /// <summary>An editor's pages: named tabs over one page at a time, each page the same size.</summary>
    internal sealed class Pages : DockPanel
    {
        static readonly Dictionary<string, int> remembered = new Dictionary<string, int>();
        readonly List<string> names = new List<string>();
        readonly List<Control> pages = new List<Control>();
        readonly Grid host;
        readonly PageTabs tabs;
        readonly string key;
        int selected = -1, wanted = -1;

        /// <param name="width">A page's width.</param>
        /// <param name="height">A page's height.</param>
        /// <param name="key">Remembers the page shown, across editors of this key (the machine's type); null: not remembered.</param>
        public Pages(NeoColor accent, double width, double height, string key = null)
        {
            this.key = key;
            if (key != null && remembered.TryGetValue(key, out int last)) wanted = last;
            LastChildFill = true;
            tabs = new PageTabs(this, accent) { Width = width, Margin = new Thickness(4, 0, 4, 4) };
            SetDock(tabs, Dock.Top);
            Children.Add(tabs);
            host = new Grid
            {
                Width = width,
                Height = height,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                ClipToBounds = true,
            };
            Children.Add(host);
            AutomationProperties.SetName(this, "Pages");
        }

        public IReadOnlyList<string> Names => names;

        /// <summary>The pages' size.</summary>
        public Size PageSize => new Size(host.Width, host.Height);

        public Control Page(int i) => pages[i];

        /// <summary>Raised after another page is shown.</summary>
        public event Action<int> SelectedChanged;

        /// <summary>The page shown; setting it shows that page.</summary>
        public int Selected
        {
            get => selected;
            set
            {
                if (value < 0 || value >= pages.Count || value == selected) return;
                selected = value;
                for (int i = 0; i < pages.Count; i++) pages[i].IsVisible = i == selected;
                if (key != null) remembered[key] = selected;
                tabs.InvalidateVisual();
                SelectedChanged?.Invoke(selected);
            }
        }

        /// <summary>Adds a page (shown when it is the first, or the one shown last in an editor of this key).</summary>
        public void Add(string name, Control content)
        {
            names.Add(name);
            pages.Add(content);
            content.IsVisible = false;
            content.HorizontalAlignment = HorizontalAlignment.Left;
            content.VerticalAlignment = VerticalAlignment.Top;
            AutomationProperties.SetName(content, "Page: " + name);
            host.Children.Add(content);
            tabs.Rebuild();
            int index = pages.Count - 1;
            if (selected < 0 || wanted == index) Selected = index;
        }

        sealed class PageTabs : NeoElement
        {
            readonly Pages owner;
            readonly NeoColor accent;
            readonly AvaloniaCanvas canvas;
            PageStrip strip;
            int hover = -1;

            public PageTabs(Pages owner, NeoColor accent)
            {
                this.owner = owner;
                this.accent = accent;
                canvas = new AvaloniaCanvas(this);
                Height = PageStrip.Height;
                Cursor = Cursors.Hand;
                ToolTip = "Pages";
                AutomationProperties.SetName(this, "Page tabs");
                strip = new PageStrip(Array.Empty<string>(), canvas);
                Rebuild();
            }

            public void Rebuild()
            {
                strip = new PageStrip(owner.names.ToArray(), canvas);
                InvalidateVisual();
            }

            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.TabAt(p.X, p.Y);
                if (i >= 0) owner.Selected = i;
                e.Handled = true;
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                var p = e.GetPosition(this);
                int i = strip.TabAt(p.X, p.Y);
                if (i == hover) return;
                hover = i;
                InvalidateVisual();
            }

            protected override void OnMouseLeave(MouseEventArgs e)
            {
                hover = -1;
                InvalidateVisual();
            }

            protected override void OnRender(DrawingContext dc) =>
                strip.Draw(canvas.On(dc), Bounds.Width > 0 ? Bounds.Width : Width, owner.selected, hover, accent);
        }
    }
}
