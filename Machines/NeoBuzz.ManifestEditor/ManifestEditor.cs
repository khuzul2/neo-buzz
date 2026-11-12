#nullable disable
// Fork (docs/specs/mcp-server.md §12.3, §12.11, U4): the manifest editor, a Neo-style editor made from
// any machine's manifest (curated or draft) on the Neo editor kit. One source for both front ends, as
// the Neo editors are: WPF in ReBuzz.exe, Avalonia in the studio (AVALONIA, the kit's WpfNames.cs).
// What it shows is planned toolkit-free (Core/ManifestFace.cs): a section per manifest group, a knob per
// continuous parameter showing its value as the agent server does, choice buttons, drop-downs and
// switches, an ADSR picture per envelope, the response plot where it can be honest, and each safe
// range marked on its knob. A right click on a knob (or a click, then typing) puts it in the value
// box below, which takes a value as param_set does ("800 Hz", "-6 dB", "35%", "1/8"). The plain
// parameter list stays a button away. The machine itself is untouched: its parameters are set as the
// parameter window sets them.

using System;
using System.Collections.Generic;
using System.Linq;
#if AVALONIA
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Border = Avalonia.Controls.Border;
using WrapPanel = Avalonia.Controls.WrapPanel;
using TextBox = Avalonia.Controls.TextBox;
using Orientation = Avalonia.Layout.Orientation;
using Brush = Avalonia.Media.IBrush;
using ScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility;
#else
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
#endif
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    internal sealed class ManifestEditor : NeoEditor
    {
        /// <summary>The classic app's accents: generators warm, effects cool, kit colours no Neo machine uses.</summary>
        static readonly NeoColor GeneratorAccent = NeoColor.FromRgb(0xE8, 0xA8, 0x4A);
        static readonly NeoColor EffectAccent = NeoColor.FromRgb(0x6F, 0xA8, 0xF0);

        /// <summary>Knobs per row in a section.</summary>
        const int KnobsPerRow = 6;
        /// <summary>The sections wrap at this width, and scroll past this height (Pedal M1 has 54 parameters).</summary>
        const double MaxBodyWidth = 1240, MaxBodyHeight = 760;
        /// <summary>About how wide the kit's 11 px text is per character, to size controls before they draw.</summary>
        const double CharWidth = 6.2;
        /// <summary>The widest a knob's label goes on a line; a longer one takes a second line.</summary>
        internal const double KnobLabelWidth = 104;

        readonly MachineFace face;
        readonly ParamDef[] table;
        readonly IReadOnlyList<ParamSlot> slots;
        readonly Action openParameters;
        readonly Dictionary<FaceParam, UIElement> controls = new Dictionary<FaceParam, UIElement>();
        EditorContext ctx;
        TextBlock entryLabel, message;
        TextBox entry;

        /// <param name="table">One entry per slot: what each control shows (its text through the face's delegates).</param>
        /// <param name="openParameters">Opens the machine's plain parameter list.</param>
        public ManifestEditor(MachineFace face, ParamDef[] table, IReadOnlyList<ParamSlot> slots, Action openParameters)
        {
            this.face = face;
            this.table = table;
            this.slots = slots;
            this.openParameters = openParameters;
        }

        protected override string Title => face.Gear;
        /// <summary>Its category's identity (from the manifest's type, ux-overhaul.md P2a); the classic app's warm or cool.</summary>
        protected override NeoColor Accent => Tokens.Identity(Tokens.CategoryOf(face.Generator, face.Type), face.Generator ? GeneratorAccent : EffectAccent);
        internal override ParamDef[] Table => table;
        internal override IReadOnlyList<ParamSlot> Slots => slots;

        /// <summary>What it shows (for tests and the hosts).</summary>
        internal MachineFace Face => face;

        /// <summary>Each parameter's control (a ParamKnob, ParamChoice or ParamDropdown).</summary>
        internal IReadOnlyDictionary<FaceParam, UIElement> Controls => controls;

        /// <summary>The parameter the value box edits, or null.</summary>
        internal FaceParam Selected { get; private set; }

        /// <summary>The value box.</summary>
        internal TextBox Entry => entry;

        /// <summary>What the value box last said (an error, a warning), or "".</summary>
        internal string Message => message?.Text ?? "";

        int Raw(FaceParam p) => ctx?.Raw(p.Slot) ?? p.Default;

        internal override UIElement BuildBody(EditorContext context)
        {
            ctx = context;
            controls.Clear();
            var accent = Accent;
            var root = new StackPanel { Orientation = Orientation.Vertical, Margin = new Thickness(4, 0, 4, 2) };

            // The summary, with what its values rest on (the manifest's provenance: notes for whoever checks it) behind an ⓘ.
            var summary = Note(face.Summary.Length > 0 ? face.Summary : face.Gear, Theme.Text);
            summary.Margin = new Thickness(4, 0, 2, 4);
            var about = Info(face.Provenance);
            about.Margin = new Thickness(2, 0, 4, 4);
            root.Children.Add(Layout.Row(summary, about));
            if (face.Tracks > 1 && face.Params.Any(p => p.IsTrack))
                root.Children.Add(Note($"Track parameters show the first of its {face.Tracks} tracks; the parameter list has every track.", Theme.DimText));

            var sections = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = MaxBodyWidth };
            if (face.Response != null) sections.Children.Add(ResponseSection(face.Response, accent));
            foreach (var s in face.Sections) sections.Children.Add(BuildSection(s, accent));
            root.Children.Add(new ScrollViewer
            {
                Content = sections,
                MaxHeight = MaxBodyHeight,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            });
            root.Children.Add(Footer(accent));
            return root;
        }

        /// <summary>
        /// An ⓘ that holds notes for whoever checks the editor (where its values and pictures come from) in its tooltip,
        /// out of the way of people playing the machine.
        /// </summary>
        internal static TextBlock Info(string tip)
        {
            var info = new TextBlock
            {
                Text = "ⓘ",
                Foreground = Theme.DimText,
                FontFamily = Theme.FontFamily,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Top,
            };
            info.ToolTip = tip;
            AutomationProperties.SetName(info, "About");
            return info;
        }

        static TextBlock Note(string text, Brush brush) => new TextBlock
        {
            Text = text,
            Foreground = brush,
            FontFamily = Theme.FontFamily,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(4, 0, 4, 4),
        };

        UIElement ResponseSection(FaceResponse response, NeoColor accent)
        {
            var plot = new GraphView(FacePainters.ResponseWidth, FacePainters.ResponseHeight,
                (c, r) => FacePainters.Response(c, r, response, Raw, accent));
            AutomationProperties.SetName(plot, "Response");
            ctx.Changed += plot.InvalidateVisual;
            // How the plot is made (a design computed, or markers only) is a note for whoever checks it: its tooltip and an ⓘ.
            plot.ToolTip = response.Caption;
            var about = Info(response.Caption);
            about.HorizontalAlignment = HorizontalAlignment.Right;
            about.Margin = new Thickness(0, 2, 0, 0);
            return new Section(response.Computed ? "Response" : "Frequencies", accent, Layout.Column(plot, about));
        }

        UIElement BuildSection(FaceSection s, NeoColor accent)
        {
            var flow = new WrapPanel { Orientation = Orientation.Horizontal, MaxWidth = KnobsPerRow * 72 + 8 };
            foreach (var p in s.Params) flow.Children.Add(Control(p, s.Title));
            var column = Layout.Column(flow);
            foreach (var env in s.Envelopes)
            {
                var picture = new GraphView(FacePainters.EnvelopeWidth, FacePainters.EnvelopeHeight,
                    (c, r) => FacePainters.Envelope(c, r, env, Raw, accent));
                AutomationProperties.SetName(picture, env.Title);
                picture.ToolTip = env.Title + (env.InSeconds ? ", to scale (√ time)" : ": its times are bare scales, drawn by position");
                picture.Margin = new Thickness(2, 6, 2, 0);
                picture.HorizontalAlignment = HorizontalAlignment.Left;
                ctx.Changed += picture.InvalidateVisual;
                column.Children.Add(picture);
                // Named when a section has two, and said when its times aren't in seconds.
                if (s.Envelopes.Count > 1 || !env.InSeconds)
                {
                    var caption = Note(env.Title + (env.InSeconds ? "" : " (relative times: bare scales)"), Theme.DimText);
                    caption.FontSize = 10;
                    caption.Margin = new Thickness(2, 1, 2, 2);
                    column.Children.Add(caption);
                }
            }
            return new Section(s.Title, accent, column);
        }

        UIElement Control(FaceParam p, string section)
        {
            UIElement control;
            string tip = Tip(p);
            string label = Label(p.Label, section);
            switch (p.Control)
            {
                case FaceControl.Choice:
                case FaceControl.Switch:
                {
                    // Cells wide enough for the choices, and together for the label over them.
                    double cell = Math.Max(34, p.Choices.Max(c => (c ?? "").Length) * 6.5 + 14);
                    cell = Math.Ceiling(Math.Max(cell, (label.Length * CharWidth + 6) / p.Choices.Length));
                    var choice = new ParamChoice(ctx, p.Slot, label, cellWidth: cell);
                    choice.Margin = new Thickness(2, 2, 8, 4);
                    choice.ToolTip = tip;
                    control = choice;
                    break;
                }
                case FaceControl.Dropdown:
                {
                    double width = Math.Clamp(Math.Max(label.Length * CharWidth + 6, p.Choices.Max(c => (c ?? "").Length) * 6.5 + 26), 110, 190);
                    var dropdown = new ParamDropdown(ctx, p.Slot, label, width: width);
                    dropdown.Margin = new Thickness(2, 2, 8, 4);
                    dropdown.ToolTip = tip;
                    control = dropdown;
                    break;
                }
                default:
                {
                    // As wide as its label (the kit measures it), a long one on two lines.
                    var knob = new ParamKnob(ctx, p.Slot, label) { Origin = p.ArcOrigin, Safe = p.SafePositions, MaxLabelWidth = KnobLabelWidth };
                    knob.ToolTip = tip;
                    knob.RightClicked += () => Select(p, focus: true);
                    knob.UserChanged += t => Select(p, focus: false);
                    control = knob;
                    break;
                }
            }
            controls[p] = control;
            return control;
        }

        /// <summary>
        /// A control's label in its section: the section's name left off the front ("Low shelf gain" in Low shelf is
        /// "Gain", "LFO 1 delay" in LFO 1 "Delay"); whole (a knob's takes two lines where it is long), cut only past 40
        /// characters (the tooltip has the whole name).
        /// </summary>
        internal static string Label(string label, string section)
        {
            string s = (label ?? "").Trim();
            string title = (section ?? "").Replace(" (track 1)", "").Trim();
            foreach (var prefix in new[] { title, title.Split(' ')[0] })
            {
                if (prefix.Length == 0 || s.Length <= prefix.Length + 1) continue;
                if (s.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase))
                {
                    s = s.Substring(prefix.Length + 1);
                    s = char.ToUpperInvariant(s[0]) + s.Substring(1);
                    break;
                }
            }
            return s.Length <= MaxLabel ? s : s.Substring(0, MaxLabel - 1) + "…";
        }

        /// <summary>The longest label shown whole (two lines of a knob).</summary>
        const int MaxLabel = 40;

        static string Tip(FaceParam p)
        {
            var lines = new List<string> { p.Label + (p.Label != p.Name ? $" ({p.Name})" : "") };
            if (p.Description.Length > 0 && p.Description != p.Name) lines.Add(p.Description);
            lines.Add($"{p.Text(p.Min)} … {p.Text(p.Max)}, default {p.Text(p.Default)}" + (p.Approximate ? " (≈ units from its description)" : ""));
            if (p.SafeRange != null)
                lines.Add($"Safe range {p.SafeRange.Text}" + (p.SafeRange.Reason.Length > 0 ? $": outside it, it {p.SafeRange.Reason}" : "") +
                          ". Agents keep inside it; the knob turns through it.");
            if (p.Control == FaceControl.Knob) lines.Add("Right click to type a value in its unit.");
            return string.Join("\n", lines);
        }

        UIElement Footer(NeoColor accent)
        {
            entryLabel = new TextBlock
            {
                Text = "Value:",
                Foreground = Theme.DimText,
                FontFamily = Theme.FontFamily,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0),
                MinWidth = 60,
            };
            entry = new TextBox
            {
                Width = 150,
                FontFamily = Theme.FontFamily,
                FontSize = 12,
                Background = Theme.Background,
                Foreground = Theme.Text,
                BorderBrush = Theme.Brush(accent),
                VerticalAlignment = VerticalAlignment.Center,
                IsEnabled = false,
            };
            AutomationProperties.SetName(entry, "Value");
            entry.ToolTip = "A value in the parameter's unit, as param_set takes it (\"800 Hz\", \"-6 dB\", \"35%\", a choice, raw:N); Enter sets it.";
            // Before the box handles the key itself (tunnelling).
#if AVALONIA
            entry.AddHandler(InputElement.KeyDownEvent, (s, e) => OnEntryKey(e.Key, () => e.Handled = true), Avalonia.Interactivity.RoutingStrategies.Tunnel);
#else
            entry.PreviewKeyDown += (s, e) => OnEntryKey(e.Key, () => e.Handled = true);
#endif
            message = new TextBlock
            {
                Foreground = Theme.DimText,
                FontFamily = Theme.FontFamily,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Margin = new Thickness(8, 0, 8, 0),
                Text = "Right click a knob to type its value.",
            };
            var list = new ActionButton("Parameter list", accent, 104);
            list.ToolTip = "The machine's plain parameter list (every parameter and track)";
            list.Click += () => openParameters?.Invoke();
            var row = new DockPanel { Margin = new Thickness(4, 6, 4, 4), LastChildFill = true };
            DockPanel.SetDock(list, Dock.Right);
            row.Children.Add(list);
            var left = Layout.Row(entryLabel, entry);
            DockPanel.SetDock(left, Dock.Left);
            row.Children.Add(left);
            row.Children.Add(message);
            return row;
        }

        void OnEntryKey(Key key, Action handled)
        {
            if (key == Key.Enter)
            {
                Apply(entry.Text);
                handled();
            }
            else if (key == Key.Escape)
            {
                if (Selected != null) entry.Text = Selected.Text(Raw(Selected));
                message.Text = "";
                handled();
            }
        }

        /// <summary>Puts a parameter in the value box (a right click focuses it, to type).</summary>
        internal void Select(FaceParam p, bool focus)
        {
            if (entry == null) return;
            Selected = p;
            entryLabel.Text = p.Label + ":";
            entry.IsEnabled = true;
            entry.Text = p.Text(Raw(p));
            message.Text = p.SafeRange != null && !p.SafeRange.Contains(Raw(p)) ? SafeWarning(p) : "";
            if (focus)
            {
                entry.Focus();
                entry.SelectAll();
            }
        }

        /// <summary>Sets the selected parameter from text, as param_set reads it; one undo step (in the studio).</summary>
        internal bool Apply(string text)
        {
            var p = Selected;
            if (p == null || ctx == null) return false;
            int raw;
            try
            {
                if (p.Parse == null) throw new InvalidOperationException($"{p.Label} takes no typed values.");
                raw = p.Parse(text ?? "", Raw(p));
            }
            catch (Exception e)
            {
                message.Text = e.Message;
                message.Foreground = Theme.Brush(KnobModel.Unsafe);
                return false;
            }
            var edits = ctx.Edits;
            edits?.Begin();
            try
            {
                // Through its control's binding, so the control (and the pictures) show it at once.
                var binding = BindingOf(p);
                if (binding != null) binding.Set(raw);
                else ctx.Set(p.Slot, raw);
            }
            finally
            {
                edits?.End();
            }
            entry.Text = p.Text(Raw(p));
            message.Foreground = Theme.DimText;
            message.Text = p.SafeRange != null && !p.SafeRange.Contains(Raw(p)) ? SafeWarning(p) : "";
            if (message.Text.Length > 0) message.Foreground = Theme.Brush(KnobModel.Unsafe);
            return true;
        }

        ParamBinding BindingOf(FaceParam p) => controls.TryGetValue(p, out var c) ? c switch
        {
            ParamKnob k => k.Binding,
            ParamChoice ch => ch.Binding,
            ParamDropdown d => d.Binding,
            _ => null,
        } : null;

        static string SafeWarning(FaceParam p) =>
            $"Outside its safe range ({p.SafeRange.Text})" + (p.SafeRange.Reason.Length > 0 ? $": it {p.SafeRange.Reason}" : "") + ".";
    }
}
