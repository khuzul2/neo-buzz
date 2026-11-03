// Fork (docs/specs/studio-mvp.md S0): the studio's main window. Menu, transport bar, one tab per
// view (StudioViews), status bar. Until the engine runs it shows its start-up steps.

using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Views;
using BuzzCommand = BuzzGUI.Interfaces.BuzzCommand;

namespace ReBuzz.Studio.Shell
{
    internal sealed class MainWindow : Window
    {
        readonly StudioHost host;
        readonly TextBlock startup = new TextBlock { Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        readonly DockPanel root = new DockPanel();
        readonly TransportBar transport;
        readonly TextBlock status = new TextBlock { Margin = new Thickness(8, 3), FontSize = 12, Foreground = Brushes.Gray };
        readonly TabControl tabs = new TabControl();
        readonly DispatcherTimer refresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        readonly Command undo, redo;

        public MainWindow(StudioHost host)
        {
            this.host = host;
            Title = "ReBuzz";
            Width = 1280;
            Height = 820;
            MinWidth = 640;
            MinHeight = 400;

            // The selected view first (IStudioEditTarget), then the song's undo history.
            undo = new Command(() => { if (Target?.Undo() != true) Stack?.Undo(); }, () => Target != null || Stack?.CanUndo == true);
            redo = new Command(() => { if (Target?.Redo() != true) Stack?.Redo(); }, () => Target != null || Stack?.CanRedo == true);
            transport = new TransportBar(host);

            var menu = BuildMenu();
            DockPanel.SetDock(menu, Dock.Top);
            DockPanel.SetDock(transport, Dock.Top);
            var statusBar = new Border { Child = status, BorderBrush = new SolidColorBrush(Color.FromRgb(0x30, 0x30, 0x30)), BorderThickness = new Thickness(0, 1, 0, 0) };
            DockPanel.SetDock(statusBar, Dock.Bottom);
            root.Children.Add(menu);
            root.Children.Add(transport);
            root.Children.Add(statusBar);
            root.Children.Add(startup);
            Content = root;
            transport.IsEnabled = false;

            AddKeys();
            AddHandler(KeyDownEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);
            AddHandler(KeyUpEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);
            AddHandler(PointerMovedEvent, (s, e) => StudioKeyboard.Held = e.KeyModifiers, handledEventsToo: true);

            refresh.Tick += (s, e) => Refresh();
            if (host.Core != null) OnStarted();
            else host.Started += OnStarted;
        }

        BuzzGUI.Common.ManagedActionStack Stack => host.Core?.SongCore?.ActionStack;

        /// <summary>The selected view, when it has edit commands of its own.</summary>
        IStudioEditTarget Target => (tabs.SelectedItem as TabItem)?.Content as IStudioEditTarget;

        /// <summary>The views' tabs, once the engine runs.</summary>
        public TabControl Tabs => tabs;

        public TransportBar Transport => transport;

        /// <summary>The status line's text now (for tests).</summary>
        public string StatusText => status.Text;

        /// <summary>A start-up step, shown until the views replace it.</summary>
        public void ShowStep(string step) => startup.Text = step;

        void OnStarted()
        {
            root.Children.Remove(startup);
            foreach (var view in StudioViews.All)
            {
                Avalonia.Controls.Control content;
                try
                {
                    content = view.Create(host);
                }
                catch (Exception e)
                {
                    host.Log($"The {view.Title} view failed: {e}");
                    content = new TextBlock { Text = $"{view.Title}: {e.Message}", Foreground = Brushes.IndianRed, Margin = new Thickness(12) };
                }
                tabs.Items.Add(new TabItem { Header = view.Title, Content = content });
            }
            tabs.SelectedIndex = 0;
            root.Children.Add(tabs);
            transport.IsEnabled = true;
            refresh.Start();
            Refresh();
        }

        Menu BuildMenu()
        {
            MenuItem Item(string header, Action run, KeyGesture key = null, Func<bool> can = null) =>
                new MenuItem { Header = header, Command = new Command(run, can), InputGesture = key };
            var cmd = CommandModifiers;
            return new Menu
            {
                Items =
                {
                    new MenuItem
                    {
                        Header = "_File",
                        Items =
                        {
                            Item("_New", () => _ = SongFiles.New(), new KeyGesture(Key.N, cmd)),
                            Item("_Open…", () => _ = SongFiles.Open(), new KeyGesture(Key.O, cmd)),
                            Item("_Save", () => _ = SongFiles.Save(), new KeyGesture(Key.S, cmd)),
                            Item("Save _as…", () => _ = SongFiles.SaveAs(), new KeyGesture(Key.S, cmd | KeyModifiers.Shift)),
                            new Separator(),
                            Item(OperatingSystem.IsMacOS() ? "_Quit" : "E_xit", Close, new KeyGesture(Key.Q, cmd)),
                        },
                    },
                    new MenuItem
                    {
                        Header = "_Edit",
                        Items =
                        {
                            new MenuItem { Header = "_Undo", Command = undo, InputGesture = new KeyGesture(Key.Z, cmd) },
                            new MenuItem { Header = "_Redo", Command = redo, InputGesture = OperatingSystem.IsMacOS() ? new KeyGesture(Key.Z, cmd | KeyModifiers.Shift) : new KeyGesture(Key.Y, cmd) },
                            new Separator(),
                            Item("Cu_t", () => Target?.Cut(), new KeyGesture(Key.X, cmd)),
                            Item("_Copy", () => Target?.Copy(), new KeyGesture(Key.C, cmd)),
                            Item("_Paste", () => Target?.Paste(), new KeyGesture(Key.V, cmd)),
                        },
                    },
                    new MenuItem
                    {
                        Header = "_Transport",
                        Items =
                        {
                            Item("_Play", () => transport.Play(), new KeyGesture(Key.F5)),
                            Item("_Stop", () => transport.Stop(), new KeyGesture(Key.F8)),
                            Item("_Record", () => transport.ToggleRecord(), new KeyGesture(Key.F7)),
                        },
                    },
                },
            };
        }

        /// <summary>Command on macOS, Control elsewhere.</summary>
        static KeyModifiers CommandModifiers => OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

        void AddKeys()
        {
            var cmd = CommandModifiers;
            void Bind(KeyGesture key, Action run) => KeyBindings.Add(new KeyBinding { Gesture = key, Command = new Command(run) });
            Bind(new KeyGesture(Key.N, cmd), () => _ = SongFiles.New());
            Bind(new KeyGesture(Key.O, cmd), () => _ = SongFiles.Open());
            Bind(new KeyGesture(Key.S, cmd), () => _ = SongFiles.Save());
            Bind(new KeyGesture(Key.S, cmd | KeyModifiers.Shift), () => _ = SongFiles.SaveAs());
            KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Z, cmd), Command = undo });
            KeyBindings.Add(new KeyBinding { Gesture = OperatingSystem.IsMacOS() ? new KeyGesture(Key.Z, cmd | KeyModifiers.Shift) : new KeyGesture(Key.Y, cmd), Command = redo });
            Bind(new KeyGesture(Key.F5), () => transport.Play());
            Bind(new KeyGesture(Key.F8), () => transport.Stop());
            Bind(new KeyGesture(Key.F7), () => transport.ToggleRecord());
        }

        void Refresh()
        {
            var core = host.Core;
            if (core == null) return;
            transport.Refresh();
            undo.Refresh();
            redo.Refresh();
            string name = core.SongCore.SongName == null ? "Untitled" : Path.GetFileName(core.SongCore.SongName);
            Title = $"{name}{(core.Modified ? " •" : "")} — ReBuzz";
            var device = core.AudioEngine?.SelectedOutDevice;
            string audio = device != null
                ? $"{device.Name}, {core.SelectedAudioDriverSampleRate} Hz, {core.AudioEngine.CurrentDriver?.BufferSize} frames"
                : "no audio output";
            status.Text = $"{audio} · dropouts {ReBuzzCore.DeadlineMisses} · {core.SongCore.MachinesList.Count} machines";
        }

        protected override void OnClosing(WindowClosingEventArgs e)
        {
            base.OnClosing(e);
            if (e.Cancel || host.Core == null || !host.Core.Modified || quitting) return;
            // Unsaved changes: ask first, then close for real.
            e.Cancel = true;
            Dispatcher.UIThread.Post(async () =>
            {
                if (await SongFiles.ConfirmDiscard())
                {
                    quitting = true;
                    Close();
                }
            });
        }

        bool quitting;

        protected override void OnClosed(EventArgs e)
        {
            refresh.Stop();
            base.OnClosed(e);
        }
    }
}
