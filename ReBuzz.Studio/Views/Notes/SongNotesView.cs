// Fork (docs/specs/machine-safety.md §3.5, studio-mvp.md): the Song Notes tab. The song's notes,
// rich text written as Markdown (ReBuzz/Core/Notes/NotesMarkdown.cs): an editor on the left with a
// toolbar and the formatting keys (bold, italic, underline, headings, lists, links), the notes as
// they read on the right. Saved with the song (SongNotesStore, a "SongNotes" subsection); every edit
// is a step on the song's undo history (a run of typing is one) and marks the song modified. Info
// machines' text and the song's info text come in when a song opens, marked as imported; the Info
// machine stays in the song as it was. F10 shows the tab, as Buzz's Info view.

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
        static readonly IBrush PageBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x22));
        static readonly IBrush BarBrush = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x2B));
        static readonly IBrush TextBrush = new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xE4));
        static readonly IBrush DimBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8A, 0x8A));
        static readonly IBrush LinkBrush = new SolidColorBrush(Color.FromRgb(0x6C, 0xB4, 0xFF));
        static readonly IBrush CodeBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x34, 0x38));
        static readonly IBrush BannerBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x3A, 0x55));
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
        readonly Button dismiss = new Button { Content = "Dismiss", Padding = new Thickness(10, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
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
                Watermark = "The song's notes. **bold**, *italic*, # heading, - list, 1. numbered, [link](https://…)",
                FontSize = 14,
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
            Tool("B", "Bold (Ctrl/Command+B)", Bold, b => b.FontWeight = FontWeight.Bold);
            Tool("I", "Italic (Ctrl/Command+I)", Italic, b => b.FontStyle = FontStyle.Italic);
            Tool("U", "Underline (Ctrl/Command+U)", Underline, b => b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline });
            Tool("S", "Strike through", Strike, b => b.Content = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough });
            bar.Children.Add(new Border { Width = 10 });
            Tool("H", "Heading: a larger line; again for a smaller heading, a fourth time back to text", Heading, b => b.FontWeight = FontWeight.Bold);
            Tool("•", "Bulleted list", Bullets);
            Tool("1.", "Numbered list", Numbers);
            bar.Children.Add(new Border { Width = 10 });
            Tool("Link", "Link (Ctrl/Command+K): the selected text links to an address", Link);
            bar.Children.Add(new TextBlock
            {
                Text = "Saved with the song · Markdown",
                Foreground = DimBrush,
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
            Grid.SetColumn(Editor, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(pageScroll, 2);
            grid.Children.Add(Editor);
            grid.Children.Add(splitter);
            grid.Children.Add(pageScroll);
            Children.Add(grid);

            Editor.TextChanged += (s, e) =>
            {
                if (syncing) return;
                store.Type(Editor.Text ?? "");
            };
            Editor.LostFocus += (s, e) => store.EndTyping();
            AddHandler(KeyDownEvent, OnFormatKey, RoutingStrategies.Tunnel);

            AttachedToVisualTree += (s, e) => Attach();
            DetachedFromVisualTree += (s, e) => Detach();
            Attach();
        }

        /// <summary>The Markdown editor.</summary>
        internal SongHistoryTextBox Editor { get; }

        /// <summary>The notes as they read (one control per paragraph, heading, item…).</summary>
        internal StackPanel Page => page;

        /// <summary>The imported mark (visible while there is one).</summary>
        internal Border Banner => banner;

        internal string BannerText => bannerText.Text;

        internal Button DismissButton => dismiss;

        void Tool(string text, string tip, Action run, Action<Button> look = null)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 28,
                Height = 26,
                MinHeight = 0,
                Padding = new Thickness(7, 0),
                FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,      // the editor keeps the keyboard and its selection
                Tag = text,
            };
            look?.Invoke(b);
            ToolTip.SetTip(b, tip);
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
                    t.FontSize = b.Level switch { 1 => 24, 2 => 19, _ => 16 };
                    t.FontWeight = FontWeight.Bold;
                    t.Foreground = Brushes.White;
                    return t;
                }
                case BlockKind.Bullet:
                case BlockKind.Numbered:
                {
                    string marker = b.Kind == BlockKind.Bullet ? (b.Level % 3 == 0 ? "•" : b.Level % 3 == 1 ? "◦" : "▪") : b.Number + ".";
                    var row = new Grid { ColumnDefinitions = new ColumnDefinitions("26,*"), Margin = new Thickness(22 * b.Level, 0, 0, 0) };
                    var m = new TextBlock { Text = marker, Foreground = TextBrush, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Left };
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
            var t = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, FontSize = 14, Inlines = new InlineCollection() };
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

        internal void Bold() => Emphasis(2);

        internal void Italic() => Emphasis(1);

        internal void Strike() => Wrap("~~", "~~");

        internal void Underline() => Wrap("<u>", "</u>");

        /// <summary>The word at the caret when nothing is selected.</summary>
        static (int From, int To) Word(string t, int a, int b)
        {
            if (a != b) return (a, b);
            int from = a, to = a;
            while (from > 0 && char.IsLetterOrDigit(t[from - 1])) from--;
            while (to < t.Length && char.IsLetterOrDigit(t[to])) to++;
            return (from, to);
        }

        /// <summary>Stars around the selection: <paramref name="stars"/> 2 for bold, 1 for italic; again takes them off.</summary>
        void Emphasis(int stars)
        {
            var (t, a, b) = Selection();
            (a, b) = Word(t, a, b);
            int before = 0, after = 0;
            while (a - before > 0 && t[a - before - 1] == '*') before++;
            while (b + after < t.Length && t[b + after] == '*') after++;
            int inside = 0;
            while (inside < (b - a) / 2 && t[a + inside] == '*' && t[b - 1 - inside] == '*') inside++;
            bool has(int n) => stars == 1 ? n % 2 == 1 : n >= 2;
            if (has(before) && has(after))
            {
                Apply(t.Remove(b, stars).Remove(a - stars, stars), a - stars, b - stars);
                return;
            }
            if (has(inside) && b - a > 2 * stars)
            {
                Apply(t.Remove(b - stars, stars).Remove(a, stars), a, b - 2 * stars);
                return;
            }
            string mark = new string('*', stars);
            Apply(t.Insert(b, mark).Insert(a, mark), a + stars, b + stars);
        }

        void Wrap(string open, string close)
        {
            var (t, a, b) = Selection();
            (a, b) = Word(t, a, b);
            if (a >= open.Length && b + close.Length <= t.Length && t.Substring(a - open.Length, open.Length) == open && t.Substring(b, close.Length) == close)
            {
                Apply(t.Remove(b, close.Length).Remove(a - open.Length, open.Length), a - open.Length, b - open.Length);
                return;
            }
            if (b - a >= open.Length + close.Length && t.Substring(a, open.Length) == open && t.Substring(b - close.Length, close.Length) == close)
            {
                Apply(t.Remove(b - close.Length, close.Length).Remove(a, open.Length), a, b - open.Length - close.Length);
                return;
            }
            Apply(t.Insert(b, close).Insert(a, open), a + open.Length, b + open.Length);
        }

        /// <summary>[selection](https://) with the address selected, to type over.</summary>
        internal void Link()
        {
            var (t, a, b) = Selection();
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
            Apply(t.Substring(0, a) + link + t.Substring(b), urlAt, urlAt + url.Length);
        }

        static readonly Regex Marker = new(@"^(\s*)(#{1,6}\s+|[-*+]\s+|\d{1,9}[.)]\s+)?", RegexOptions.Compiled);

        /// <summary>Changes the start of each line the selection touches.</summary>
        void Lines(Func<IReadOnlyList<string>, IReadOnlyList<string>> change)
        {
            var (t, a, b) = Selection();
            int from = t.LastIndexOf('\n', Math.Max(0, a - 1)) + 1;
            if (a == 0) from = 0;
            int end = b > a && b > 0 && t[b - 1] == '\n' ? b - 1 : b;
            int to = t.IndexOf('\n', end);
            if (to < 0) to = t.Length;
            var lines = t.Substring(from, to - from).Split('\n');
            var changed = change(lines);
            string joined = string.Join("\n", changed);
            Apply(t.Substring(0, from) + joined + t.Substring(to), from, from + joined.Length);
        }

        internal void Heading()
        {
            Lines(lines =>
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
        }

        internal void Bullets() => Lines(lines =>
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

        internal void Numbers() => Lines(lines =>
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
