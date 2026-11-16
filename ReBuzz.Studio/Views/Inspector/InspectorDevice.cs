// Fork (docs/specs/ux-overhaul.md, the parameter panel): what the Inspector shows of the selected machine's parameters: its macros (up to
// eight, from the manifest's roles) and the parameters pinned to it (up to eight, pinned from a control's right-click menu in the Device
// panel). The same knobs as the Device panel's cards.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views.Device;

namespace ReBuzz.Studio.Views.Inspector
{
    using Control = Avalonia.Controls.Control;

    internal sealed class InspectorDevice : StackPanel
    {
        readonly ReBuzzCore core;
        readonly WrapPanel macros = new() { Orientation = Orientation.Horizontal };
        readonly WrapPanel pinned = new() { Orientation = Orientation.Horizontal };
        readonly TextBlock macroTitle, pinTitle, pinHint;
        readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromMilliseconds(100) };
        readonly List<DeviceCell> cells = new();
        IMachine machine;
        DeviceLayout layout;

        public InspectorDevice(ReBuzzCore core)
        {
            this.core = core;
            Margin = new Thickness(0, 10, 0, 0);
            macroTitle = Title("MACROS");
            pinTitle = Title("PINNED");
            pinHint = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Text = "Right-click a parameter in the Device panel and choose Pin to Inspector." };
            Children.Add(macroTitle);
            Children.Add(macros);
            Children.Add(pinTitle);
            Children.Add(pinned);
            Children.Add(pinHint);
            IsVisible = false;
            Avalonia.Automation.AutomationProperties.SetName(this, "Machine parameters");
            poll.Tick += (s, e) => { foreach (var c in cells) if (!c.Busy) c.Sync(); };
        }

        static TextBlock Title(string text) => new() { Text = text, FontSize = 10.5, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 6, 0, 4) };

        public IReadOnlyList<DeviceCell> MacroCells => macros.Children.OfType<DeviceCell>().ToList();

        public IReadOnlyList<DeviceCell> PinnedCells => pinned.Children.OfType<DeviceCell>().ToList();

        public WrapPanel MacroPanel => macros;

        public WrapPanel PinnedPanel => pinned;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            poll.Start();
            if (core.PinStore != null) core.PinStore.Changed += Rebuild;
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            poll.Stop();
            if (core.PinStore != null) core.PinStore.Changed -= Rebuild;
        }

        /// <summary>Shows a machine's macros and pins (null: nothing).</summary>
        public void Show(IMachine m)
        {
            if (m != machine) layout = m == null ? null : DeviceLayout.Build(m);
            machine = m;
            Rebuild();
        }

        void Rebuild()
        {
            if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Rebuild); return; }
            macros.Children.Clear();
            pinned.Children.Clear();
            cells.Clear();
            IsVisible = machine != null && layout != null;
            if (!IsVisible) return;
            var category = MachineIdentity.CategoryOf(machine) ?? NeoBuzz.Editor.MachineCategory.Synth;
            var accent = NeoBuzz.Editor.Tokens.Identity(category);
            DeviceCell Make(DeviceParam p)
            {
                DeviceCell cell = null;
                cell = new DeviceCell(machine, p, core, accent, c => DeviceMenus.ForParam(machine, p, c.BeginType, Rebuild).Open(c), null);
                cells.Add(cell);
                return cell;
            }
            foreach (var p in layout.Macros) macros.Children.Add(Make(p));
            macroTitle.IsVisible = macros.IsVisible = layout.Macros.Count > 0;
            var names = core.PinStore?.Get(machine.Name) ?? Array.Empty<string>();
            foreach (var n in names)
                if (layout.All.FirstOrDefault(p => p.CanPin && p.Parameter.Name == n) is { } dp)
                    pinned.Children.Add(Make(dp));
            pinTitle.IsVisible = pinned.IsVisible = pinned.Children.Count > 0;
            pinHint.IsVisible = pinned.Children.Count == 0;
        }
    }
}
