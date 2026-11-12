// Fork (docs/specs/ux-overhaul.md P3a): the inspector, the main window's panel on the right. It shows what is selected
// (StudioSelection, one for every view): a machine, a pattern or clip, or the song. The Session View's clip panel lives in it
// (the Clip and Scene sections), Song Notes are its Song tab. It collapses to a rail, remembers whether it is open and its
// width, and is the same in every main view.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Notes;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Inspector
{
    internal sealed class InspectorPanel : Border, SessionView.IInspectorLink
    {
        public const string SelectionTab = "Selection";
        public const string SongTab = "Song";
        public const string HistoryTabName = "History";

        // The settings (section "Studio"): whether it is open and its width. The tab is not kept: it opens on the selection.
        internal const string RegistrySection = "Studio";
        internal const string OpenKey = "InspectorOpen";
        internal const string WidthKey = "InspectorWidth";

        public const double DefaultWidth = 288;
        public const double MinPanelWidth = 240;
        public const double MaxPanelWidth = 560;
        public const double RailWidth = 28;

        readonly ReBuzzCore core;
        readonly IRegistryEx registry;
        readonly StudioSelection selection;
        readonly Action<string> showView;
        readonly SessionView session;
        readonly SessionView.ClipPanel clip;
        readonly SongNotesView notes;

        readonly Border grip;
        readonly Button selectionButton, songButton, historyButton, collapseButton, railButton, patternButton, notesButton;
        readonly Grid frame;
        readonly DockPanel full;
        readonly Avalonia.Controls.Control rail;
        readonly DockPanel selectionPage;
        readonly Avalonia.Controls.Control songPage;
        readonly HistoryTab historyPage;
        readonly TextBlock heading, subtitle, hint;
        readonly Grid facts;

        bool open;
        double width;
        string tab;
        string lastFacts;
        bool disposed;

        public InspectorPanel(ReBuzzCore core, IRegistryEx registry, Action<string> showView, SessionView session, SongNotesView notes)
        {
            this.core = core;
            this.registry = registry;
            this.showView = showView;
            this.session = session;
            this.notes = notes;
            selection = StudioSelection.For(core);

            open = Read(OpenKey, true);
            width = Math.Clamp(Read(WidthKey, DefaultWidth), MinPanelWidth, MaxPanelWidth);
            tab = SelectionTab;

            Background = StudioTokens.SurfacePanelBrush;
            BorderBrush = StudioTokens.SurfaceBorderBrush;
            BorderThickness = new Thickness(1, 0, 0, 0);
            ClipToBounds = true;
            AutomationProperties.SetName(this, "Inspector");

            // The header: the two tabs and the collapse button.
            selectionButton = TabButton(SelectionTab);
            songButton = TabButton(SongTab);
            historyButton = TabButton(HistoryTabName);
            collapseButton = IconButton("›", "Hide the inspector", "Hide inspector", Close);
            DockPanel.SetDock(collapseButton, Dock.Right);
            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { selectionButton, songButton, historyButton } };
            var header = new DockPanel { Margin = new Thickness(8, 8, 8, 8), LastChildFill = true, Children = { collapseButton, tabs } };
            DockPanel.SetDock(header, Dock.Top);

            // The Selection tab: what is selected, then the Session's Clip and Scene sections.
            heading = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            subtitle = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
            facts = new Grid { Margin = new Thickness(0, 8, 0, 0), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            hint = new TextBlock { FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
            patternButton = ActionButton("Open in Tracker", "Edit this pattern in the Tracker", () => showView("Pattern"));
            notesButton = ActionButton("Song notes", "Write about the song: the Song tab", () => ShowTab(SongTab, focus: true));
            var top = new StackPanel
            {
                Margin = new Thickness(12, 0, 12, 8),
                Children = { heading, subtitle, facts, hint, patternButton, notesButton },
            };
            DockPanel.SetDock(top, Dock.Top);
            selectionPage = new DockPanel { LastChildFill = true };
            selectionPage.Children.Add(top);

            // The clip panel of the Session View moves in here (the view keeps working without a window around it).
            clip = session?.MoveClipPanelToInspector(this);
            if (clip != null)
            {
                clip.Margin = new Thickness(12, 0, 12, 0);
                selectionPage.Children.Add(clip);
            }

            songPage = notes;
            if (songPage != null) songPage.IsVisible = false;

            historyPage = new HistoryTab(core) { IsVisible = false };

            var pages = new Panel();
            pages.Children.Add(historyPage);
            pages.Children.Add(selectionPage);
            if (songPage != null) pages.Children.Add(songPage);

            full = new DockPanel { LastChildFill = true };
            full.Children.Add(header);
            full.Children.Add(pages);

            // Collapsed, a rail with the button that opens it again.
            railButton = IconButton("‹", "Show the inspector (" + Shell.Commands.StudioKeymap.Get("view.inspector").KeyLabel() + ")", "Show inspector", Open);
            railButton.Margin = new Thickness(2, 8, 2, 0);
            railButton.VerticalAlignment = VerticalAlignment.Top;
            rail = new Border { Child = railButton };

            // The grip on the left edge sizes it.
            grip = new Border { Width = 5, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.SizeWestEast), HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(grip, "Resize inspector");
            grip.PointerPressed += GripPressed;
            grip.PointerMoved += GripMoved;
            grip.PointerReleased += GripReleased;

            frame = new Grid();
            frame.Children.Add(full);
            frame.Children.Add(rail);
            frame.Children.Add(grip);
            Child = frame;

            DockPanel.SetDock(this, Dock.Right);
            ApplyOpen();
            ApplyTab(focus: false);

            selection.Changed += OnSelectionChanged;
            if (core.SessionLayout != null) core.SessionLayout.Changed += OnLayoutChanged;
            Refresh();
        }

        // ------------------------------------------------------------------ state

        /// <summary>The layout around it changed: the window fits the panels to its width.</summary>
        public event Action Changed;

        /// <summary>
        /// The room the window leaves it (a narrow window): less than its least width, it shows as the rail while it is open
        /// (the setting is unchanged), else it is as wide as the room if that is less than its width.
        /// </summary>
        public void Fit(double available)
        {
            if (available == fit) return;
            fit = available;
            ApplyOpen(notify: false);
        }

        double fit = double.PositiveInfinity;

        /// <summary>Open and squeezed into the rail by a narrow window.</summary>
        public bool IsSqueezed => open && fit < MinPanelWidth;

        /// <summary>The width it takes now.</summary>
        public double EffectiveWidth => open && !IsSqueezed ? Math.Min(width, fit) : RailWidth;

        /// <summary>Shown (as opposed to the rail only).</summary>
        public bool IsOpen => open;

        /// <summary>The tab shown: <see cref="SelectionTab"/> or <see cref="SongTab"/>.</summary>
        public string CurrentTab => tab;

        /// <summary>The width when open (a rail when closed); remembered.</summary>
        public double PanelWidth
        {
            get => width;
            set
            {
                value = Math.Clamp(value, MinPanelWidth, MaxPanelWidth);
                if (value == width) return;
                width = value;
                Write(WidthKey, width);
                ApplyOpen();
            }
        }

        public SongNotesView SongNotes => notes;
        public SessionView.ClipPanel ClipPanel => clip;

        // For tests: the controls, as a user would use them.
        internal Button SelectionTabButton => selectionButton;
        internal Button SongTabButton => songButton;
        internal Button HistoryTabButton => historyButton;
        internal HistoryTab History => historyPage;
        internal Button CollapseButton => collapseButton;
        internal Button RailButton => railButton;
        internal Button PatternButton => patternButton;
        internal Button NotesButton => notesButton;
        internal Avalonia.Controls.Control Grip => grip;
        internal string HeadingText => heading.Text;
        internal string SubtitleText => subtitle.Text;
        internal string HintText => hint.Text;

        /// <summary>The facts rows, as "label: value" (the clip panel's own sections are not among them).</summary>
        internal IReadOnlyList<(string Label, string Value)> Facts =>
            Enumerable.Range(0, facts.Children.Count / 2)
                .Select(i => (((TextBlock)facts.Children[2 * i]).Text, ((TextBlock)facts.Children[2 * i + 1]).Text)).ToList();

        public void Open()
        {
            ShowTab(SelectionTab);
            SetOpen(true);
        }

        public void Close() => SetOpen(false);

        public void Toggle() => SetOpen(!open);

        void SessionView.IInspectorLink.Toggle() => Toggle();
        void SessionView.IInspectorLink.Open() => Open();

        /// <summary>Opens the inspector on a tab (the Song tab with the editor focused: Song Notes).</summary>
        public void ShowTab(string name, bool focus = false)
        {
            tab = name == SongTab ? SongTab : name == HistoryTabName ? HistoryTabName : SelectionTab;
            SetOpen(true);
            ApplyTab(focus);
        }

        void SetOpen(bool value)
        {
            if (open == value) return;
            open = value;
            Write(OpenKey, open);
            ApplyOpen();
            session?.InspectorChanged();
            if (open) Refresh();
        }

        void ApplyOpen(bool notify = true)
        {
            bool shown = open && !IsSqueezed;
            full.IsVisible = shown;
            rail.IsVisible = !shown;
            grip.IsVisible = shown;
            Width = EffectiveWidth;
            if (notify) Changed?.Invoke();
        }

        void ApplyTab(bool focus)
        {
            bool song = tab == SongTab && songPage != null;
            bool history = tab == HistoryTabName;
            selectionPage.IsVisible = !song && !history;
            if (songPage != null) songPage.IsVisible = song;
            historyPage.IsVisible = history;
            Mark(selectionButton, !song && !history);
            Mark(songButton, song);
            Mark(historyButton, history);
            if (song && focus) Dispatcher.UIThread.Post(() => notes.Editor.Focus(), DispatcherPriority.Input);
        }

        T Read<T>(string key, T def)
        {
            try { return registry == null ? def : registry.Read(key, def, RegistrySection); }
            catch { return def; }
        }

        void Write<T>(string key, T value)
        {
            try { registry?.Write(key, value, RegistrySection); }
            catch { /* a setting that can't be saved is not worth a failed click */ }
        }

        // ------------------------------------------------------------------ the selection

        void OnSelectionChanged(object source) => Refresh();

        void OnLayoutChanged()
        {
            if (!open || disposed) return;
            clip?.Follow(ClipSlot(), force: true);
            Refresh();
        }

        /// <summary>The Session slot the selection is on: the machine's track, in the scene the selection has; null if none.</summary>
        internal (int Track, int Scene)? ClipSlot()
        {
            var layout = core.SessionLayout;
            var doc = layout?.Document;
            if (TrackOf(selection.Machine) is not { } track || doc == null || doc.Scenes.Count == 0) return null;
            int? scene = selection.Scene;
            if (selection.Pattern is { } pattern)
            {
                // A clip is a pattern in a slot: the scene the selection has when the pattern is there, else its first.
                if (scene is { } sc && sc < doc.Scenes.Count && doc.GetSlot(track, sc) == pattern.Name) return (track, sc);
                for (int s = 0; s < doc.Scenes.Count; s++)
                    if (doc.GetSlot(track, s) == pattern.Name) return (track, s);
                return null;
            }
            return scene is { } one ? (track, Math.Clamp(one, 0, doc.Scenes.Count - 1)) : null;
        }

        int? TrackOf(IMachine machine)
        {
            var layout = core.SessionLayout;
            if (machine == null || layout?.Document == null) return null;
            for (int t = 0; t < layout.TrackCount; t++)
                if (layout.SequenceOf(t)?.Machine == machine) return t;
            return null;
        }

        /// <summary>Brings the panel up to date with the selection and the song (the window's timer calls it).</summary>
        public void Refresh()
        {
            if (!open || disposed) return;
            var machine = selection.Machine;
            var pattern = selection.Pattern;
            int tpb = Math.Max(1, core.TPB);
            var rows = new List<(string, string)>();
            string title, sub;
            if (machine == null)
            {
                title = "Song";
                sub = core.SongCore.SongName == null ? "Untitled" : System.IO.Path.GetFileName(core.SongCore.SongName);
                rows.Add(("Tempo", $"{core.BPM} BPM"));
                rows.Add(("Ticks per beat", tpb.ToString()));
                rows.Add(("Machines", core.Song.Machines.Count(m => m.DLL?.Info?.Type != MachineType.Master).ToString()));
            }
            else
            {
                title = machine.Name;
                sub = machine.DLL?.Info?.Type switch
                {
                    MachineType.Generator => "Generator",
                    MachineType.Effect => "Effect",
                    MachineType.Master => "Master",
                    _ => "Machine",
                };
                rows.Add(("Type", machine.DLL?.Name ?? ""));
                rows.Add(("Patterns", machine.Patterns.Count.ToString()));
                rows.Add(("Wires", $"{machine.Inputs.Count} in, {machine.Outputs.Count} out"));
                if (machine.IsMuted) rows.Add(("State", machine.IsBypassed ? "Muted, bypassed" : "Muted"));
                else if (machine.IsBypassed) rows.Add(("State", "Bypassed"));
                if (pattern != null)
                {
                    rows.Add(("Pattern", pattern.Name));
                    rows.Add(("Length", $"{pattern.Length} ticks, {pattern.Length / (double)tpb:0.##} beats"));
                }
            }

            string signature = title + "|" + sub + "|" + string.Join("|", rows.Select(r => r.Item1 + "=" + r.Item2)) + "|" + (machine == null);
            if (signature != lastFacts)
            {
                lastFacts = signature;
                heading.Text = title;
                subtitle.Text = sub;
                facts.Children.Clear();
                facts.RowDefinitions.Clear();
                for (int i = 0; i < rows.Count; i++)
                {
                    facts.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                    var label = new TextBlock { Text = rows[i].Item1, FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 2, 12, 2) };
                    var value = new TextBlock { Text = rows[i].Item2, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
                    Grid.SetRow(label, i);
                    Grid.SetRow(value, i);
                    Grid.SetColumn(value, 1);
                    facts.Children.Add(label);
                    facts.Children.Add(value);
                }
                hint.Text = machine == null ? "Select a machine, a pattern or a clip in any view to see its settings here." : "";
                hint.IsVisible = machine == null;
                patternButton.IsVisible = pattern != null;
                notesButton.IsVisible = machine == null;
            }

            if (clip != null)
            {
                bool hasTrack = TrackOf(machine) != null;
                clip.IsVisible = hasTrack;
                if (hasTrack) clip.Follow(ClipSlot());
            }
        }

        // ------------------------------------------------------------------ resizing

        double gripStart;

        void GripPressed(object sender, PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed) return;
            gripStart = width;
            e.Pointer.Capture(grip);
            e.Handled = true;
        }

        void GripMoved(object sender, PointerEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip) || Parent is not Visual parent) return;
            double x = e.GetPosition(parent).X;
            PanelWidth = (parent as Layoutable)?.Bounds.Width - x ?? width;
        }

        void GripReleased(object sender, PointerReleasedEventArgs e)
        {
            if (!ReferenceEquals(e.Pointer.Captured, grip)) return;
            e.Pointer.Capture(null);
            e.Handled = true;
        }

        // ------------------------------------------------------------------ parts

        Button TabButton(string name)
        {
            var b = new Button
            {
                Content = name,
                FontSize = 12,
                Padding = new Thickness(10, 4, 10, 4),
                MinHeight = 24,
                Focusable = false,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
            };
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => ShowTab(name, focus: name == SongTab);
            return b;
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? StudioTokens.SelectionFillBrush : Brushes.Transparent;
            b.BorderBrush = on ? StudioTokens.AccentBrush : Brushes.Transparent;
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
        }

        static Button IconButton(string glyph, string tip, string name, Action run)
        {
            var b = new Button { Content = glyph, FontSize = 15, Padding = new Thickness(6, 0, 6, 2), MinWidth = 24, MinHeight = 24, Focusable = false, Background = Brushes.Transparent };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => run();
            return b;
        }

        static Button ActionButton(string text, string tip, Action run)
        {
            var b = new Button { Content = text, FontSize = 12, Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Focusable = false };
            ToolTip.SetTip(b, tip);
            b.Click += (s, e) => run();
            return b;
        }

        /// <summary>The window closed: the selection and the layout no longer reach it.</summary>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            selection.Changed -= OnSelectionChanged;
            historyPage.Dispose();
            if (core.SessionLayout != null) core.SessionLayout.Changed -= OnLayoutChanged;
        }
    }
}
