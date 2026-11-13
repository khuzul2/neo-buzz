// Fork (docs/specs/machine-safety.md §3.5, studio-mvp.md): the Song Notes tab. The song's notes,
// rich text written as Markdown (ReBuzz/Core/Notes/NotesMarkdown.cs): an editor on the left with a
// toolbar and the formatting keys (bold, italic, underline, headings, lists, links), the notes as
// they read on the right. Saved with the song (SongNotesStore, a "SongNotes" subsection); every edit
// is a step on the song's undo history (a run of typing is one) and marks the song modified. Info
// machines' text and the song's info text come in when a song opens, marked as imported; the Info
// machine stays in the song as it was. F10 shows the tab, as Buzz's Info view.

using ReBuzz.Studio.Shell.Design;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;
using ReBuzz.Core.Notes;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Notes
{
    using Control = Avalonia.Controls.Control;
    using Block = NotesMarkdown.Block;
    using BlockKind = NotesMarkdown.BlockKind;

    /// <summary>The "Song Notes" tab.</summary>
    internal sealed class SongNotesTab : IStudioView
    {
        public const string TabTitle = "Song Notes";

        public string Title => TabTitle;

        public Control Create(StudioHost host) => new SongNotesView(host.Core);
    }

    /// <summary>
    /// A text box whose edits are steps on the song's undo history (not its own): the shell's Undo
    /// and Redo go to the song for it, as for the views, while Cut, Copy and Paste stay the box's.
    /// </summary>
    internal sealed class SongHistoryTextBox : TextBox
    {
        protected override Type StyleKeyOverride => typeof(TextBox);
    }

    internal sealed class SongNotesView : DockPanel, IStudioEditTarget
    {
        static readonly IBrush PageBrush = StudioTokens.SurfaceViewBrush;
        static readonly IBrush BarBrush = StudioTokens.SurfacePanelBrush;
        static readonly IBrush TextBrush = StudioTokens.TextPrimaryBrush;
        static readonly IBrush DimBrush = StudioTokens.TextSecondaryBrush;
        static readonly IBrush LinkBrush = StudioTokens.Brush(StudioTokens.Link);
        static readonly IBrush CodeBrush = StudioTokens.SurfaceControlBrush;
        static readonly IBrush BannerBrush = StudioTokens.Brush(StudioTokens.BannerBack);
        static Cursor hand;
        static Cursor Hand => hand ??= new Cursor(StandardCursorType.Hand);     // made once the platform runs
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Menlo, Consolas, DejaVu Sans Mono, monospace");

        /// <summary>Opens a link from the notes (tests replace it); true when it was handled.</summary>
        internal static Func<Uri, bool> OpenLink;

        readonly ReBuzzCore core;
        readonly SongNotesStore store;
        readonly StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, Margin = new Thickness(6, 3) };
        readonly Border banner = new Border { Background = BannerBrush, Padding = new Thickness(10, 6), IsVisible = false };
        readonly TextBlock bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
        readonly Button dismiss = new Button { Content = "Dismiss", Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly StackPanel page = new StackPanel { Margin = new Thickness(18, 14, 18, 24) };
        readonly ScrollViewer pageScroll = new ScrollViewer { HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        readonly TextBlock emptyPage = new TextBlock { Text = "The notes appear here as they read.", Foreground = DimBrush, FontStyle = FontStyle.Italic };
        readonly List<(SelectableTextBlock Block, List<(int Start, int Length, string Url)> Links)> linkMap = new();
        bool syncing;
        bool attached;

        public SongNotesView(ReBuzzCore core)
        {
            this.core = core;
            store = core.SongNotes;
            Background = PageBrush;

            Editor = new SongHistoryTextBox
            {
                AcceptsReturn = true,
                AcceptsTab = false,
                NewLine = "\n",              // the notes' line ends on every platform
                TextWrapping = TextWrapping.Wrap,
                IsUndoEnabled = false,      // the song's history has the steps
                PlaceholderText = "The song's notes. **bold**, *italic*, # heading, - list, 1. numbered, [link](https://…)",
                FontSize = StudioTokens.FontBody,
                Padding = new Thickness(10, 8),
                BorderThickness = new Thickness(0),
                Background = PageBrush,
                Foreground = TextBrush,
                VerticalAlignment = VerticalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Top,
            };
            ScrollViewer.SetVerticalScrollBarVisibility(Editor, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
            // The page's colour while typing too (Fluent turns a focused box black), with a thin edge.
            foreach (var key in new[] { "TextControlBackgroundFocused", "TextControlBackgroundPointerOver" })
                Editor.Resources[key] = PageBrush;
            Editor.Resources["TextControlForegroundFocused"] = TextBrush;
            Editor.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(0);

            // The toolbar: the formats, and where the notes are kept.
            // Fork (ux-overhaul.md §4): the keys as the platform writes them (⌘B on a Mac, Ctrl+B elsewhere).
            Tool("B", $"Bold ({KeyName("Cmd+B")})", Bold, b => b.FontWeight = FontWeight.Bold);
            Tool("I", $"Italic ({KeyName("Cmd+I")})", Italic, b => b.FontStyle = FontStyle.Italic);
            Tool("U", $"Underline ({KeyName("Cmd+U")})", Underline, b => b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline });
            Tool("S", "Strike through", Strike, b => b.Content = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough });
            bar.Children.Add(new Border { Width = 10 });
            Tool("H", "Heading: a larger line; again for a smaller heading, a fourth time back to text", Heading, b => b.FontWeight = FontWeight.Bold);
            Tool("•", "Bulleted list", Bullets);
            Tool("1.", "Numbered list", Numbers);
            bar.Children.Add(new Border { Width = 10 });
            Tool("Link", $"Link ({KeyName("Cmd+K")}): the selected text links to an address", Link);
            bar.Children.Add(new TextBlock
            {
                Text = "Saved with the song · Markdown",
                Foreground = Shell.Design.StudioTokens.TextSecondaryBrush,      // 5.7:1 on the bar (was 4.1:1)
                FontSize = 11,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });
            var top = new Border { Background = BarBrush, Child = bar };
            SetDock(top, Dock.Top);
            Children.Add(top);

            var bannerRow = new DockPanel();
            DockPanel.SetDock(dismiss, Dock.Right);
            bannerRow.Children.Add(dismiss);
            bannerRow.Children.Add(bannerText);
            banner.Child = bannerRow;
            SetDock(banner, Dock.Top);
            Children.Add(banner);
            dismiss.Click += (s, e) => store.DismissImport();

            // The editor and the notes as they read, side by side.
            pageScroll.Content = page;
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,5,*") };
            var splitter = new GridSplitter { Background = BarBrush, ResizeDirection = GridResizeDirection.Columns };
            grid.Children.Add(Editor);
            grid.Children.Add(splitter);
            grid.Children.Add(pageScroll);
            Children.Add(grid);
            // Fork (ux-overhaul.md decision 38): in the inspector's width the two columns are 90 pt each, so below
            // StackBelow pt the editor sits over the notes as they read, with a horizontal splitter between.
            void Layout(bool stacked)
            {
                grid.ColumnDefinitions = new ColumnDefinitions(stacked ? "*" : "*,5,*");
                grid.RowDefinitions = new RowDefinitions(stacked ? "*,5,*" : "*");
                splitter.ResizeDirection = stacked ? GridResizeDirection.Rows : GridResizeDirection.Columns;
                splitter.Width = stacked ? double.NaN : 5;
                splitter.Height = stacked ? 5 : double.NaN;
                splitter.HorizontalAlignment = HorizontalAlignment.Stretch;
                splitter.VerticalAlignment = VerticalAlignment.Stretch;
                Grid.SetRow(Editor, 0);
                Grid.SetColumn(Editor, 0);
                Grid.SetRow(splitter, stacked ? 1 : 0);
                Grid.SetColumn(splitter, stacked ? 0 : 1);
                Grid.SetRow(pageScroll, stacked ? 2 : 0);
                Grid.SetColumn(pageScroll, stacked ? 0 : 2);
                Stacked = stacked;
            }
            Layout(false);
            grid.SizeChanged += (s, e) =>
            {
                bool want = e.NewSize.Width > 0 && e.NewSize.Width < StackBelow;
                if (want != Stacked) Layout(want);
            };

            Editor.TextChanged += (s, e) =>
            {
                if (syncing) return;
                store.Type(Editor.Text ?? "");
            };
            Editor.LostFocus += (s, e) => store.EndTyping();
            AddHandler(KeyDownEvent, OnFormatKey, RoutingStrategies.Tunnel);

            // It follows the notes while it is shown, and syncs when shown again: a view whose tab is never
            // shown is never told it is gone, so it doesn't listen until then.
            AttachedToVisualTree += (s, e) => Attach();
            DetachedFromVisualTree += (s, e) => Detach();
            Sync();
        }

        /// <summary>The width under which the editor and the notes as they read are stacked, not side by side.</summary>
        internal const double StackBelow = 520;

        /// <summary>Whether the editor is over the notes (a narrow view) and not beside them.</summary>
        internal bool Stacked { get; private set; }

        /// <summary>The Markdown editor.</summary>
        internal SongHistoryTextBox Editor { get; }

        /// <summary>The notes as they read (one control per paragraph, heading, item…).</summary>
        internal StackPanel Page => page;

        /// <summary>The imported mark (visible while there is one).</summary>
        internal Border Banner => banner;

        internal string BannerText => bannerText.Text;

        internal Button DismissButton => dismiss;

        static string KeyName(string keys) => Shell.Commands.KeyText.Of(keys);

        void Tool(string text, string tip, Action run, Action<Button> look = null)
        {
            var b = StudioButtons.KeepFocus(new Button     // the editor keeps the keyboard and its selection
            {
                Content = text,
                MinWidth = StudioStyles.ControlHeight,
                Tag = text,
            });
            look?.Invoke(b);
            ToolTip.SetTip(b, tip);
            // A screen reader hears the tool's words ("Bold"), not its letter: the tip's first words, before its key or its explanation.
            int cut = tip.IndexOfAny(new[] { '(', ':', ',' });
            Avalonia.Automation.AutomationProperties.SetName(b, (cut > 0 ? tip.Substring(0, cut) : tip).Trim());
            b.Click += (s, e) =>
            {
                run();
                Editor.Focus();
            };
            bar.Children.Add(b);
        }

        /// <summary>A toolbar button by its label ("B", "I", "U", "S", "H", "•", "1.", "Link"), for tests.</summary>
        internal Button ToolButton(string label) => bar.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == label);

        void Attach()
        {
            if (attached) return;
            attached = true;
            store.Changed += OnStoreChanged;
            Sync();
        }

        void Detach()
        {
            if (!attached) return;
            attached = false;
            store.Changed -= OnStoreChanged;
            store.EndTyping();
        }

        void OnStoreChanged()
        {
            if (Dispatcher.UIThread.CheckAccess()) Sync();
            else Dispatcher.UIThread.Post(Sync);
        }

        /// <summary>The editor, the page and the imported mark as the notes now are.</summary>
        void Sync()
        {
            string text = store.Text;
            if ((Editor.Text ?? "") != text)
            {
                syncing = true;
                int caret = Math.Min(Editor.CaretIndex, text.Length);
                Editor.Text = text;
                Editor.CaretIndex = caret;
                syncing = false;
            }
            Render(text);
            var imported = store.JustImported;
            banner.IsVisible = imported.Count > 0;
            bannerText.Text = imported.Count == 0 ? "" :
                "Imported when the song opened: " + string.Join(", ", imported.Select(Describe)) +
                ". " + (imported.Any(s => s.Kind == SongNotesStore.Source.InfoMachine)
                    ? "The Info machine stays in the song as it was; the notes are kept here from now on."
                    : "The song's info text stays as it was; the notes are kept here from now on.");
        }

        static string Describe(SongNotesStore.Source s) =>
            s.Kind == SongNotesStore.Source.InfoMachine ? $"the text of the Info machine “{s.Machine}”" : "the song's info text";

        // ------------------------------------------------------------------ the page

        void Render(string markdown)
        {
            page.Children.Clear();
            linkMap.Clear();
            var blocks = NotesMarkdown.Parse(markdown);
            if (blocks.Count == 0)
            {
                page.Children.Add(emptyPage);
                return;
            }
            Block previous = null;
            foreach (var b in blocks)
            {
                var control = RenderBlock(b);
                double gap = previous == null ? 0 : b.Kind == BlockKind.Heading ? 14 : IsItem(b) && IsItem(previous) ? 2 : 9;
                control.Margin = new Thickness(control.Margin.Left, gap, 0, 0);
                page.Children.Add(control);
                previous = b;
            }
        }

        static bool IsItem(Block b) => b.Kind == BlockKind.Bullet || b.Kind == BlockKind.Numbered;

        Control RenderBlock(Block b)
        {
            switch (b.Kind)
            {
                case BlockKind.Heading:
                {
                    var t = Text(b);
                    t.FontSize = b.Level switch { 1 => StudioTokens.FontHeading, 2 => StudioTokens.FontTitle, _ => StudioTokens.FontBody };
                    t.FontWeight = FontWeight.Bold;
                    t.Foreground = StudioTokens.White;
                    return t;
                }
                case BlockKind.Bullet:
                case BlockKind.Numbered:
                {
                    string marker = b.Kind == BlockKind.Bullet ? (b.Level % 3 == 0 ? "•" : b.Level % 3 == 1 ? "◦" : "▪") : b.Number + ".";
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), Margin = new Thickness(22 * b.Level, 0, 0, 0) };
                    var m = new TextBlock { Text = marker, Foreground = TextBrush, FontSize = StudioTokens.FontBody, HorizontalAlignment = HorizontalAlignment.Left };
                    var t = Text(b);
                    Grid.SetColumn(t, 1);
                    row.Children.Add(m);
                    row.Children.Add(t);
                    return row;
                }
                case BlockKind.Quote:
                {
                    var t = Text(b);
                    t.Foreground = DimBrush;
                    return new Border { BorderBrush = DimBrush, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(10, 2, 0, 2), Child = t };
                }
                case BlockKind.Rule:
                    return new Border { Height = 1, Background = DimBrush, Margin = new Thickness(0, 4) };
                default:
                    return Text(b);
            }
        }

        SelectableTextBlock Text(Block b)
        {
            var t = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, FontSize = StudioTokens.FontBody, Inlines = new InlineCollection() };
            var links = new List<(int, int, string)>();
            int at = 0;
            foreach (var s in b.Spans)
            {
                // Line breaks as "\n" in a run, so a character's place in the text is its place in the runs.
                var run = new Run(s.LineBreak ? "\n" : s.Text);
                if (!s.LineBreak)
                {
                    if (s.Bold) run.FontWeight = FontWeight.Bold;
                    if (s.Italic) run.FontStyle = FontStyle.Italic;
                    var decorations = new TextDecorationCollection();
                    if (s.Underline || s.Link != null) foreach (var d in TextDecorations.Underline) decorations.Add(d);
                    if (s.Strike) foreach (var d in TextDecorations.Strikethrough) decorations.Add(d);
                    if (decorations.Count > 0) run.TextDecorations = decorations;
                    if (s.Code)
                    {
                        run.FontFamily = Mono;
                        run.Background = CodeBrush;
                    }
                    if (s.Link != null)
                    {
                        run.Foreground = LinkBrush;
                        links.Add((at, run.Text.Length, s.Link));
                    }
                    else if (s.Color is uint c) run.Foreground = new SolidColorBrush(Readable(c));
                }
                t.Inlines.Add(run);
                at += run.Text.Length;
            }
            if (links.Count > 0)
            {
                linkMap.Add((t, links));
                // A click on a link opens it: pressed and let go on the same link, without dragging
                // (which selects text, as anywhere on the page).
                string pressed = null;
                Point pressedAt = default;
                t.PointerMoved += (s, e) => t.Cursor = LinkAt(t, links, e.GetPosition(t)) != null ? Hand : Cursor.Default;
                t.AddHandler(PointerPressedEvent, (s, e) =>
                {
                    pressedAt = e.GetPosition(t);
                    pressed = e.GetCurrentPoint(t).Properties.IsLeftButtonPressed ? LinkAt(t, links, pressedAt) : null;
                }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                t.AddHandler(PointerReleasedEvent, (s, e) =>
                {
                    var at = e.GetPosition(t);
                    string url = pressed;
                    pressed = null;
                    if (url == null || e.InitialPressMouseButton != MouseButton.Left) return;
                    if (Math.Abs(at.X - pressedAt.X) > 4 || Math.Abs(at.Y - pressedAt.Y) > 4 || LinkAt(t, links, at) != url) return;
                    Open(url);
                }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            }
            return t;
        }

        /// <summary>The link drawn under <paramref name="p"/> (the text block's coordinates), or null.</summary>
        static string LinkAt(SelectableTextBlock t, List<(int Start, int Length, string Url)> links, Point p)
        {
            foreach (var (start, length, url) in links)
                if (t.TextLayout.HitTestTextRange(start, length).Any(r => r.Contains(p))) return url;
            return null;
        }

        /// <summary>Where each link is drawn, in the page's coordinates (tests click them).</summary>
        internal IEnumerable<(string Url, Rect Bounds)> LinkBounds()
        {
            foreach (var (block, links) in linkMap)
                foreach (var (start, length, url) in links)
                {
                    var rects = block.TextLayout.HitTestTextRange(start, length).ToList();
                    if (rects.Count == 0) continue;
                    var r = rects[0];
                    var origin = block.TranslatePoint(new Point(r.X, r.Y), this);
                    if (origin is Point o) yield return (url, new Rect(o, r.Size));
                }
        }

        void Open(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) && !Uri.TryCreate("https://" + url, UriKind.Absolute, out uri)) return;
            if (OpenLink?.Invoke(uri) == true) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto) return;
            _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
        }

        /// <summary>A colour from the notes made light enough to read on the dark page.</summary>
        static Color Readable(uint argb)
        {
            var c = Color.FromUInt32(argb | 0xFF000000);
            double luminance = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255;
            if (luminance >= 0.35) return c;
            double k = (0.35 - luminance) / (1 - luminance);
            byte Mix(byte v) => (byte)Math.Round(v + (255 - v) * k);
            return Color.FromRgb(Mix(c.R), Mix(c.G), Mix(c.B));
        }

        // ------------------------------------------------------------------ formatting

        void OnFormatKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.KeyModifiers != MainWindow.CommandModifiers) return;
            Action run = e.Key switch
            {
                Key.B => Bold,
                Key.I => Italic,
                Key.U => Underline,
                Key.K => Link,
                _ => null,
            };
            if (run == null) return;
            run();
            e.Handled = true;
        }

        (string Text, int From, int To) Selection()
        {
            string t = Editor.Text ?? "";
            int a = Math.Clamp(Math.Min(Editor.SelectionStart, Editor.SelectionEnd), 0, t.Length);
            int b = Math.Clamp(Math.Max(Editor.SelectionStart, Editor.SelectionEnd), 0, t.Length);
            return (t, a, b);
        }

        /// <summary>The new text as one step on the song's history, with this selection.</summary>
        void Apply(string text, int from, int to)
        {
            store.EndTyping();
            store.Write(text);
            syncing = true;
            if ((Editor.Text ?? "") != text) Editor.Text = text;
            syncing = false;
            Editor.SelectionStart = from;
            Editor.SelectionEnd = to;
            Editor.CaretIndex = to;
            Editor.SelectionStart = from;
            Editor.SelectionEnd = to;
            store.EndTyping();
        }

        /// <summary>A formatting command (NotesFormatting, shared with the Windows app) on the selection: one step.</summary>
        void Format(Func<string, int, int, NotesFormatting.Result> format)
        {
            var (t, a, b) = Selection();
            var r = format(t, a, b);
            Apply(r.Text, r.From, r.To);
        }

        internal void Bold() => Format(NotesFormatting.Bold);

        internal void Italic() => Format(NotesFormatting.Italic);

        internal void Strike() => Format(NotesFormatting.Strike);

        internal void Underline() => Format(NotesFormatting.Underline);

        /// <summary>[selection](https://) with the address selected, to type over.</summary>
        internal void Link() => Format(NotesFormatting.Link);

        internal void Heading() => Format(NotesFormatting.Heading);

        internal void Bullets() => Format(NotesFormatting.Bullets);

        internal void Numbers() => Format(NotesFormatting.Numbers);

        // ------------------------------------------------------------------ edit commands

        /// <summary>The song's history undoes the notes (after the typing so far, which is a step already).</summary>
        public bool Undo()
        {
            store.EndTyping();
            return false;
        }

        public bool Redo()
        {
            store.EndTyping();
            return false;
        }

        SelectableTextBlock SelectedPageText => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(page).OfType<SelectableTextBlock>().FirstOrDefault(t => t.SelectionStart != t.SelectionEnd);

        public bool Copy()
        {
            var t = SelectedPageText;
            if (t == null) return false;
            t.Copy();
            return true;
        }
    }

}
