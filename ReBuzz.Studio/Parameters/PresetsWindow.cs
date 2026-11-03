// Fork (docs/specs/studio-mvp.md S2): the parameter window's "Presets…" dialog, ported from the
// WPF PresetsWindow and ParameterWindowVM's preset editor: the machine's presets (its .prs.xml
// beside it in Gear, as BuzzGUI.Common's MachineExtensions read and write it) with a comment each;
// a name and Add (or Update, for an existing name) saves the machine's state now; Delete removes
// the selected one; Import… merges .prs.xml / .prs files in.

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;

namespace ReBuzz.Studio.Parameters
{
    using Control = Avalonia.Controls.Control;

    internal sealed class PresetsWindow : Window
    {
        readonly IMachine machine;
        readonly ListBox list = new ListBox { Margin = new Thickness(8, 8, 4, 4) };
        readonly TextBox comment = new TextBox { Margin = new Thickness(0, 8, 8, 4), AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        readonly TextBox nameBox = new TextBox { Margin = new Thickness(8, 0, 4, 8) };
        readonly Button add = new Button { Width = 70, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly Button delete = new Button { Width = 70, Content = "Delete", Margin = new Thickness(6, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };
        readonly Button import = new Button { Width = 80, Content = "Import…", Margin = new Thickness(6, 0, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Center };

        public PresetsWindow(IMachine machine)
        {
            this.machine = machine;
            Title = "Presets";
            Width = 420;
            Height = 300;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var grid = new Grid { RowDefinitions = new RowDefinitions("*,40"), ColumnDefinitions = new ColumnDefinitions("2*,3*") };
            void Place(Control c, int row, int col)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, col);
                grid.Children.Add(c);
            }
            Place(list, 0, 0);
            Place(comment, 0, 1);
            Place(nameBox, 1, 0);
            Place(new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 8, 8), Children = { add, delete, import } }, 1, 1);
            Content = grid;

            list.SelectionChanged += (s, e) => Select(list.SelectedItem as string);
            nameBox.TextChanged += (s, e) => UpdateButtons();
            add.Click += (s, e) => Add();
            delete.Click += (s, e) => Delete();
            import.Click += async (s, e) => await Import();
            Opened += (s, e) => nameBox.Focus();
            Fill();
        }

        /// <summary>Tests choose here instead of a file picker: the files to import.</summary>
        public static System.Func<IReadOnlyList<string>> ImportPicker { get; set; }

        public IReadOnlyList<string> Names => machine.GetPresetNames().ToList();

        public string PresetName { get => nameBox.Text; set => nameBox.Text = value; }

        public string Comment { get => comment.Text; set => comment.Text = value; }

        /// <summary>The machine's presets changed (the parameter window lists them).</summary>
        public event System.Action PresetsChanged;

        void Fill()
        {
            list.ItemsSource = machine.GetPresetNames().ToList();
            UpdateButtons();
        }

        void UpdateButtons()
        {
            string n = nameBox.Text?.Trim() ?? "";
            add.Content = machine.GetPresetNames().Contains(n) ? "Update" : "Add";
            add.IsEnabled = n.Length > 0;
            delete.IsEnabled = list.SelectedItem is string;
        }

        /// <summary>A preset selected: its name and comment fill the boxes (PresetEditorSelectedValue).</summary>
        public void Select(string name)
        {
            if (name == null)
            {
                UpdateButtons();
                return;
            }
            if (!Equals(list.SelectedItem, name)) list.SelectedItem = name;
            nameBox.Text = name;
            comment.Text = machine.GetPreset(name)?.Comment;
            UpdateButtons();
        }

        /// <summary>Saves the machine's state as the named preset, then closes (as the WPF window).</summary>
        public void Add()
        {
            string n = nameBox.Text?.Trim();
            if (string.IsNullOrEmpty(n)) return;
            machine.SetPreset(n, new Preset(machine, false, true) { Comment = comment.Text });
            PresetsChanged?.Invoke();
            Close();
        }

        public void Delete()
        {
            if (list.SelectedItem is not string n) return;
            machine.SetPreset(n, null);
            PresetsChanged?.Invoke();
            nameBox.Text = "";
            comment.Text = "";
            Fill();
        }

        async Task Import()
        {
            IReadOnlyList<string> files = ImportPicker?.Invoke();
            if (files == null && StorageProvider != null)
            {
                var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Import presets",
                    AllowMultiple = true,
                    FileTypeFilter = new[]
                    {
                        new FilePickerFileType("Preset Files") { Patterns = new[] { "*.prs.xml" } },
                        new FilePickerFileType("Old Preset Files") { Patterns = new[] { "*.prs" } },
                    },
                });
                files = picked.Select(f => f.TryGetLocalPath()).Where(p => p != null).ToList();
            }
            if (files == null || files.Count == 0) return;
            foreach (var f in files) machine.ImportPresets(f);
            PresetsChanged?.Invoke();
            Fill();
        }
    }
}
