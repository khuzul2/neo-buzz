// Fork (docs/specs/studio-mvp.md S2, §3.4): the generic parameter window, ported from
// BuzzGUI.ParameterWindow (WPF: ParameterWindowVM, ParameterVM, PresetsWindow, and the row
// template in RunTime/Themes/Default/ParameterWindow.xaml). Every machine without its own GUI here
// shows it: StudioMachineUiHost.ParameterWindowContent makes one per machine.
//  - The top row: the presets (choosing one applies it), undo and redo, Presets…, Help, Edit >>.
//  - Edit mode: preset B, the edit functions (randomize, morph) and their amount, locks, and which
//    parameters show (ParameterVisibility).
//  - The track choice (studio-only): every track, as WPF lists them, or one.
//  - One row per state parameter of every group and track (ParameterRow), with the WPF menu:
//    MIDI bind and learn, unbind, attributes, copy and paste of the values as a preset, locks,
//    add and delete a track, and the keyboard playing the machine (MIDI notes).
// Changes are undo steps on the song's stack (ParameterEdits); the WPF window's "Editor" tab
// (XAML templates) and a machine's embedded WPF GUI don't exist here.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Parameters
{
    using Control = Avalonia.Controls.Control;

    internal sealed class ParameterWindowView : UserControl
    {
        public enum EditFunction { RandomizeAbsolute, RandomizeRelative, RandomizeMaybe, RandomizeAB, RandomizeAorB, MorphAB }

        const string DefaultPreset = "<default>";
        static readonly Random random = new Random();

        readonly IMachine machine;
        readonly List<ParameterRow> rows = new List<ParameterRow>();
        readonly StackPanel list = new StackPanel();
        readonly ComboBox presetA = Compact(new ComboBox());
        readonly ComboBox presetB = Compact(new ComboBox { Margin = new Thickness(0, 8, 0, 0) });
        readonly ComboBox function = Compact(new ComboBox { Margin = new Thickness(6, 8, 0, 0) });
        readonly ComboBox trackBox = Compact(new ComboBox { MinWidth = 110 });
        readonly Button undo = Compact(new Button { Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0) });
        readonly Button redo = Compact(new Button { Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(4, 0) });
        readonly Button presets = Compact(new Button { Content = "Presets...", Margin = new Thickness(4, 0, 0, 0) });
        readonly Button help = Compact(new Button { Content = "Help", Margin = new Thickness(4, 0, 0, 0) });
        readonly Button edit = Compact(new Button { Content = "Edit >>", Margin = new Thickness(4, 0, 0, 0) });
        readonly Button apply = Compact(new Button { Content = "Apply", Margin = new Thickness(6, 8, 0, 0) });
        readonly Slider amount = new Slider { Minimum = 0, Maximum = 1, SmallChange = 0.01, LargeChange = 0.1, Value = 0.32 };
        readonly Grid editGrid = new Grid { IsVisible = false };
        readonly StackPanel trackRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
        readonly ScrollViewer scroller;
        readonly DispatcherTimer poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Dictionary<int, int> keysDown = new Dictionary<int, int>();
        bool editMode, filling, attached;
        TopLevel top;

        public ParameterWindowView(IMachine machine)
        {
            this.machine = machine;
            MinWidth = 410;
            Background = ParameterTheme.Background;
            Focusable = true;

            undo.Content = Icon(undoArrow: true);
            redo.Content = Icon(undoArrow: false);
            ToolTip.SetTip(presetA, "Preset A");
            ToolTip.SetTip(presetB, "Preset B");
            ToolTip.SetTip(function, "Edit Function");
            ToolTip.SetTip(amount, "Edit Function Parameter");
            function.ItemsSource = new[] { "Randomize (Absolute)", "Randomize (Relative)", "Randomize (Maybe)", "Randomize (A..B)", "Randomize (A or B)", "Morph (A..B)" };
            function.SelectedIndex = 0;

            // The top row (WPF: 2.5*, Auto, Auto, *, *, *).
            var top = new Grid { ColumnDefinitions = new ColumnDefinitions("2.5*,Auto,Auto,*,*,*"), Height = 23 };
            Place(top, presetA, 0, 0);
            Place(top, undo, 0, 1);
            Place(top, redo, 0, 2);
            Place(top, presets, 0, 3);
            Place(top, help, 0, 4);
            Place(top, edit, 0, 5);

            // Edit mode: preset B, the function, Apply; the amount under them.
            editGrid.ColumnDefinitions = new ColumnDefinitions("2.5*,*,*,*,*");
            editGrid.RowDefinitions = new RowDefinitions("31,Auto");
            Place(editGrid, presetB, 0, 0);
            Place(editGrid, function, 0, 1, 3);
            Place(editGrid, apply, 0, 4);
            Place(editGrid, amount, 1, 0, 5);

            trackRow.Children.Add(new TextBlock { Text = "Track", FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Foreground = ParameterTheme.Text });
            trackRow.Children.Add(trackBox);

            scroller = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var root = new Grid { Margin = new Thickness(8), RowDefinitions = new RowDefinitions("23,Auto,Auto,8,1,8,*") };
            Place(root, top, 0, 0);
            Place(root, editGrid, 1, 0);
            Place(root, trackRow, 2, 0);
            Place(root, new Border { Background = ParameterTheme.Separator }, 4, 0);
            Place(root, scroller, 6, 0);
            Content = root;

            presetA.SelectionChanged += (s, e) =>
            {
                if (!filling && presetA.SelectedItem is string name) SelectPreset(name);
            };
            trackBox.SelectionChanged += (s, e) =>
            {
                if (!filling) ApplyTrackFilter();
            };
            function.SelectionChanged += (s, e) => UpdateEditControls();
            amount.PropertyChanged += (s, e) =>
            {
                if (e.Property == RangeBase.ValueProperty) AmountChanged();
            };
            undo.Click += (s, e) => Undo();
            redo.Click += (s, e) => Redo();
            presets.Click += (s, e) => OpenPresets();
            help.Click += (s, e) => ShowHelp();
            edit.Click += (s, e) => EditMode = !EditMode;
            apply.Click += (s, e) => ApplyEditFunction(updatable: false);
            poll.Tick += (s, e) => Refresh();
            UpdateEditControls();
        }

        public IMachine Machine => machine;

        /// <summary>The rows, every parameter and track (shown or not).</summary>
        public IReadOnlyList<ParameterRow> Rows => rows;

        /// <summary>The rows shown now (edit mode, hidden parameters and the track choice decide).</summary>
        public IEnumerable<ParameterRow> ShownRows => rows.Where(r => r.IsVisible);

        public ComboBox PresetBox => presetA;

        public Button UndoButton => undo;

        public Button RedoButton => redo;

        public bool TrackChoiceShown => trackRow.IsVisible;

        static ReBuzzCore CoreOf(IMachine m) => m.Graph?.Buzz as ReBuzzCore;

        ReBuzzCore Core => CoreOf(machine);

        ManagedActionStack Stack => Core?.SongCore?.ActionStack;

        /// <summary>The WPF window's small controls (23 pixels, 10–11 point) instead of Fluent's.</summary>
        static T Compact<T>(T c) where T : TemplatedControl
        {
            c.FontSize = 11;
            c.MinHeight = 0;
            c.Height = 23;
            c.VerticalAlignment = VerticalAlignment.Stretch;
            c.HorizontalAlignment = HorizontalAlignment.Stretch;
            if (c is ContentControl content)
            {
                content.VerticalContentAlignment = VerticalAlignment.Center;
                content.HorizontalContentAlignment = HorizontalAlignment.Center;
                if (content.Padding == default) content.Padding = new Thickness(6, 0);
            }
            if (c is ComboBox combo)
            {
                combo.VerticalContentAlignment = VerticalAlignment.Center;
                combo.Padding = new Thickness(6, 0, 2, 0);
            }
            return c;
        }

        static void Place(Grid grid, Control c, int row, int column, int span = 1)
        {
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, span);
            grid.Children.Add(c);
        }

        /// <summary>The undo and redo arrows (the WPF window's button_undo/redo images), drawn.</summary>
        static Control Icon(bool undoArrow)
        {
            var g = StreamGeometry.Parse(undoArrow
                ? "M 4,6 L 1,3 L 4,0 M 1.5,3 L 9,3 C 12,3 13,5 13,7.5 C 13,10 11,12 8,12 L 5,12"
                : "M 10,6 L 13,3 L 10,0 M 12.5,3 L 5,3 C 2,3 1,5 1,7.5 C 1,10 3,12 6,12 L 9,12");
            return new Avalonia.Controls.Shapes.Path { Data = g, Stroke = ReBuzz.Studio.Shell.Design.StudioTokens.AccentBrush, StrokeThickness = 1.6, Width = 14, Height = 13, Stretch = Stretch.None };
        }

        // ---------------------------------------------------------------- life

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (attached) return;
            attached = true;
            machine.PropertyChanged += MachinePropertyChanged;
            CreateRows();
            FillPresets();
            top = TopLevel.GetTopLevel(this);
            if (top != null)
            {
                top.AddHandler(KeyDownEvent, WindowKeyDown);
                top.AddHandler(KeyUpEvent, WindowKeyUp);
                top.AddHandler(TextInputEvent, WindowTextInput);
                if (top is Window w)
                {
                    w.Activated += WindowActivated;
                    w.Deactivated += WindowDeactivated;
                    // The WPF window caps itself at the screen's height; the rows scroll.
                    var screen = w.Screens?.ScreenFromWindow(w);
                    MaxHeight = screen != null ? screen.WorkingArea.Height / screen.Scaling * 0.85 : 720;
                }
            }
            UpdateTitle();
            poll.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (!attached) return;
            attached = false;
            poll.Stop();
            NotesOff();
            machine.PropertyChanged -= MachinePropertyChanged;
            if (top != null)
            {
                top.RemoveHandler(KeyDownEvent, WindowKeyDown);
                top.RemoveHandler(KeyUpEvent, WindowKeyUp);
                top.RemoveHandler(TextInputEvent, WindowTextInput);
                if (top is Window w)
                {
                    w.Activated -= WindowActivated;
                    w.Deactivated -= WindowDeactivated;
                }
                top = null;
            }
            foreach (var r in rows) r.Release();
            rows.Clear();
            list.Children.Clear();
        }

        void MachinePropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            void Handle()
            {
                if (!attached) return;
                switch (e.PropertyName)
                {
                    case "TrackCount": CreateRows(); break;
                    case "Presets": FillPresets(); break;
                    case "Name": UpdateTitle(); break;
                }
            }
            if (Dispatcher.UIThread.CheckAccess()) Handle();
            else Dispatcher.UIThread.Post(Handle);
        }

        void UpdateTitle()
        {
            // The WPF window's title: "name - machine type".
            if (top is Window w) w.Title = $"{machine.Name} - {machine.DLL.Info.Name}";
        }

        // ---------------------------------------------------------------- rows

        void CreateRows()
        {
            int selectedTrack = SelectedTrack;
            foreach (var r in rows) r.Release();
            rows.Clear();
            list.Children.Clear();
            foreach (var (p, t) in machine.AllParametersAndTracks().Where(x => x.Item1.Flags.HasFlag(ParameterFlags.State)).Select(x => (x.Item1, x.Item2)))
            {
                var row = new ParameterRow(p, t) { EditMode = editMode };
                row.Slider.ValueChanging += v =>
                {
                    ParameterEdits.SetLive(row.Parameter, row.Track, v);
                    row.Refresh();
                };
                row.Slider.GestureEnded += (from, to, stepped) =>
                {
                    ParameterEdits.Changed(Core, row.Parameter, row.Track, from, to, stepped);
                    row.Refresh();
                    RefreshCommands();
                };
                row.Slider.DigitTyped += async digit =>
                {
                    var thumb = row.Slider.ThumbBounds;
                    var v = await ParameterDialogs.AskValue(row.Slider, new Point(thumb.X, row.Slider.Bounds.Height + 20), digit, row.Parameter.MinValue, row.Parameter.MaxValue, replace: false);
                    if (v is int value) row.Slider.Commit(value);
                };
                row.ContextMenu = RowMenu(row);
                rows.Add(row);
                list.Children.Add(row);
            }
            FillTracks(selectedTrack);
            ApplyTrackFilter();
        }

        /// <summary>Shows every row's value now (the engine's events need its ticks, so the window also polls).</summary>
        public void Refresh()
        {
            foreach (var r in rows) r.Refresh();
            RefreshCommands();
        }

        void RefreshCommands()
        {
            undo.IsEnabled = Stack?.CanUndo == true;
            redo.IsEnabled = Stack?.CanRedo == true;
        }

        public void Undo()
        {
            Stack?.Undo();
            Refresh();
        }

        public void Redo()
        {
            Stack?.Redo();
            Refresh();
        }

        // ---------------------------------------------------------------- tracks (studio-only)

        bool HasTrackParameters => machine.ParameterGroups.Count > 2
            && machine.ParameterGroups[2].Parameters.Any(p => p.Flags.HasFlag(ParameterFlags.State));

        /// <summary>The track shown alone, or -1 for every track (the WPF window's list).</summary>
        public int SelectedTrack
        {
            get => trackBox.SelectedIndex > 0 ? trackBox.SelectedIndex - 1 : -1;
            set => trackBox.SelectedIndex = value < 0 ? 0 : Math.Min(value + 1, trackBox.ItemCount - 1);
        }

        void FillTracks(int selected)
        {
            int count = HasTrackParameters ? machine.ParameterGroups[2].TrackCount : 0;
            trackRow.IsVisible = HasTrackParameters;
            filling = true;
            trackBox.ItemsSource = new[] { "All tracks" }.Concat(Enumerable.Range(0, count).Select(t => $"Track {t}")).ToList();
            trackBox.SelectedIndex = selected >= 0 && selected < count ? selected + 1 : 0;
            filling = false;
        }

        void ApplyTrackFilter()
        {
            int t = SelectedTrack;
            foreach (var r in rows)
            {
                r.FilteredOut = t >= 0 && r.Parameter.Group.Type == ParameterGroupType.Track && r.Track != t;
                r.UpdateVisibility();
            }
        }

        // ---------------------------------------------------------------- presets

        IEnumerable<string> PresetsWithDefault => new[] { DefaultPreset }.Concat(machine.GetPresetNames());

        void FillPresets()
        {
            filling = true;
            string a = presetA.SelectedItem as string ?? DefaultPreset;
            string b = presetB.SelectedItem as string ?? DefaultPreset;
            var names = PresetsWithDefault.ToList();
            presetA.ItemsSource = names;
            presetB.ItemsSource = names.ToList();
            presetA.SelectedItem = names.Contains(a) ? a : DefaultPreset;
            presetB.SelectedItem = names.Contains(b) ? b : DefaultPreset;
            filling = false;
        }

        /// <summary>Applies a preset ("&lt;default&gt;": the parameters' defaults) as one undo step.</summary>
        public void SelectPreset(string name)
        {
            if (name == null) return;
            if (!Equals(presetA.SelectedItem, name))
            {
                filling = true;
                presetA.SelectedItem = name;
                filling = false;
            }
            ParameterEdits.ApplyPreset(Core, machine, machine.GetPreset(name));
            Refresh();
        }

        /// <summary>The "Presets…" dialog (modal over the parameter window), its changes listed here.</summary>
        public PresetsWindow OpenPresets()
        {
            var window = new PresetsWindow(machine);
            window.PresetsChanged += FillPresets;
            if (TopLevel.GetTopLevel(this) is Window owner) _ = window.ShowDialog(owner);
            else window.Show();
            return window;
        }

        void ShowHelp()
        {
            try
            {
                machine.ShowHelp();
            }
            catch (Exception e)
            {
                Core?.DCWriteLine("Help: " + e.Message);
            }
        }

        /// <summary>The machine's values to the clipboard as a preset (Buzz text, or XML).</summary>
        public async Task CopyPreset(bool xml)
        {
            var preset = new Preset(machine, false, true);
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(xml ? preset.ToXml() : preset.ToString());
        }

        /// <summary>A preset of this machine type from the clipboard, or null (WPF: PastePresetCommand.CanExecute).</summary>
        public async Task<Preset> ClipboardPreset()
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            string text = clipboard != null ? await clipboard.TryGetTextAsync() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var p = Preset.FromXml(text) ?? new Preset(machine, text);
                return p != null && p.Machine == machine.DLL.Name ? p : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public async Task PastePreset()
        {
            var p = await ClipboardPreset();
            if (p == null) return;
            ParameterEdits.ApplyPreset(Core, machine, p);
            Refresh();
        }

        // ---------------------------------------------------------------- edit mode and functions

        public bool EditMode
        {
            get => editMode;
            set
            {
                if (value == editMode) return;
                editMode = value;
                editGrid.IsVisible = value;
                edit.Content = value ? "Edit <<" : "Edit >>";
                foreach (var r in rows)
                {
                    // Leaving edit mode saves which parameters show (UpdateParameterVisibility).
                    if (!value) ParameterVisibility.SetVisible(r.Parameter, r.IsShown);
                    r.EditMode = value;
                }
            }
        }

        public EditFunction Function
        {
            get => (EditFunction)Math.Max(0, function.SelectedIndex);
            set => function.SelectedIndex = (int)value;
        }

        public double Amount { get => amount.Value; set => amount.Value = value; }

        public string PresetBName { get => presetB.SelectedItem as string ?? DefaultPreset; set => presetB.SelectedItem = value; }

        void UpdateEditControls()
        {
            var f = Function;
            amount.IsEnabled = f != EditFunction.RandomizeAbsolute && f != EditFunction.RandomizeAB;
            presetB.IsEnabled = f == EditFunction.RandomizeAB || f == EditFunction.RandomizeAorB || f == EditFunction.MorphAB;
        }

        void AmountChanged()
        {
            // Morph follows the amount live, updating its own last step (EditFunctionParameter's setter).
            if (!editMode || Function != EditFunction.MorphAB) return;
            if (Stack is { CanUndo: true, CanRedo: false } s && s.Actions.LastOrDefault() is ParameterEdits.PresetChange last && last.IsUpdatable && last.Owner == this)
            {
                last.Update(RunEditFunction);
                Refresh();
            }
            else ApplyEditFunction(updatable: true);
        }

        public void ApplyEditFunction(bool updatable)
        {
            Stack?.Do(new ParameterEdits.PresetChange(Core, machine, RunEditFunction) { IsUpdatable = updatable, Owner = this });
            Refresh();
        }

        /// <summary>The edit function on every unlocked row (ParameterVM.ApplyEditFunction); null: it set the values.</summary>
        Preset RunEditFunction()
        {
            var a = machine.GetPreset(presetA.SelectedItem as string ?? DefaultPreset);
            var b = machine.GetPreset(PresetBName);
            double alpha = amount.Value;
            var f = Function;
            foreach (var row in rows)
            {
                if (row.IsLocked) continue;
                var p = row.Parameter;
                int t = row.Track, min = p.MinValue, max = p.MaxValue;
                int va = a.GetValueOrDefault(p, t), vb = b.GetValueOrDefault(p, t);
                int Clamp(double v) => Math.Clamp((int)Math.Round(v), min, max);
                int value = f switch
                {
                    EditFunction.RandomizeAbsolute => random.Next(min, max + 1),
                    EditFunction.RandomizeRelative => Clamp(p.GetValue(t) + alpha * alpha * random.Next(-(max - min), max - min + 1)),
                    EditFunction.RandomizeAB => Clamp(va + random.NextDouble() * (vb - va)),
                    EditFunction.RandomizeAorB => alpha < random.NextDouble() ? va : vb,
                    EditFunction.RandomizeMaybe => random.NextDouble() < alpha * alpha ? random.Next(min, max + 1) : va,
                    _ => Clamp(va + alpha * (vb - va)),
                };
                ParameterEdits.SetLive(p, t, value);
            }
            return null;
        }

        // ---------------------------------------------------------------- the row menu

        /// <summary>The row's right-click menu, made again as it opens (what's enabled changes).</summary>
        ContextMenu RowMenu(ParameterRow row)
        {
            var menu = new ContextMenu { FontSize = 12 };
            // The WPF window's menu is compact; Fluent's items are tall.
            menu.Styles.Add(new Style(x => x.OfType<MenuItem>())
            {
                Setters = { new Setter(MenuItem.PaddingProperty, new Thickness(8, 3)) },
            });
            async Task Fill()
            {
                var paste = new MenuItem { Header = "_Paste Preset", IsEnabled = false };
                paste.Click += async (s, e) => await PastePreset();
                menu.ItemsSource = MenuItems(row, paste);
                // Paste only what is a preset of this machine (WPF: PastePresetCommand.CanExecute).
                paste.IsEnabled = await ClipboardPreset() != null;
            }
            _ = Fill();
            menu.Opening += async (s, e) => await Fill();
            return menu;
        }

        List<Control> MenuItems(ParameterRow row, MenuItem paste)
        {
            MenuItem Item(string header, Action run, bool enabled = true)
            {
                var item = new MenuItem { Header = header, IsEnabled = enabled };
                item.Click += (s, e) => run();
                return item;
            }
            var p = row.Parameter;
            int t = row.Track;

            // Bind to MIDI Controller: Bind… (learn), then the controllers named in the MIDI settings.
            var bind = new MenuItem { Header = "_Bind to MIDI Controller" };
            var bindItems = new List<Control> { Item("Bind...", async () => await BindLearned(p, t)), new Separator() };
            var controllers = Core?.MIDIControllers;
            if (controllers != null && controllers.Count > 0)
                bindItems.AddRange(controllers.Select((name, index) => (Control)Item(name, () => p.BindToMIDIController(t, index))));
            else
                bindItems.Add(Item("(no controllers defined)", () => { }, enabled: false));
            bind.ItemsSource = bindItems;

            var attributes = new MenuItem { Header = "_Attributes", IsEnabled = machine.Attributes.Count > 0, ItemsSource = AttributeItems() };
            int tracks = machine.TrackCount;
            var keyboard = new MenuItem
            {
                Header = "_Keyboard Sends MIDI Notes",
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = KeyboardMidi,
            };
            ToolTip.SetTip(keyboard, "Keyboard sends MIDI note on/offs to the machine (the WPF window's setting).");
            keyboard.Click += (s, e) => KeyboardMidi = !KeyboardMidi;

            return new List<Control>
            {
                bind,
                Item("_Unbind All", () => machine.UnbindAllMIDIControllers()),
                new Separator(),
                attributes,
                new Separator(),
                Item("_Copy Preset (ASCII)", async () => await CopyPreset(xml: false)),
                Item("C_opy Preset (XML)", async () => await CopyPreset(xml: true)),
                paste,
                new Separator(),
                Item("_Unlock All", UnlockAll, editMode),
                Item("_Invert Locks", InvertLocks, editMode),
                new Separator(),
                Item("A_dd Track", () => SetTrackCount(tracks + 1), tracks < machine.DLL.Info.MaxTracks),
                Item("D_elete Last Track", () => SetTrackCount(tracks - 1), tracks > machine.DLL.Info.MinTracks),
                new Separator(),
                keyboard,
            };
        }

        async Task BindLearned(IParameter p, int track)
        {
            var c = await ParameterDialogs.AskController();
            if (c is (int channel, int controller)) p.BindToMIDIController(track, channel - 1, controller);
        }

        /// <summary>Adds or removes the last track: one undo step (WPF sets the count directly).</summary>
        public void SetTrackCount(int count)
        {
            count = Math.Clamp(count, machine.DLL.Info.MinTracks, machine.DLL.Info.MaxTracks);
            if (count == machine.TrackCount) return;
            if (Stack != null) Stack.Do(new SetTrackCountAction(machine, count));
            else machine.TrackCount = count;
        }

        public void UnlockAll()
        {
            foreach (var r in rows.Where(r => r.Parameter.Group.Type != ParameterGroupType.Input)) r.IsLocked = false;
        }

        public void InvertLocks()
        {
            foreach (var r in rows.Where(r => r.Parameter.Group.Type != ParameterGroupType.Input)) r.IsLocked = !r.IsLocked;
        }

        /// <summary>Each attribute: its values (the current one ticked, the default in bold), and "Other…" for big ranges.</summary>
        List<Control> AttributeItems()
        {
            var items = new List<Control>();
            foreach (var a in machine.Attributes)
            {
                var values = a.GetPresentableNumberOfValues().ToList();
                var children = new List<Control>();
                foreach (int v in values)
                {
                    var item = new MenuItem
                    {
                        Header = v.ToString(),
                        ToggleType = MenuItemToggleType.Radio,
                        IsChecked = v == a.Value,
                        FontWeight = v == a.DefValue ? FontWeight.Bold : FontWeight.Normal,
                        StaysOpenOnClick = true,
                    };
                    item.Click += (s, e) => a.Value = v;
                    children.Add(item);
                }
                if (values.Count < a.MaxValue - a.MinValue + 1)
                {
                    children.Add(new Separator());
                    var other = new MenuItem { Header = "Other..." };
                    other.Click += async (s, e) =>
                    {
                        var v = await ParameterDialogs.AskValue(this, new Point(40, 40), a.Value, a.MinValue, a.MaxValue, replace: true);
                        if (v is int value) a.Value = value;
                    };
                    children.Add(other);
                }
                items.Add(new MenuItem { Header = a.Name, ItemsSource = children });
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = "(no attributes)", IsEnabled = false });
            return items;
        }

        // ---------------------------------------------------------------- the keyboard plays the machine

        /// <summary>The WPF window's one setting (off by default): the keyboard sends MIDI notes.</summary>
        public static bool KeyboardMidi
        {
            get => StudioHost.Current?.Registry?.Read("KeyboardMIDI", false, "Parameter Window") ?? false;
            set => StudioHost.Current?.Registry?.Write("KeyboardMIDI", value, "Parameter Window");
        }

        // PianoKeys' scan codes, as physical keys: Z S X D C V G B H N J M, then Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P.
        static readonly PhysicalKey[] NoteKeys =
        {
            PhysicalKey.Z, PhysicalKey.S, PhysicalKey.X, PhysicalKey.D, PhysicalKey.C, PhysicalKey.V, PhysicalKey.G, PhysicalKey.B,
            PhysicalKey.H, PhysicalKey.N, PhysicalKey.J, PhysicalKey.M, PhysicalKey.Q, PhysicalKey.Digit2, PhysicalKey.W,
            PhysicalKey.Digit3, PhysicalKey.E, PhysicalKey.R, PhysicalKey.Digit5, PhysicalKey.T, PhysicalKey.Digit6, PhysicalKey.Y,
            PhysicalKey.Digit7, PhysicalKey.U, PhysicalKey.I, PhysicalKey.Digit9, PhysicalKey.O, PhysicalKey.Digit0, PhysicalKey.P,
        };

        public static int PianoKeyIndex(PhysicalKey key) => Array.IndexOf(NoteKeys, key);

        void WindowKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || !KeyboardMidi || e.KeyModifiers != KeyModifiers.None || machine.DLL.Info.Type != MachineType.Generator) return;
            if (top?.FocusManager?.GetFocusedElement() is TextBox) return;
            int i = PianoKeyIndex(e.PhysicalKey);
            if (i < 0) return;
            e.Handled = true;
            if (keysDown.ContainsKey(i)) return;
            int k = 12 * machine.BaseOctave + i;
            Core?.SendMIDIInput(MIDI.Encode(MIDI.NoteOn, k, 100));
            keysDown[i] = k;
        }

        void WindowKeyUp(object sender, KeyEventArgs e)
        {
            if (e.KeyModifiers != KeyModifiers.None) return;
            int i = PianoKeyIndex(e.PhysicalKey);
            if (i < 0 || !keysDown.TryGetValue(i, out int k)) return;
            keysDown.Remove(i);
            Core?.SendMIDIInput(MIDI.Encode(MIDI.NoteOff, k, 0));
            e.Handled = true;
        }

        void WindowTextInput(object sender, TextInputEventArgs e)
        {
            // * and / move the keyboard's octave (the machine's base octave), as on Windows.
            if (top?.FocusManager?.GetFocusedElement() is TextBox) return;
            if (e.Text == "*" && machine.BaseOctave < 9) { machine.BaseOctave++; e.Handled = true; }
            else if (e.Text == "/" && machine.BaseOctave > 0) { machine.BaseOctave--; e.Handled = true; }
        }

        void WindowActivated(object sender, EventArgs e)
        {
            // MIDI goes to the generator (or control machine) whose window is in front.
            if (Core != null && (machine.DLL.Info.Type == MachineType.Generator || machine.IsControlMachine))
                Core.MIDIFocusMachine = machine;
        }

        void WindowDeactivated(object sender, EventArgs e) => NotesOff();

        void NotesOff()
        {
            foreach (var k in keysDown.Values) Core?.SendMIDIInput(MIDI.Encode(MIDI.NoteOff, k, 0));
            keysDown.Clear();
        }
    }
}
