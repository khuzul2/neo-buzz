// Fork (docs/specs/ux-overhaul.md P6b, §9): the MIDI-focus functions of ReBuzz.exe's toolbar (BuzzGUI.ToolBar), at the right of the status
// line: a light that flashes on MIDI input, the machine that receives MIDI notes and controllers (a menu of the song's machines), and
// the lock that keeps it there while another machine is selected (otherwise selecting a machine moves the focus, as before).
// Choosing with the lock on moves the focus and keeps the lock, as the toolbar does.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class MidiFocusBar : StackPanel
    {
        readonly Func<ReBuzzCore> core;
        readonly Ellipse light = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
        internal readonly ComboBox Machines = new ComboBox { MinWidth = 130, MaxWidth = 200, FontSize = 12, Padding = new Thickness(6, 0), MinHeight = 22, VerticalAlignment = VerticalAlignment.Center };
        internal readonly ToggleButton Lock = new ToggleButton { Content = "Lock" };
        List<IMachine> listed = new List<IMachine>();
        bool updating;

        public MidiFocusBar(Func<ReBuzzCore> core)
        {
            this.core = core;
            Orientation = Orientation.Horizontal;
            Spacing = 6;
            Margin = new Thickness(8, 1);
            var label = new TextBlock { Text = "MIDI", FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
            Children.Add(light);
            Children.Add(label);
            Children.Add(Machines);
            Children.Add(Lock);
            AutomationProperties.SetName(Machines, "MIDI focus machine");
            AutomationProperties.SetName(Lock, "Lock the MIDI focus");
            AutomationProperties.SetName(light, "MIDI activity");
            ToolTip.SetTip(Machines, "The machine that receives MIDI notes and controllers");
            ToolTip.SetTip(Lock, "Keep the MIDI focus on this machine while you select others");
            ToolTip.SetTip(light, "Lights when MIDI arrives");
            Machines.SelectionChanged += (s, e) =>
            {
                if (updating) return;
                var c = core();
                int i = Machines.SelectedIndex;
                if (c != null) Choose(c, i >= 0 && i < listed.Count ? listed[i] : null);
            };
            Lock.Click += (s, e) => { var c = core(); if (c != null) c.MIDIFocusLocked = Lock.IsChecked == true; };
            Refresh();
        }

        /// <summary>The toolbar's rule: with the lock on, the focus moves and the lock stays.</summary>
        internal static void Choose(ReBuzzCore c, IMachine machine)
        {
            if (c.MIDIFocusLocked)
            {
                c.MIDIFocusLocked = false;
                c.MIDIFocusMachine = machine;
                c.MIDIFocusLocked = true;
            }
            else c.MIDIFocusMachine = machine;
        }

        internal bool Active { get; private set; }

        /// <summary>The names shown, in order (tests).</summary>
        internal IReadOnlyList<string> Names => listed.Select(m => m.Name).ToList();

        /// <summary>From the engine: the machines, the focus, the lock, the light.</summary>
        public void Refresh()
        {
            var c = core();
            IsVisible = c?.SongCore != null;
            if (c?.SongCore == null) return;
            var machines = c.SongCore.MachinesList.Where(m => !m.Hidden && m.DLL.Info.Type != MachineType.Master).OrderBy(m => m.Name, StringComparer.OrdinalIgnoreCase).Cast<IMachine>().ToList();
            updating = true;
            try
            {
                if (!machines.SequenceEqual(listed) || !Machines.Items.Cast<object>().SequenceEqual(machines.Select(m => (object)m.Name)))
                {
                    listed = machines;
                    Machines.ItemsSource = machines.Select(m => m.Name).ToList();
                }
                int at = listed.IndexOf(c.MIDIFocusMachine);
                if (Machines.SelectedIndex != at) Machines.SelectedIndex = at;
                if (Lock.IsChecked != c.MIDIFocusLocked) Lock.IsChecked = c.MIDIFocusLocked;
            }
            finally { updating = false; }
            Active = c.MIDIActivity;
            light.Fill = Active ? StudioTokens.PlayBrush : StudioTokens.ControlTrackBrush;
        }
    }
}
