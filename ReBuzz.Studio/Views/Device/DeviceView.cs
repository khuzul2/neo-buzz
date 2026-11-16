// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): the device view of a machine, the parameters page. A search box (⌘F; Ctrl+F
// elsewhere), the chips row, the cards of knobs (DeviceCards) or the grouped, virtualised list (DeviceList) and, under it, the Explain text of the parameter last
// focused when the machine's manifest has it (neo-learn.md §5.3: How it changes the sound, and what you will hear).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal sealed class DeviceView : UserControl
    {
        readonly IMachine machine;
        DeviceLayout layout;
        string signature;
        readonly DeviceList list;
        readonly DeviceCards cards;
        readonly TextBox search;
        readonly ScrollViewer scroller;
        readonly Border explain;
        readonly StackPanel nothing, chips;
        readonly ScrollViewer chipScroller;
        readonly Button changedButton, knobsButton, listButton;
        readonly TextBlock explainTitle, explainValue, explainHow, explainSound, count;
        readonly HashSet<string> collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        DeviceMode mode;
        bool docked, changedOnly, filling;
        DeviceParam explained;
        int ticks;
        string changedSignature = "";

        public DeviceView(IMachine machine)
        {
            this.machine = machine;
            layout = DeviceLayout.Build(machine);
            signature = Signature();
            Background = StudioTokens.SurfacePanelBrush;
            Focusable = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Device");

            search = new TextBox
            {
                Watermark = "Search parameters  " + FindKeyLabel,
                MinHeight = 0,
                Height = 26,
                Padding = new Thickness(8, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
                FontSize = StudioTokens.FontBody,
                MinWidth = 160,
                MaxWidth = 360,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            Avalonia.Automation.AutomationProperties.SetName(search, "Search parameters");
            search.TextChanged += (s, e) => Refill();
            search.KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                {
                    if (!string.IsNullOrEmpty(search.Text)) search.Text = "";
                    FocusContent();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down || e.Key == Key.Enter)
                {
                    FocusContent();
                    e.Handled = true;
                }
            };
            count = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            // The search takes what room it is given (up to its MaxWidth), so the bar's second line never runs past the page.
            var toolbar = new Grid { VerticalAlignment = VerticalAlignment.Center, ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto") };
            search.MinWidth = 96;
            Grid.SetColumn(count, 1);
            toolbar.Children.Add(search);
            toolbar.Children.Add(count);
            Toolbar = toolbar;
            // A canvas modulator's output (ux-overhaul.md P5b): drag it onto a knob, a row here, a machine's editor.
            if (ModulationBoard.Of(machine) is { } modulator)
            {
                var handle = new NeoBuzz.Editor.ModSourceHandle(modulator, NeoBuzz.Editor.KnobModel.ModulationRing, 214) { Margin = new Thickness(8, 0, 0, 0) };
                Grid.SetColumn(handle, 2);
                toolbar.Children.Add(handle);
            }

            list = new DeviceList(machine);
            var category = ReBuzz.Studio.Views.MachineIdentity.CategoryOf(machine) ?? NeoBuzz.Editor.MachineCategory.Synth;
            cards = new DeviceCards(machine, NeoBuzz.Editor.Tokens.Identity(category));
            list.HeaderClicked += s => { if (!collapsed.Remove(s.Name)) collapsed.Add(s.Name); Refill(); };
            list.FocusChanged += p => ShowExplain(p);
            cards.FocusChanged += p => ShowExplain(p);
            cards.Changed += () => Refill();
            scroller = new ScrollViewer
            {
                Content = list,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Focusable = false,
            };

            // The options row: a chip per section (jump to it), Changed only, Knobs | List.
            chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
            chipScroller = new ScrollViewer
            {
                Content = chips,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Focusable = false,
                VerticalAlignment = VerticalAlignment.Center,
            };
            changedButton = Option("Changed only", "Only the parameters that are off their default");
            changedButton.Click += (s, e) => { ChangedOnly = !ChangedOnly; };
            knobsButton = Option("Knobs", "Cards of knobs");
            listButton = Option("List", "Rows with bars, in columns");
            knobsButton.Click += (s, e) => SetMode(DeviceMode.Knobs, remember: true);
            listButton.Click += (s, e) => SetMode(DeviceMode.List, remember: true);
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), Children = { changedButton, knobsButton, listButton } };
            Options = new DockPanel { Height = 30, Margin = new Thickness(8, 0, 8, 0), LastChildFill = true };
            DockPanel.SetDock(right, Dock.Right);
            Options.Children.Add(right);
            Options.Children.Add(chipScroller);

            explainTitle = new TextBlock { FontSize = StudioTokens.FontBody, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            explainValue = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.AccentBrush, Margin = new Thickness(12, 0, 0, 0) };
            explainHow = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextPrimaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            explainSound = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            explain = new Border
            {
                // One line until a parameter is chosen, then its fixed height (the strip is under the list: choosing grows it upward only
                // over the list's bottom rows, never moving the rows above, so a double click still finds them still).
                Height = ExplainLineHeight,
                ClipToBounds = true,
                IsVisible = layout.All.Any(p => p.HasExplain || p.Description.Length > 0),
                Background = StudioTokens.SurfaceControlBrush,
                BorderBrush = StudioTokens.SurfaceBorderBrush,
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(Pad, 6, Pad, 8),
                Child = new StackPanel
                {
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Children = { explainTitle, explainValue } },
                        explainHow,
                        explainSound,
                    },
                },
            };
            Avalonia.Automation.AutomationProperties.SetName(explain, "Explain");

            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(Options, Dock.Top);
            root.Children.Add(Options);
            DockPanel.SetDock(explain, Dock.Bottom);
            root.Children.Add(explain);
            nothing = new StackPanel
            {
                Margin = new Thickness(16), Spacing = 6, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsVisible = false,
                Children =
                {
                    new TextBlock { Text = NothingLoadedText, FontSize = StudioTokens.FontBody + 1, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush },
                    new TextBlock { Text = "Its parameters appear here once a plug-in is loaded.", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap },
                },
            };
            root.Children.Add(new Grid { Children = { scroller, cards, nothing } });     // the last child fills
            Content = root;
            mode = ReadMode(machine);
            ShowExplain(null);
            ApplyMode();
            Refill();
        }

        /// <summary>How the parameters show: cards of knobs, or rows in columns.</summary>
        public enum DeviceMode { Knobs, List }

        /// <summary>The setting a machine type's mode is kept in.</summary>
        public static string ModeKey(IMachine machine) => "DeviceMode:" + (machine?.DLL?.Name ?? machine?.Name ?? "");

        static DeviceMode ReadMode(IMachine machine) => DevicePage.Read(ModeKey(machine), "Knobs") == "List" ? DeviceMode.List : DeviceMode.Knobs;

        static Button Option(string text, string tip)
        {
            var b = StudioButtons.KeepFocus(new Button { Content = text, BorderThickness = new Thickness(1) });
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, text);
            return b;
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? StudioTokens.SelectionFillBrush : Brushes.Transparent;
            b.BorderBrush = on ? StudioTokens.AccentBrush : Brushes.Transparent;
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
        }

        public DeviceMode Mode => mode;

        /// <summary>Shows the cards or the list; <paramref name="remember"/> keeps the choice for this machine type.</summary>
        public void SetMode(DeviceMode next, bool remember)
        {
            if (remember) DevicePage.Write(ModeKey(machine), next.ToString());
            if (next == mode) return;
            mode = next;
            ApplyMode();
            Refill();
        }

        /// <summary>The docked page: one row of columns or cards, scrolling sideways. The floating one wraps and scrolls down.</summary>
        public bool Docked
        {
            get => docked;
            set
            {
                if (docked == value) return;
                docked = value;
                ApplyMode();
                Refill();
            }
        }

        public bool ChangedOnly
        {
            get => changedOnly;
            set
            {
                if (changedOnly == value) return;
                changedOnly = value;
                Mark(changedButton, changedOnly);
                Refill();
            }
        }

        public DeviceCards Cards => cards;

        /// <summary>The chips row: a chip per section, Changed only, Knobs | List.</summary>
        public DockPanel Options { get; }

        public IReadOnlyList<Control> ChipButtons => chips.Children.OfType<Control>().ToList();

        public Button ChangedButton => changedButton;

        public Button KnobsButton => knobsButton;

        public Button ListButton => listButton;

        public ScrollViewer ChipScroller => chipScroller;

        void ApplyMode()
        {
            bool knobs = mode == DeviceMode.Knobs;
            cards.SetSideways(docked);
            list.Sideways = docked;
            scroller.HorizontalScrollBarVisibility = docked ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
            scroller.VerticalScrollBarVisibility = docked ? Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled : Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
            scroller.IsVisible = !knobs && !layout.NothingLoaded;
            cards.IsVisible = knobs && !layout.NothingLoaded;
            Mark(knobsButton, knobs);
            Mark(listButton, !knobs);
            Mark(changedButton, changedOnly);
        }

        void FocusContent()
        {
            if (mode == DeviceMode.List) list.Focus();
            else Focus();
        }

        /// <summary>Scrolls to a section (a chip's job): its first card, or its header in the list.</summary>
        public bool JumpTo(DeviceSection section) => mode == DeviceMode.Knobs ? cards.JumpTo(section) : list.JumpTo(section);

        /// <summary>What decides the layout besides the machine's parameters: its track and input counts.</summary>
        string Signature() => string.Join(",", machine.ParameterGroups.Select(g => g.TrackCount)) + (machine.HasStereoInput ? "s" : "m") + "/" + DeviceLayout.HiddenCount(machine);

        /// <summary>Tracks or inputs came or went: the groups and the table are made again.</summary>
        void Rebuild()
        {
            signature = Signature();
            layout = DeviceLayout.Build(machine);
            Refill();
        }

        const double Pad = 12;
        const double ExplainHeight = 72, ExplainLineHeight = 28;
        public const string ExplainPrompt = "Click a parameter to see what it does.";
        public const string NothingLoadedText = "Load a plug-in";

        /// <summary>The "Load a plug-in" note shows where a machine has parameters but all are hidden (Neo Plug before a plug-in is loaded).</summary>
        public bool NothingLoadedShown => nothing.IsVisible;

        public static string FindKeyLabel => OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl+F";

        public IMachine Machine => machine;

        public DeviceLayout Layout => layout;

        public DeviceList List => list;

        public TextBox Search => search;

        /// <summary>The search box and the count, for the page's bar (they share its row, so the rows get the height).</summary>
        public Avalonia.Controls.Control Toolbar { get; }

        public ScrollViewer Scroller => scroller;

        /// <summary>What the search box holds.</summary>
        public string Query { get => search.Text ?? ""; set => search.Text = value; }

        /// <summary>Whether the machine has any help text to show (its manifest's How and Sound, or descriptions): the strip is in the panel.</summary>
        public bool ExplainShown => explain.IsVisible;

        /// <summary>The strip speaks of a focused parameter (else it says to click one).</summary>
        public bool ExplainFocused { get; private set; }

        public string ExplainTitle => explainTitle.Text;

        public string ExplainHow => explainHow.IsVisible ? explainHow.Text : null;

        public string ExplainSound => explainSound.IsVisible ? explainSound.Text : null;

        /// <summary>The count shown beside the search ("433 parameters", "12 of 433").</summary>
        public string CountText => count.Text;

        public bool IsCollapsed(string section) => collapsed.Contains(section);

        public void ToggleSection(string section)
        {
            if (!collapsed.Remove(section)) collapsed.Add(section);
            Refill();
        }

        public void FocusSearch()
        {
            search.Focus();
            search.SelectAll();
            // A page that has only just been put in the window takes focus at its next layout.
            if (!search.IsFocused) Dispatcher.UIThread.Post(() => { search.Focus(); search.SelectAll(); }, DispatcherPriority.Loaded);
        }

        void Refill()
        {
            if (filling) return;
            filling = true;
            try
            {
                var items = layout.Items(Query, collapsed, changedOnly);
                var shownCards = layout.Cards(Query, changedOnly);
                scroller.IsVisible = mode == DeviceMode.List && !layout.NothingLoaded;
                cards.IsVisible = mode == DeviceMode.Knobs && !layout.NothingLoaded;
                nothing.IsVisible = layout.NothingLoaded;
                if (mode == DeviceMode.List)
                {
                    list.SetItems(items);
                    scroller.Offset = new Vector(0, 0);
                }
                else cards.SetCards(shownCards, layout.TableSection, layout.TableColumnsFor(Query, changedOnly));
                FillChips();
                if (explained != null && !layout.All.Contains(explained)) ShowExplain(null);
                int shown = items.Sum(i => i switch { ParamItem => 1, TableItem t => t.Columns.Count, _ => 0 });
                int all = layout.Sections.Sum(s => s.Params.Count);
                count.Text = Query.Trim().Length == 0 && !changedOnly || collapsed.Count > 0 && shown == 0 && mode == DeviceMode.List ? $"{all} parameters" : $"{shown} of {all}";
                if (items.Count == 0 && (Query.Trim().Length > 0 || changedOnly)) count.Text = changedOnly && Query.Trim().Length == 0 ? "Nothing changed" : "No parameter matches";
                changedSignature = ChangedSignature();
            }
            finally { filling = false; }
        }

        /// <summary>The chips: a chip per section with something in it (the search and Changed only leave fewer).</summary>
        void FillChips()
        {
            chips.Children.Clear();
            foreach (var s in layout.Chips(Query, changedOnly))
            {
                var section = s;
                var chip = StudioButtons.KeepFocus(new Button { Content = section.Name, BorderThickness = new Thickness(1), BorderBrush = StudioTokens.SurfaceBorderBrush, Background = Brushes.Transparent, Foreground = StudioTokens.TextSecondaryBrush });
                ToolTip.SetTip(chip, "Jump to " + section.Name);
                Avalonia.Automation.AutomationProperties.SetName(chip, section.Name);
                chip.Click += (a, b) => JumpTo(section);
                chips.Children.Add(chip);
            }
            chipScroller.IsVisible = chips.Children.Count > 1;
        }

        string ChangedSignature() => changedOnly ? string.Join(",", layout.All.Where(p => p.IsChanged).Select(p => p.FullName)) : "";

        /// <summary>A gesture or an editor is open (a drag, a typed value): nothing is rebuilt under it.</summary>
        public bool Interacting => cards.Busy || list.Dragging || list.IsTyping;

        // ------------------------------------------------------------------ explain

        /// <summary>The focused parameter's name and value, and its How and Sound when the manifest has them (its description else).</summary>
        public void ShowExplain(DeviceParam p)
        {
            explained = p;
            ExplainFocused = p != null;
            if (p == null) SetExpanded(false);
            else if (!expanded) ExpandSoon();
            ShowDetail();
            if (p == null)
            {
                explainTitle.Text = "";
                explainValue.Text = "";
                explainHow.IsVisible = true;
                explainHow.Text = ExplainPrompt;
                explainSound.IsVisible = false;
                return;
            }
            explainTitle.Text = p.IsTrack ? $"{p.FullName}" : p.Group.Length > 0 && p.Group != "Global" && p.Group != DeviceLayout.OneGroup && p.Group != "Inputs" ? $"{p.Group}: {p.Name}" : p.Name;
            explainValue.Text = p.Describe(p.Value);
            bool has = p.HasExplain;
            explainHow.IsVisible = has ? p.How.Length > 0 : p.Description.Length > 0;
            explainHow.Text = has ? "How: " + p.How : p.Description;
            explainSound.IsVisible = has && p.Sound.Length > 0;
            explainSound.Text = "Sound: " + p.Sound;
        }

        bool expanded;
        DispatcherTimer expandTimer;

        /// <summary>The How and Sound lines show once the strip is open (before, they would peek under the one line); the prompt is the line.</summary>
        void ShowDetail()
        {
            explainTitle.IsVisible = ExplainFocused;
            explainValue.IsVisible = ExplainFocused;
            explainHow.Opacity = explainSound.Opacity = !ExplainFocused || expanded ? 1 : 0;
        }

        void SetExpanded(bool on)
        {
            expandTimer?.Stop();
            expanded = on;
            explain.Height = on ? ExplainHeight : ExplainLineHeight;
            explain.Padding = on ? new Thickness(Pad, 6, Pad, 8) : new Thickness(Pad, 5, Pad, 0);
            explainHow.Margin = on ? new Thickness(0, 4, 0, 0) : new Thickness(0);
            ShowDetail();
        }

        /// <summary>
        /// The strip opens to its full height only once the click that chose the parameter can't be the first of a double click (which would
        /// type the value): growing at once moved the rows at the bottom of the list from under the pointer.
        /// </summary>
        void ExpandSoon()
        {
            expandTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            expandTimer.Stop();
            expandTimer.Tick -= ExpandNow;
            expandTimer.Tick += ExpandNow;
            expandTimer.Start();
        }

        void ExpandNow(object sender, EventArgs e)
        {
            expandTimer.Stop();
            if (Interacting)
            {
                expandTimer.Start();
                return;
            }
            var p = explained;
            if (p == null) return;
            SetExpanded(true);
            if (mode == DeviceMode.List && list.Focused != null) Dispatcher.UIThread.Post(() => list.Reveal(p), DispatcherPriority.Loaded);
        }

        /// <summary>Opens the strip now instead of after the pause (tests).</summary>
        internal void ExplainExpandedNow() => ExpandNow(null, EventArgs.Empty);

        /// <summary>The strip is open to its full height (a parameter is chosen and the pause has passed).</summary>
        public bool ExplainExpanded => expanded;

        // ------------------------------------------------------------------ life

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            poll.Tick += Tick;
            poll.Start();
            machine.PropertyChanged += MachineChanged;
            top = TopLevel.GetTopLevel(this);
            top?.AddHandler(KeyDownEvent, FindKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            poll.Stop();
            expandTimer?.Stop();
            poll.Tick -= Tick;
            machine.PropertyChanged -= MachineChanged;
            top?.RemoveHandler(KeyDownEvent, FindKey);
            top = null;
        }

        TopLevel top;

        void MachineChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != "TrackCount") return;
            if (Dispatcher.UIThread.CheckAccess()) Refresh();
            else Dispatcher.UIThread.Post(Refresh);
        }

        /// <summary>Makes the groups and the table again if the track or input count changed.</summary>
        public void Refresh()
        {
            if (Signature() != signature) Rebuild();
        }

        void Tick(object sender, EventArgs e)
        {
            Refresh();
            if (mode == DeviceMode.List) list.Poll();
            else cards.Poll();
            if (ExplainFocused && explained is { } p) explainValue.Text = p.Describe(p.Value);
            // Changed only follows the values: looked at once a second, and never under a drag.
            if (changedOnly && ++ticks % 10 == 0 && !Interacting && ChangedSignature() != changedSignature) Refill();
        }

        /// <summary>⌘F (Ctrl+F elsewhere) goes to the search box.</summary>
        void FindKey(object sender, KeyEventArgs e)
        {
            var command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key == Key.F && e.KeyModifiers == command)
            {
                FocusSearch();
                e.Handled = true;
            }
        }
    }
}
