// Fork (docs/specs/ux-overhaul.md P3e, decisions 62-70): Detail ▸ Device. It shows the selected machine's device page, with the piano
// keyboard under it for a machine that plays notes, and follows the selection; Float moves the page to a window of its own (one per
// machine) and the dock says so until the window closes or Dock is pressed.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Platform;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Device
{
    internal sealed class DeviceDock : Border
    {
        static readonly Dictionary<IMachine, Window> floating = new();

        readonly ReBuzzCore core;
        readonly StudioSelection selection;
        readonly Shell.EmptyState hint;
        readonly Action<IMachine> followFloat;
        IMachine shown;
        DevicePage page;
        bool attached;

        public DeviceDock(ReBuzzCore core)
        {
            this.core = core;
            selection = StudioSelection.For(core);
            Background = StudioTokens.SurfacePanelBrush;
            hint = new Shell.EmptyState(core);
            followFloat = m => { if (attached && m == shown) { shown = null; Follow(); } };
            Avalonia.Automation.AutomationProperties.SetName(this, "Device");
        }

        /// <summary>The device page of the selected machine, when it is docked here.</summary>
        public DevicePage Page => page;

        public IMachine Machine => shown;

        /// <summary>The empty state shown while no machine is selected (P3i).</summary>
        internal Shell.EmptyState Empty => hint;

        /// <summary>The page, with the machine's keyboard under it, or null.</summary>
        public Avalonia.Controls.Control Frame => Child as Avalonia.Controls.Control;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (attached) return;
            attached = true;
            selection.Changed += SelectionChanged;
            FloatChanged += followFloat;
            Follow();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            if (!attached) return;
            attached = false;
            selection.Changed -= SelectionChanged;
            FloatChanged -= followFloat;
            Clear();
        }

        void SelectionChanged(object source) => Follow();

        /// <summary>Shows the selected machine (called again when something that changes what shows has happened).</summary>
        public void Follow()
        {
            var m = selection.Machine;
            if (m == shown && Child != null) return;
            Clear();
            shown = m;
            if (m == null)
            {
                bool bare = Shell.EmptyState.SongIsEmpty(core);
                hint.Update(true, bare ? "none:bare" : "none", "No machine selected",
                    bare ? "The device view shows a machine's parameters. Start with a sound, or search every machine."
                         : "Select a machine in any view to see its parameters here, grouped and searchable.",
                    Shell.EmptyState.AddMachineStep(), bare);
                Child = hint;
                return;
            }
            if (floating.TryGetValue(m, out var w) && w.IsVisible)
            {
                var b = new Button { Content = "Dock" };
                b.Click += (s, e) => Dock(m);
                Child = new StackPanel { Margin = new Thickness(16), Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left, Children = { new TextBlock { Text = m.Name + " is floating in its own window.", FontSize = StudioTokens.FontBody, Foreground = StudioTokens.TextSecondaryBrush }, b } };
                return;
            }
            page = new DevicePage(m, floating: false);
            page.FloatToggled += () => Float(m);
            Child = Framed(m, page);
        }

        /// <summary>The settings key of the device view's keyboard (hidden unless the person opened it; one choice for the docked page and the windows).</summary>
        public const string KeyboardKey = "DeviceKeyboard";

        /// <summary>The page with the machine's keyboard under it, collapsed at first and remembering its state apart from the machine windows'.</summary>
        static Avalonia.Controls.Control Framed(IMachine m, DevicePage content)
        {
            var frame = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(m, content, scroll: false);
            NeoBuzz.Editor.MachineKeyboardFrame.Of(frame)?.UseOwnState(DevicePage.Read(KeyboardKey, false), on => DevicePage.Write(KeyboardKey, on));
            return (Avalonia.Controls.Control)frame;
        }

        void Clear()
        {
            shown = null;
            page = null;
            Child = null;
        }

        /// <summary>A machine's device moved into a window or back (the docks showing it follow).</summary>
        static event Action<IMachine> FloatChanged;

        /// <summary>Moves a machine's device page into a window of its own.</summary>
        public void Float(IMachine m) => OpenFloating(m, null);

        /// <summary>
        /// A machine's device page in a window of its own (one per machine), at the placement a song kept where there is one. This is what
        /// "Parameters...", a double click on a machine with no editor and the song's saved parameter windows open: the retired
        /// parameter window's place.
        /// </summary>
        public static void OpenFloating(IMachine m, WindowPlacement? at)
        {
            if (m == null) return;
            if (!floating.TryGetValue(m, out var w) || !w.IsVisible)
            {
                var content = new DevicePage(m, floating: true);
                w = new Window
                {
                    Title = m.Name + " — device",
                    Content = Framed(m, content),
                    Width = 720,
                    Height = 620,
                    ShowInTaskbar = false,
                };
                var window = w;
                content.FloatToggled += () => window.Close();
                _ = new TransportKeys(w, () => m.Graph?.Buzz as ReBuzzCore, () => false);
                if (at is WindowPlacement p && p.Width > 0)
                {
                    w.Position = new PixelPoint((int)p.Left, (int)p.Top);
                    w.Width = p.Width;
                    w.Height = Math.Max(240, p.Height + NeoBuzz.Editor.MachineKeyboardFrame.ExtraHeightOf(w.Content));
                }
                MachineWindowFit.Clamp(w);
                w.Closed += (s, e) =>
                {
                    floating.Remove(m);
                    FloatChanged?.Invoke(m);
                };
                floating[m] = w;
                var owner = Dialogs.Owner;
                if (owner != null) w.Show(owner); else w.Show();
                FloatChanged?.Invoke(m);
            }
            w.Activate();
        }

        /// <summary>Brings a floating device back into the panel.</summary>
        public void Dock(IMachine m)
        {
            if (m != null && floating.TryGetValue(m, out var w)) w.Close();
        }

        public static bool IsFloating(IMachine m) => m != null && floating.TryGetValue(m, out var w) && w.IsVisible;

        public static Window FloatingWindowOf(IMachine m) => m != null && floating.TryGetValue(m, out var w) ? w : null;
    }
}
