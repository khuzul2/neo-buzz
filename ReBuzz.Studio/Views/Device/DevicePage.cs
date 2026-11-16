// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): one machine's device page: a bar (the machine's name, Editor | Parameters,
// the search, Presets, Float or Dock) over either its own editor (when it has one) or the device view of its parameters. The same page is docked
// in Detail ▸ Device and floats in its own window.

using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    internal sealed class DevicePage : UserControl
    {
        public const string EditorTab = "Editor";
        public const string ParametersTab = "Parameters";
        public const string RegistrySection = "Studio";
        public const string EditorKey = "DeviceEditor";

        readonly IMachine machine;
        readonly bool floating;
        readonly DeviceView view;
        readonly Border body = new Border();
        readonly Grid bar;
        readonly (TextBlock Title, StackPanel Tabs, StackPanel Right) barParts;
        bool narrow;

        /// <summary>Under this width the bar wraps onto two lines.</summary>
        public const double NarrowBar = 520;

        readonly Button editorButton, parametersButton, floatButton, presetsButton;
        readonly TextBlock title;
        Avalonia.Controls.Control editorContent;
        bool editorMode;
        readonly DeviceKeys keys;

        public DevicePage(IMachine machine, bool floating)
        {
            this.machine = machine;
            this.floating = floating;
            view = new DeviceView(machine) { Docked = !floating };
            keys = new DeviceKeys(machine, this, floating);
            Focusable = !floating;       // the docked page takes the keyboard focus on a click: the computer's keys play the machine only while it has it
            if (!floating)
                AddHandler(PointerPressedEvent, (s, e) => { if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TextBox && !IsKeyboardFocusWithin) Focus(); }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            Background = StudioTokens.SurfacePanelBrush;
            AutomationProperties.SetName(this, "Device page");

            title = new TextBlock
            {
                Text = Title(machine),
                FontSize = StudioTokens.FontBody,
                FontWeight = FontWeight.SemiBold,
                Foreground = StudioTokens.TextPrimaryBrush,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 0, 12, 0),
            };
            editorButton = Tab(EditorTab, "The machine's own editor");
            parametersButton = Tab(ParametersTab, "Every parameter, grouped and searchable");
            editorButton.IsVisible = HasEditor(machine);
            editorButton.Click += (s, e) => SetEditor(true, remember: true);
            parametersButton.Click += (s, e) => SetEditor(false, remember: true);
            floatButton = Tab(floating ? "Dock" : "Float", floating ? "Back to the Detail panel" : "Open this machine's device in its own window");
            floatButton.Click += (s, e) => FloatToggled?.Invoke();
            presetsButton = Tab("Presets", "Apply a preset, randomize or morph the unlocked parameters, edit the presets, copy and paste one");
            presetsButton.Click += (s, e) => DeviceMenus.Presets(machine, this, () => view.Refresh()).Open(presetsButton);

            var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { editorButton, parametersButton } };
            var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { presetsButton, floatButton } };
            // One row of 32 pt: name, tabs, search, Presets, Float. Under NarrowBar it wraps to two: the search takes the second.
            bar = new Grid { Margin = new Thickness(8, 0, 8, 0), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), RowDefinitions = new RowDefinitions("32,Auto") };
            title.MaxWidth = 240;
            barParts = (title, tabs, right);
            bar.Children.Add(title);
            bar.Children.Add(tabs);
            view.Toolbar.Margin = new Thickness(12, 0, 8, 0);
            bar.Children.Add(view.Toolbar);       // tab order is reading order: the search, then the buttons at the right
            bar.Children.Add(right);
            bar.SizeChanged += (s, e) => LayoutBar(e.NewSize.Width);
            LayoutBar(double.PositiveInfinity);
            var root = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Top);
            root.Children.Add(bar);
            root.Children.Add(body);
            Content = root;
            bool wantEditor = HasEditor(machine) && Read(EditorKey, false);
            SetEditor(wantEditor, remember: false);
        }

        public DeviceKeys Keys => keys;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            keys.Attach();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            keys.Detach();
        }

        /// <summary>Whether the bar is on two lines (a page narrower than <see cref="NarrowBar"/>).</summary>
        public bool BarWrapped => narrow;

        public Avalonia.Controls.Control Bar => bar;

        void LayoutBar(double width)
        {
            bool wrap = width < NarrowBar;
            narrow = wrap;
            void At(Avalonia.Controls.Control c, int row, int col, int span = 1)
            {
                Grid.SetRow(c, row);
                Grid.SetColumn(c, col);
                Grid.SetColumnSpan(c, span);
            }
            barParts.Title.MaxWidth = wrap ? 96 : 240;     // the name gives way before the buttons do
            At(barParts.Title, 0, 0);
            At(barParts.Tabs, 0, 1);
            At(barParts.Right, 0, 4);
            if (wrap) At(view.Toolbar, 1, 0, 5);
            else At(view.Toolbar, 0, 3);
            view.Toolbar.Margin = wrap ? new Thickness(0, 0, 0, 4) : new Thickness(12, 0, 8, 0);
            view.Toolbar.MaxWidth = wrap && !double.IsInfinity(width) ? Math.Max(120, width) : double.PositiveInfinity;   // the search shrinks to the line
        }

        public IMachine Machine => machine;

        public bool IsFloating => floating;

        public DeviceView Parameters => view;

        public bool EditorShown => editorMode;

        public Avalonia.Controls.Control EditorContent => editorContent;

        public Button EditorButton => editorButton;

        public Button ParametersButton => parametersButton;

        public Button FloatButton => floatButton;

        public Button PresetsButton => presetsButton;

        /// <summary>The Float button (Dock in a window) was pressed.</summary>
        public event Action FloatToggled;

        static string Title(IMachine m) => m.DLL?.Info?.Name is { Length: > 0 } t && t != m.Name ? $"{m.Name} · {t}" : m.Name;

        /// <summary>Whether the machine brings an editor of its own (its GUI factory).</summary>
        public static bool HasEditor(IMachine m)
        {
            try { return m?.DLL?.GUIFactory != null; }
            catch (Exception) { return false; }
        }

        static Button Tab(string text, string tip)
        {
            var b = StudioButtons.KeepFocus(new Button { Content = text, BorderThickness = new Thickness(1) });
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, text);
            return b;
        }

        public void SetEditor(bool editor, bool remember)
        {
            if (editor && !HasEditor(machine)) editor = false;
            editorMode = editor;
            if (remember) Write(EditorKey, editor);
            if (editor)
            {
                var open = StudioMachineUiHost.GuiWindowOf(machine);
                if (open is { IsVisible: true })
                    body.Child = Note("The editor is open in its own window.", "Show window", () => (machine.Graph?.Buzz as ReBuzzCore)?.UiHost?.OpenGui(machine, null));
                else
                {
                    editorContent ??= StudioMachineUiHost.GuiContent?.Invoke(machine);
                    body.Child = editorContent == null
                        ? Note("This machine's editor doesn't run here.", "Parameters", () => SetEditor(false, true))
                        : MachineGuis.Resizable(machine) ? editorContent : new ScrollViewer { Content = editorContent, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
                }
            }
            else body.Child = view;
            view.Toolbar.IsVisible = !editorMode;
            view.Options.IsVisible = !editorMode;
            Mark(editorButton, editorMode);
            Mark(parametersButton, !editorMode);
            Mark(floatButton, false);
            Mark(presetsButton, false);
        }

        static void Mark(Button b, bool on)
        {
            b.Background = on ? StudioTokens.SelectionFillBrush : Brushes.Transparent;
            b.BorderBrush = on ? StudioTokens.AccentBrush : Brushes.Transparent;
            b.Foreground = on ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
        }

        static Avalonia.Controls.Control Note(string text, string action, Action run)
        {
            var b = Tab(action, text);
            b.Click += (s, e) => run();
            return new StackPanel
            {
                Margin = new Thickness(16), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left,
                Children = { new TextBlock { Text = text, FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap }, b },
            };
        }

        internal static T Read<T>(string key, T def)
        {
            try { return StudioHost.Current?.Registry == null ? def : StudioHost.Current.Registry.Read(key, def, RegistrySection); }
            catch { return def; }
        }

        internal static void Write<T>(string key, T value)
        {
            try { StudioHost.Current?.Registry?.Write(key, value, RegistrySection); }
            catch { /* a setting that can't be saved is not worth a failed click */ }
        }
    }
}
