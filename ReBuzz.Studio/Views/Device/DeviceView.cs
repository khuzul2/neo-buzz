// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): the device view of a machine, the parameters page. A search box (⌘F; Ctrl+F
// elsewhere), the macro strip, the grouped, virtualised list (DeviceList) and, under it, the Explain text of the parameter last
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
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    internal sealed class DeviceView : UserControl
    {
        readonly IMachine machine;
        DeviceLayout layout;
        string signature;
        readonly DeviceList list, macros;
        readonly TextBox search;
        readonly ScrollViewer scroller;
        readonly Border explain;
        readonly TextBlock explainTitle, explainValue, explainHow, explainSound, count;
        readonly HashSet<string> collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };

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
                    list.Focus();
                    e.Handled = true;
                }
                else if (e.Key == Key.Down || e.Key == Key.Enter)
                {
                    list.Focus();
                    e.Handled = true;
                }
            };
            count = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            var top = new DockPanel { Margin = new Thickness(Pad, 6, Pad, 6), LastChildFill = false };
            top.Children.Add(search);
            top.Children.Add(count);

            list = new DeviceList(machine);
            macros = new DeviceList(machine) { Height = MacroItem.StripHeight };
            list.HeaderClicked += s => { if (!collapsed.Remove(s.Name)) collapsed.Add(s.Name); Refill(); };
            list.FocusChanged += p => FocusChanged(list, p);
            macros.FocusChanged += p => FocusChanged(macros, p);
            scroller = new ScrollViewer
            {
                Content = list,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Focusable = false,
            };

            explainTitle = new TextBlock { FontSize = StudioTokens.FontBody, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            explainValue = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.AccentBrush, Margin = new Thickness(12, 0, 0, 0) };
            explainHow = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextPrimaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            explainSound = new TextBlock { FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
            explain = new Border
            {
                // A fixed height, so that focusing a parameter never moves the rows under the pointer (a double click needs them still).
                Height = ExplainHeight,
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
            DockPanel.SetDock(top, Dock.Top);
            root.Children.Add(top);
            DockPanel.SetDock(macros, Dock.Top);
            root.Children.Add(macros);
            DockPanel.SetDock(explain, Dock.Bottom);
            root.Children.Add(explain);
            root.Children.Add(scroller);
            Content = root;
            ShowExplain(null);
            Refill();
        }

        /// <summary>What decides the layout besides the machine's parameters: its track and input counts.</summary>
        string Signature() => string.Join(",", machine.ParameterGroups.Select(g => g.TrackCount)) + (machine.HasStereoInput ? "s" : "m");

        /// <summary>Tracks or inputs came or went: the groups and the table are made again.</summary>
        void Rebuild()
        {
            signature = Signature();
            layout = DeviceLayout.Build(machine);
            Refill();
        }

        const double Pad = 12;
        const double ExplainHeight = 84;
        public const string ExplainPrompt = "Click a parameter to see what it does.";

        public static string FindKeyLabel => OperatingSystem.IsMacOS() ? "⌘F" : "Ctrl+F";

        public IMachine Machine => machine;

        public DeviceLayout Layout => layout;

        public DeviceList List => list;

        public DeviceList MacroStrip => macros;

        public TextBox Search => search;

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
            var items = layout.Items(Query, collapsed);
            list.SetItems(items);
            macros.SetItems(layout.Macros.Count > 0 ? new DeviceItem[] { new MacroItem(layout.Macros) } : Array.Empty<DeviceItem>());
            macros.IsVisible = layout.Macros.Count > 0;
            if (list.Focused == null && macros.Focused == null && ExplainFocused) ShowExplain(null);
            int shown = items.Sum(i => i switch { ParamItem => 1, TableItem t => t.Columns.Count, _ => 0 });
            int all = layout.Sections.Sum(s => s.IsTable ? s.Params.Count : s.Params.Count);
            count.Text = Query.Trim().Length == 0 || collapsed.Count > 0 && shown == 0 ? $"{all} parameters" : $"{shown} of {all}";
            if (items.Count == 0 && Query.Trim().Length > 0) count.Text = "No parameter matches";
            scroller.Offset = new Vector(scroller.Offset.X, 0);
        }

        // ------------------------------------------------------------------ explain

        void FocusChanged(DeviceList source, DeviceParam p)
        {
            if (source == list && macros.Focused != null) macros.SetFocus(null);
            else if (source == macros && list.Focused != null) list.SetFocus(null);
            ShowExplain(p);
        }

        /// <summary>The focused parameter's name and value, and its How and Sound when the manifest has them (its description else).</summary>
        public void ShowExplain(DeviceParam p)
        {
            ExplainFocused = p != null;
            if (p == null)
            {
                explainTitle.Text = "";
                explainValue.Text = "";
                explainHow.IsVisible = true;
                explainHow.Text = ExplainPrompt;
                explainSound.IsVisible = false;
                return;
            }
            explainTitle.Text = p.IsTrack ? $"{p.FullName}" : p.Group.Length > 0 && p.Group != "Global" && p.Group != "Inputs" ? $"{p.Group}: {p.Name}" : p.Name;
            explainValue.Text = p.Describe(p.Value);
            bool has = p.HasExplain;
            explainHow.IsVisible = has ? p.How.Length > 0 : p.Description.Length > 0;
            explainHow.Text = has ? "How: " + p.How : p.Description;
            explainSound.IsVisible = has && p.Sound.Length > 0;
            explainSound.Text = "Sound: " + p.Sound;
        }

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
            list.Poll();
            macros.Poll();
            if (ExplainFocused && (list.Focused ?? macros.Focused) is { } p) explainValue.Text = p.Describe(p.Value);
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
