using ReBuzz.Core;
using ReBuzz.Core.Notes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/machine-safety.md §3.5): Song Notes, the Windows app's F10 view, as the
    /// studio's Song Notes tab (ReBuzz.Studio/Views/Notes/SongNotesView.cs): the song's notes, rich
    /// text written as Markdown (NotesMarkdown), an editor on the left with a toolbar and the
    /// formatting keys (NotesFormatting, shared), the notes as they read on the right. Saved with the
    /// song (SongNotesStore, a "SongNotes" subsection); every edit is a step on the song's undo history
    /// (a run of typing is one). Buzz's info text, and Info machines' text, come in when a song opens,
    /// marked as imported. It replaces Buzz's Info view, which showed only the old info text, so notes
    /// written by the Mac app or an agent (song_notes_write) looked lost there (gallery win-060).
    /// </summary>
    internal sealed class SongNotesView : UserControl
    {
        static readonly Brush PageBrush = Frozen(Color.FromRgb(0x1E, 0x1F, 0x22));
        static readonly Brush BarBrush = Frozen(Color.FromRgb(0x2B, 0x2B, 0x2B));
        static readonly Brush TextBrush = Frozen(Color.FromRgb(0xE4, 0xE4, 0xE4));
        static readonly Brush DimBrush = Frozen(Color.FromRgb(0x8A, 0x8A, 0x8A));
        static readonly Brush LinkBrush = Frozen(Color.FromRgb(0x6C, 0xB4, 0xFF));
        static readonly Brush CodeBrush = Frozen(Color.FromRgb(0x33, 0x34, 0x38));
        static readonly Brush BannerBrush = Frozen(Color.FromRgb(0x23, 0x3A, 0x55));
        static readonly FontFamily Mono = new FontFamily("Cascadia Mono, Consolas");

        static Brush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>Opens a link from the notes (tests replace it); true when it was handled.</summary>
        internal static Func<Uri, bool> OpenLink;

        readonly ReBuzzCore core;
        readonly SongNotesStore store;
        readonly StackPanel bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 3, 6, 3) };
        readonly Border banner = new Border { Background = BannerBrush, Padding = new Thickness(10, 6, 10, 6), Visibility = Visibility.Collapsed };
        readonly TextBlock bannerText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = TextBrush, VerticalAlignment = VerticalAlignment.Center };
        readonly Button dismiss = new Button { Content = "Dismiss", Padding = new Thickness(10, 2, 10, 2), Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        readonly FlowDocumentScrollViewer page = new FlowDocumentScrollViewer
        {
            IsToolBarVisible = false,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Background = PageBrush,
            Focusable = true,
        };
        bool syncing;
        bool listening;

        public SongNotesView(ReBuzzCore core)
        {
            this.core = core;
            store = core.SongNotes;
            Background = PageBrush;
            Focusable = false;

            Editor = new TextBox
            {
                AcceptsReturn = true,
                AcceptsTab = false,
                TextWrapping = TextWrapping.Wrap,
                IsUndoEnabled = false,      // the song's history has the steps
                FontSize = 14,
                Padding = new Thickness(10, 8, 10, 8),
                BorderThickness = new Thickness(0),
                Background = PageBrush,
                Foreground = TextBrush,
                CaretBrush = TextBrush,
                SelectionBrush = LinkBrush,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalContentAlignment = VerticalAlignment.Top,
                ToolTip = "The song's notes. **bold**, *italic*, # heading, - list, 1. numbered, [link](https://…)",
            };

            // The toolbar: the formats, and where the notes are kept.
            Tool("B", "Bold (Ctrl+B)", Bold, b => b.FontWeight = FontWeights.Bold);
            Tool("I", "Italic (Ctrl+I)", Italic, b => b.FontStyle = FontStyles.Italic);
            Tool("U", "Underline (Ctrl+U)", Underline, b => b.Content = new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline });
            Tool("S", "Strike through", Strike, b => b.Content = new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough });
            bar.Children.Add(new Border { Width = 10 });
            Tool("H", "Heading: a larger line; again for a smaller heading, a fourth time back to text", Heading, b => b.FontWeight = FontWeights.Bold);
            Tool("•", "Bulleted list", Bullets);
            Tool("1.", "Numbered list", Numbers);
            bar.Children.Add(new Border { Width = 10 });
            Tool("Link", "Link (Ctrl+K): the selected text links to an address", Link);
            bar.Children.Add(new TextBlock
            {
                Text = "Saved with the song · Markdown",
                Foreground = DimBrush,
                FontSize = 11,
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
            });

            var root = new DockPanel { Background = PageBrush };
            var top = new Border { Background = BarBrush, Child = bar };
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);

            var bannerRow = new DockPanel();
            DockPanel.SetDock(dismiss, Dock.Right);
            bannerRow.Children.Add(dismiss);
            bannerRow.Children.Add(bannerText);
            banner.Child = bannerRow;
            DockPanel.SetDock(banner, Dock.Top);
            root.Children.Add(banner);
            dismiss.Click += (s, e) => store.DismissImport();

            // The editor and the notes as they read, side by side.
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var splitter = new GridSplitter { Background = BarBrush, HorizontalAlignment = HorizontalAlignment.Stretch, ResizeDirection = GridResizeDirection.Columns };
            Grid.SetColumn(Editor, 0);
            Grid.SetColumn(splitter, 1);
            Grid.SetColumn(page, 2);
            grid.Children.Add(Editor);
            grid.Children.Add(splitter);
            grid.Children.Add(page);
            root.Children.Add(grid);
            Content = root;

            Editor.TextChanged += (s, e) => OnTyped();
            Editor.LostKeyboardFocus += (s, e) => store.EndTyping();
            PreviewKeyDown += OnFormatKey;
            // Undo and redo in the editor are the song's (its box keeps none of its own).
            Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo, (s, e) => Undo(), (s, e) => { e.CanExecute = true; e.Handled = true; }));
            Editor.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo, (s, e) => Redo(), (s, e) => { e.CanExecute = true; e.Handled = true; }));

            // It follows the notes while it is loaded, and syncs when loaded again.
            Loaded += (s, e) => Listen(true);
            Unloaded += (s, e) => Listen(false);
            Sync();
        }

        /// <summary>The Markdown editor.</summary>
        internal TextBox Editor { get; }

        /// <summary>The notes as they read.</summary>
        internal FlowDocument Page => page.Document;

        internal Border Banner => banner;

        internal string BannerText => bannerText.Text;

        internal Button DismissButton => dismiss;

        void Tool(string text, string tip, Action run, Action<Button> look = null)
        {
            var b = new Button
            {
                Content = text,
                MinWidth = 28,
                Height = 24,
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(0, 0, 2, 0),
                FontSize = 13,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,      // the editor keeps the keyboard and its selection
                Tag = text,
                ToolTip = tip,
            };
            look?.Invoke(b);
            b.Click += (s, e) =>
            {
                run();
                Editor.Focus();
            };
            bar.Children.Add(b);
        }

        /// <summary>A toolbar button by its label ("B", "I", "U", "S", "H", "•", "1.", "Link"), for tests.</summary>
        internal Button ToolButton(string label) => bar.Children.OfType<Button>().FirstOrDefault(b => (string)b.Tag == label);

        void Listen(bool on)
        {
            if (on == listening) return;
            listening = on;
            if (on)
            {
                store.Changed += OnStoreChanged;
                Sync();
            }
            else
            {
                store.Changed -= OnStoreChanged;
                store.EndTyping();
            }
        }

        void OnStoreChanged()
        {
            if (Dispatcher.CheckAccess()) Sync();
            else Dispatcher.BeginInvoke(new Action(Sync));
        }

        /// <summary>
        /// Typing: the box writes "\r\n" for Enter, the notes keep "\n" (as on every platform), so the
        /// box is kept in "\n" too and its positions are the notes' positions.
        /// </summary>
        void OnTyped()
        {
            if (syncing) return;
            string text = Editor.Text ?? "";
            if (text.Contains('\r'))
            {
                int caret = Editor.CaretIndex;
                int before = CountPairs(text, Math.Min(caret, text.Length));
                string normal = text.Replace("\r\n", "\n").Replace('\r', '\n');
                syncing = true;
                Editor.Text = normal;
                Editor.CaretIndex = Math.Max(0, caret - before);
                syncing = false;
                text = normal;
            }
            store.Type(text);
        }

        /// <summary>The "\r\n" pairs that start before <paramref name="end"/>.</summary>
        static int CountPairs(string text, int end)
        {
            int n = 0;
            for (int i = 0; i < end && i + 1 < text.Length; i++)
                if (text[i] == '\r' && text[i + 1] == '\n') n++;
            return n;
        }

        /// <summary>The editor, the page and the imported mark as the notes now are.</summary>
        internal void Sync()
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
            page.Document = Render(text);
            var imported = store.JustImported;
            banner.Visibility = imported.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            bannerText.Text = imported.Count == 0 ? "" :
                "Imported when the song opened: " + string.Join(", ", imported.Select(Describe)) +
                ". " + (imported.Any(s => s.Kind == SongNotesStore.Source.InfoMachine)
                    ? "The Info machine stays in the song as it was; the notes are kept here from now on."
                    : "The song's info text stays as it was; the notes are kept here from now on.");
        }

        static string Describe(SongNotesStore.Source s) =>
            s.Kind == SongNotesStore.Source.InfoMachine ? $"the text of the Info machine “{s.Machine}”" : "the song's info text";

        // ------------------------------------------------------------------ the page

        /// <summary>The notes as a document: one paragraph per block (heading, item, quote…), as the studio draws them.</summary>
        internal FlowDocument Render(string markdown)
        {
            var doc = new FlowDocument
            {
                Background = PageBrush,
                Foreground = TextBrush,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 14,
                PagePadding = new Thickness(18, 14, 18, 24),
                TextAlignment = TextAlignment.Left,
            };
            var blocks = NotesMarkdown.Parse(markdown);
            if (blocks.Count == 0)
            {
                doc.Blocks.Add(new Paragraph(new Run("The notes appear here as they read.")) { Foreground = DimBrush, FontStyle = FontStyles.Italic });
                return doc;
            }
            NotesMarkdown.Block previous = null;
            foreach (var b in blocks)
            {
                double gap = previous == null ? 0 : b.Kind == NotesMarkdown.BlockKind.Heading ? 14 : IsItem(b) && IsItem(previous) ? 2 : 9;
                var block = RenderBlock(b);
                block.Margin = new Thickness(block.Margin.Left, gap, 0, 0);
                doc.Blocks.Add(block);
                previous = b;
            }
            return doc;
        }

        static bool IsItem(NotesMarkdown.Block b) => b.Kind == NotesMarkdown.BlockKind.Bullet || b.Kind == NotesMarkdown.BlockKind.Numbered;

        Block RenderBlock(NotesMarkdown.Block b)
        {
            switch (b.Kind)
            {
                case NotesMarkdown.BlockKind.Heading:
                {
                    var p = Text(b);
                    p.FontSize = b.Level switch { 1 => 24, 2 => 19, _ => 16 };
                    p.FontWeight = FontWeights.Bold;
                    p.Foreground = Brushes.White;
                    return p;
                }
                case NotesMarkdown.BlockKind.Bullet:
                case NotesMarkdown.BlockKind.Numbered:
                {
                    string marker = b.Kind == NotesMarkdown.BlockKind.Bullet ? (b.Level % 3 == 0 ? "•" : b.Level % 3 == 1 ? "◦" : "▪") : b.Number + ".";
                    var p = Text(b);
                    // The marker hangs in the indent, the text lines up after it.
                    var mark = new Run(marker + "  ");
                    if (p.Inlines.FirstInline == null) p.Inlines.Add(mark);
                    else p.Inlines.InsertBefore(p.Inlines.FirstInline, mark);
                    p.Margin = new Thickness(22 * b.Level + 26, 0, 0, 0);
                    p.TextIndent = -26;
                    return p;
                }
                case NotesMarkdown.BlockKind.Quote:
                {
                    var p = Text(b);
                    p.Foreground = DimBrush;
                    p.BorderBrush = DimBrush;
                    p.BorderThickness = new Thickness(3, 0, 0, 0);
                    p.Padding = new Thickness(10, 2, 0, 2);
                    return p;
                }
                case NotesMarkdown.BlockKind.Rule:
                    return new BlockUIContainer(new Border { Height = 1, Background = DimBrush, Margin = new Thickness(0, 4, 0, 4) });
                default:
                    return Text(b);
            }
        }

        Paragraph Text(NotesMarkdown.Block b)
        {
            var p = new Paragraph { Margin = new Thickness(0) };
            foreach (var s in b.Spans)
            {
                if (s.LineBreak)
                {
                    p.Inlines.Add(new LineBreak());
                    continue;
                }
                var run = new Run(s.Text);
                if (s.Bold) run.FontWeight = FontWeights.Bold;
                if (s.Italic) run.FontStyle = FontStyles.Italic;
                var decorations = new TextDecorationCollection();
                if (s.Underline || s.Link != null) decorations.Add(TextDecorations.Underline);
                if (s.Strike) decorations.Add(TextDecorations.Strikethrough);
                if (decorations.Count > 0) run.TextDecorations = decorations;
                if (s.Code)
                {
                    run.FontFamily = Mono;
                    run.Background = CodeBrush;
                }
                if (s.Link != null)
                {
                    string url = s.Link;
                    var link = new Hyperlink(run) { Foreground = LinkBrush, ToolTip = url, Cursor = Cursors.Hand };
                    link.Click += (sender, e) => Open(url);
                    p.Inlines.Add(link);
                    continue;
                }
                if (s.Color is uint c)
                {
                    uint argb = NotesFormatting.Readable(c);
                    run.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
                }
                p.Inlines.Add(run);
            }
            return p;
        }

        /// <summary>The links of the page, as the reader sees them (tests).</summary>
        internal IEnumerable<Hyperlink> Links() =>
            page.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Hyperlink>());

        internal static void Open(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) && !Uri.TryCreate("https://" + url, UriKind.Absolute, out uri)) return;
            if (OpenLink?.Invoke(uri) == true) return;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeMailto) return;
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (Exception)
            {
                // No browser to open it with: nothing more to do.
            }
        }

        // ------------------------------------------------------------------ formatting

        void OnFormatKey(object sender, KeyEventArgs e)
        {
            if (e.Handled || Keyboard.Modifiers != ModifierKeys.Control || !Editor.IsKeyboardFocusWithin) return;
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

        void Format(Func<string, int, int, NotesFormatting.Result> format)
        {
            string t = Editor.Text ?? "";
            int a = Math.Clamp(Editor.SelectionStart, 0, t.Length);
            int b = Math.Clamp(Editor.SelectionStart + Editor.SelectionLength, 0, t.Length);
            var r = format(t, a, b);
            store.EndTyping();
            store.Write(r.Text);
            syncing = true;
            if ((Editor.Text ?? "") != r.Text) Editor.Text = r.Text;
            syncing = false;
            Editor.Select(r.From, Math.Max(0, r.To - r.From));
            store.EndTyping();
        }

        internal void Bold() => Format(NotesFormatting.Bold);

        internal void Italic() => Format(NotesFormatting.Italic);

        internal void Strike() => Format(NotesFormatting.Strike);

        internal void Underline() => Format(NotesFormatting.Underline);

        internal void Link() => Format(NotesFormatting.Link);

        internal void Heading() => Format(NotesFormatting.Heading);

        internal void Bullets() => Format(NotesFormatting.Bullets);

        internal void Numbers() => Format(NotesFormatting.Numbers);

        // ------------------------------------------------------------------ edit commands

        /// <summary>The song's history undoes the notes (after the typing so far, which is a step already).</summary>
        internal void Undo()
        {
            store.EndTyping();
            var stack = core.SongCore.ActionStack;
            if (stack.CanUndo) stack.Undo();
        }

        internal void Redo()
        {
            store.EndTyping();
            var stack = core.SongCore.ActionStack;
            if (stack.CanRedo) stack.Redo();
        }
    }
}
