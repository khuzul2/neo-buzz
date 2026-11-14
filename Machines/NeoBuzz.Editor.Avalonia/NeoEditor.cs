// Fork (docs/specs/studio-mvp.md S1): the Neo editor shell on Avalonia, ported from the WPF
// kit's NeoEditor.cs: the header (machine name and presets) over the machine's own body, and the
// preset bar over ReBuzz's preset files. Each editor gets an undo history for its edits
// (ParamEdits); a preset loaded from the bar is one undo step.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;
using KitTheme = NeoBuzz.Editor.Theme;   // inside controls, Theme is Avalonia's (StyledElement.Theme)

namespace NeoBuzz.Editor
{
    /// <summary>Base of every Neo editor: header (name, presets) over the machine's body.</summary>
    internal abstract class NeoEditor : UserControl, IMachineGUI
    {
        IMachine machine;
        EditorContext ctx;
        bool stale;

        protected NeoEditor()
        {
            Background = Theme.Background;
            Foreground = Theme.Text;
            FontFamily = Theme.FontFamily;
            UseLayoutRounding = true;
            // Grey-scale text, as WPF's editors draw it: no coloured subpixel fringes on the dark theme.
            RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        }

        /// <summary>
        /// The kit's theme, by the name the editors use (as on WPF): inside a control, Avalonia's
        /// own Theme property (StyledElement.Theme, a control theme) would take the name.
        /// </summary>
        protected new static class Theme
        {
            public static IBrush Background => KitTheme.Background;
            public static IBrush Panel => KitTheme.Panel;
            public static IBrush PanelEdge => KitTheme.PanelEdge;
            public static IBrush Text => KitTheme.Text;
            public static IBrush DimText => KitTheme.DimText;
            public static FontFamily FontFamily => KitTheme.FontFamily;
            public static IBrush Brush(NeoColor c) => KitTheme.Brush(c);
        }

        protected abstract string Title { get; }
        protected abstract NeoColor Accent { get; }
        internal abstract ParamDef[] Table { get; }

        /// <summary>Where each table entry is (U4's manifest editor: any group and track); null: global parameter i.</summary>
        internal virtual IReadOnlyList<ParamSlot> Slots => null;

        /// <summary>Builds the editor's body.</summary>
        internal abstract Control BuildBody(EditorContext ctx);

        /// <summary>What the body was built with (its parameters and undo history).</summary>
        internal EditorContext Context => ctx;

        /// <summary>The header's preset bar.</summary>
        internal PresetBar Presets { get; private set; }

        public IMachine Machine
        {
            get => machine;
            set
            {
                if (machine == value) return;
                ctx?.Detach();
                machine = value;
                if (machine != null) Build();
            }
        }

        void Build()
        {
            ctx = new EditorContext(machine, Table, Accent, Slots)
            {
                Edits = new ParamEdits(machine, Table, a => Dispatcher.UIThread.Post(a, DispatcherPriority.Background), Slots),
            };
            AutomationProperties.SetName(this, Title);
            var root = new DockPanel { Background = Theme.Background, LastChildFill = true };
            var header = new DockPanel { Margin = new Thickness(8, 6, 8, 2), LastChildFill = false };
            var title = new TextBlock
            {
                Text = Title,
                Foreground = Theme.Brush(Accent),
                FontFamily = Theme.FontFamily,
                FontSize = 16,
                FontWeight = FontWeight.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);
            Presets = new PresetBar(machine, Accent);
            // The bar names the preset the sound is, and marks it edited once a parameter moves off it.
            ctx.Changed += Presets.Refresh;
            DockPanel.SetDock(Presets, Dock.Right);
            header.Children.Add(Presets);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            Control body;
            try
            {
                body = BuildBody(ctx);
            }
            catch (Exception e)
            {
                body = new TextBlock { Text = "Editor error: " + e.Message, Foreground = Theme.Text, Margin = new Thickness(10) };
            }
            var holder = new Border { Padding = new Thickness(4), Child = body };
            root.Children.Add(holder);
            Content = root;
        }

        // A closed window drops its editor: let go of the parameters, and build again if shown again.
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            ctx?.Detach();
            stale = true;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!stale || machine == null) return;
            stale = false;
            Build();
        }
    }

    /// <summary>
    /// ReBuzz presets for the machine: the current preset's name (click for the list), with an "edited" mark once the
    /// parameters differ from it (PresetState), previous / next, and save. Factory presets ship as "Machine.prs.xml"
    /// next to the DLL.
    /// </summary>
    internal sealed class PresetBar : NeoElement
    {
        readonly IMachine machine;
        readonly NeoColor accent;
        readonly AvaloniaCanvas canvas;
        string current;   // the preset the sound is (ReBuzz's name; null: none known)
        bool edited;

        public PresetBar(IMachine machine, NeoColor accent)
        {
            this.machine = machine;
            this.accent = accent;
            canvas = new AvaloniaCanvas(this);
            Width = PresetBarFace.Width;
            Height = PresetBarFace.H;
            Cursor = Cursors.Hand;
            ToolTip = "Presets: click the name for the list, arrows to step, Save to store the current sound";
            AutomationProperties.SetName(this, "Presets");
            Refresh();
        }

        /// <summary>The preset the bar names: the one loaded last or the one the sound matches ("Init": the defaults; "Presets": none).</summary>
        public string Current => PresetState.Display(current);

        /// <summary>Whether the parameters differ from that preset (the bar marks it "edited").</summary>
        public bool Edited => edited;

        /// <summary>Reads again which preset the sound is (a parameter changed).</summary>
        public void Refresh()
        {
            var (name, e) = PresetState.Current(machine);
            if (name == current && e == edited) return;
            current = name;
            edited = e;
            InvalidateVisual();
        }

        /// <summary>The list, while it is open (for tests).</summary>
        public ContextMenu Menu { get; private set; }

        List<string> Names()
        {
            try
            {
                return machine.GetPresetNames().Where(n => n != "<default>").OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception)
            {
                return new List<string>();
            }
        }

        /// <summary>Loads a preset: one undo step.</summary>
        void Load(string name)
        {
            var edits = Edits;
            edits?.Begin();
            try
            {
                edits?.TouchAll();
                machine.GetPreset(name).Apply(machine, true);
                PresetState.Loaded(machine, name);
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Preset");
            }
            finally
            {
                edits?.End();
            }
            Refresh();
            InvalidateVisual();
        }

        void Step(int dir)
        {
            var names = Names();
            if (names.Count == 0) return;
            int i = names.IndexOf(current);
            i = i < 0 ? (dir > 0 ? 0 : names.Count - 1) : (i + dir + names.Count) % names.Count;
            Load(names[i]);
        }

        void ShowList()
        {
            var menu = new ContextMenu();
            var init = new MenuItem { Header = "Initialise (defaults)" };
            init.Click += (s, e) => Load("<default>");
            menu.Items.Add(init);
            menu.Items.Add(new Separator());
            // Group by the prefix before " — " or ":" (e.g. "Pad — Lush").
            foreach (var group in Names().GroupBy(n => GroupOf(n)))
            {
                var items = menu.Items;
                if (group.Key.Length > 0)
                {
                    var sub = new MenuItem { Header = group.Key };
                    menu.Items.Add(sub);
                    items = sub.Items;
                }
                foreach (var n in group)
                {
                    var item = new MenuItem { Header = n, ToggleType = MenuItemToggleType.Radio, IsChecked = n == current };
                    string name = n;
                    item.Click += (s, e) => Load(name);
                    items.Add(item);
                }
            }
            menu.Closed += (s, e) => { if (Menu == menu) Menu = null; };
            menu.PlacementTarget = this;
            menu.Placement = PlacementMode.Bottom;
            Menu = menu;
            menu.Open(this);
        }

        static string GroupOf(string name)
        {
            int i = name.IndexOf(" — ", StringComparison.Ordinal);
            if (i < 0) i = name.IndexOf(": ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : "";
        }

        async void Save()
        {
            string name = await NameDialog.Ask(TopLevel.GetTopLevel(this) as Window, current == null || current == PresetState.Defaults ? "" : current);
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                machine.SetPreset(name.Trim(), new Preset(machine, false, true));
                PresetState.Loaded(machine, name.Trim());
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Save preset");
            }
            Refresh();
            InvalidateVisual();
        }

        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            switch (PresetBarFace.ZoneAt(e.GetPosition(this).X))
            {
                case PresetBarFace.Zone.Previous: Step(-1); break;
                case PresetBarFace.Zone.List: ShowList(); break;
                case PresetBarFace.Zone.Next: Step(1); break;
                case PresetBarFace.Zone.Save: Save(); break;
            }
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext dc) =>
            PresetBarFace.Draw(canvas.On(dc), PresetState.Display(current), accent, edited);
    }

    /// <summary>A small dialog asking for a preset name.</summary>
    internal sealed class NameDialog : Window
    {
        readonly TextBox box;
        string result;

        NameDialog(string initial)
        {
            Title = "Save preset";
            Width = 300;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = KitTheme.Panel;
            ShowInTaskbar = false;
            // The studio's dialog (ReBuzz.Studio's StudioDialog; the kit doesn't reference it): the same classes, margin and rhythm.
            var stack = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
            stack.Children.Add(new TextBlock { Text = "Name (\"Group — Name\" files it in a group)", Foreground = KitTheme.Text });
            box = new TextBox { Text = initial ?? "" };
            stack.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var ok = new Button { Content = "Save", IsDefault = true };
            ok.Classes.Add("dialog");
            ok.Classes.Add("accent");
            ok.Click += (s, e) => { result = box.Text; Close(); };
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Classes.Add("dialog");
            cancel.Click += (s, e) => Close();
            if (OperatingSystem.IsMacOS()) { buttons.Children.Add(cancel); buttons.Children.Add(ok); }
            else { buttons.Children.Add(ok); buttons.Children.Add(cancel); }
            stack.Children.Add(buttons);
            Content = stack;
            Opened += (s, e) => { box.Focus(); box.SelectAll(); };
        }

        /// <summary>The name given, or null when cancelled.</summary>
        public static Task<string> Ask(Window owner, string initial)
        {
            var dialog = new NameDialog(initial);
            var done = new TaskCompletionSource<string>();
            dialog.Closed += (s, e) => done.TrySetResult(dialog.result);
            if (owner != null) _ = dialog.ShowDialog(owner);
            else dialog.Show();
            return done.Task;
        }
    }
}
