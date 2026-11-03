#nullable disable
// Neo machines' editor kit (docs/specs/machines.md §4): the editor shell every Neo machine's GUI
// derives from (header with the machine name and presets, then the machine's own body), and the
// preset bar over ReBuzz's preset files. EditorContext is in Params/EditorContext.cs (shared with
// the studio's Avalonia kit, docs/specs/studio-mvp.md S1).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using NeoBuzz.Dsp;

namespace NeoBuzz.Editor
{
    /// <summary>Base of every Neo editor: header (name, presets) over the machine's body.</summary>
    internal abstract class NeoEditor : UserControl, IMachineGUI
    {
        IMachine machine;
        EditorContext ctx;

        protected NeoEditor()
        {
            Background = Theme.Background;
            Foreground = Theme.Text;
            FontFamily = Theme.FontFamily;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
        }

        protected abstract string Title { get; }
        protected abstract NeoColor Accent { get; }
        internal abstract ParamDef[] Table { get; }

        /// <summary>Builds the editor's body.</summary>
        internal abstract UIElement BuildBody(EditorContext ctx);

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
            ctx = new EditorContext(machine, Table, Accent);
            var root = new DockPanel { Background = Theme.Background, LastChildFill = true };
            var header = new DockPanel { Margin = new Thickness(8, 6, 8, 2), LastChildFill = false };
            var title = new TextBlock
            {
                Text = Title,
                Foreground = Theme.Brush(Accent),
                FontFamily = Theme.FontFamily,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(title, Dock.Left);
            header.Children.Add(title);
            var presets = new PresetBar(machine, Accent);
            DockPanel.SetDock(presets, Dock.Right);
            header.Children.Add(presets);
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            UIElement body;
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
    }

    /// <summary>
    /// ReBuzz presets for the machine: the current name (click for the list), previous / next,
    /// and save. Factory presets ship as "Machine.prs.xml" next to the DLL.
    /// </summary>
    internal sealed class PresetBar : FrameworkElement
    {
        readonly IMachine machine;
        readonly NeoColor accent;
        readonly WpfCanvas canvas;
        string current = "Presets";

        public PresetBar(IMachine machine, NeoColor accent)
        {
            this.machine = machine;
            this.accent = accent;
            canvas = new WpfCanvas(this);
            Width = PresetBarFace.Width;
            Height = PresetBarFace.H;
            Cursor = Cursors.Hand;
            ToolTip = "Presets: click the name for the list, arrows to step, Save to store the current sound";
        }

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

        void Load(string name)
        {
            try
            {
                machine.GetPreset(name).Apply(machine, true);
                current = name;
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Preset");
            }
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
                ItemsControl parent = menu;
                if (group.Key.Length > 0)
                {
                    var sub = new MenuItem { Header = group.Key };
                    menu.Items.Add(sub);
                    parent = sub;
                }
                foreach (var n in group)
                {
                    var item = new MenuItem { Header = n, IsChecked = n == current };
                    string name = n;
                    item.Click += (s, e) => Load(name);
                    parent.Items.Add(item);
                }
            }
            menu.PlacementTarget = this;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        static string GroupOf(string name)
        {
            int i = name.IndexOf(" — ", StringComparison.Ordinal);
            if (i < 0) i = name.IndexOf(": ", StringComparison.Ordinal);
            return i > 0 ? name.Substring(0, i) : "";
        }

        void Save()
        {
            var dlg = new NameDialog(current == "Presets" || current == "<default>" ? "" : current) { Owner = Window.GetWindow(this) };
            if (dlg.ShowDialog() != true || string.IsNullOrWhiteSpace(dlg.Value)) return;
            try
            {
                machine.SetPreset(dlg.Value.Trim(), new Preset(machine, false, true));
                current = dlg.Value.Trim();
            }
            catch (Exception e)
            {
                MessageBox.Show(e.Message, "Save preset");
            }
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
            PresetBarFace.Draw(canvas.On(dc), current == "<default>" ? "Init" : current, accent);
    }

    /// <summary>A small dialog asking for a preset name.</summary>
    internal sealed class NameDialog : Window
    {
        readonly TextBox box;

        public NameDialog(string initial)
        {
            Title = "Save preset";
            Width = 300;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = Theme.Panel;
            var stack = new StackPanel { Margin = new Thickness(12) };
            stack.Children.Add(new TextBlock { Text = "Name (\"Group — Name\" files it in a group)", Foreground = Theme.Text, Margin = new Thickness(0, 0, 0, 6) });
            box = new TextBox { Text = initial ?? "", Margin = new Thickness(0, 0, 0, 10) };
            stack.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "Save", Width = 70, IsDefault = true, Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (s, e) => DialogResult = true;
            var cancel = new Button { Content = "Cancel", Width = 70, IsCancel = true };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            stack.Children.Add(buttons);
            Content = stack;
            Loaded += (s, e) => { box.Focus(); box.SelectAll(); };
        }

        public string Value => box.Text;
    }
}
