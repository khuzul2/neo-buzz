// Fork (docs/specs/ux-overhaul.md P6c, §9 row 11): the on-screen piano keyboard of ReBuzz.exe's MIDI-focus toolbar (BuzzGUI.PianoKeyboard,
// IsPianoKeyboardVisible), as a tool window: View ▸ Piano Keyboard, and the "Keys" button at the end of the MIDI focus bar. It plays the
// machine that has the MIDI focus, with the keyboard every machine window has under its content (MachineKeyboard: the octave, the keys
// as many as fit, a strike's height as its velocity, lit keys for every note the machine plays, each key named for screen readers); the
// notes go the way that machine takes them (MIDI, or its note tracks: KeyboardRoute). It follows the focus: choosing another machine in
// the bar re-points the keyboard, and a machine that plays no notes says so. The WPF window's pitch and mod wheels, aftertouch and
// channel choice are not repeated (a machine's own keyboard has no wheels either); the computer keys play it as in a machine window.

using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Keyboards;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class PianoKeyboardWindow : ToolWindow
    {
        public const string PlacementName = "PianoKeyboard";
        static PianoKeyboardWindow current;

        readonly TextBlock heading = Label("", size: StudioTokens.FontBody);
        readonly Border body = new Border();
        IMachine playing;

        PianoKeyboardWindow(StudioHost host) : base(host, "Piano Keyboard", PlacementName, 760, 200)
        {
            AutomationProperties.SetName(this, "Piano Keyboard");
            MinWidth = 420;
            SizeToContent = SizeToContent.Height;
            heading.Margin = new Thickness(12, 8, 12, 4);
            var dock = new DockPanel();
            DockPanel.SetDock(heading, Dock.Top);
            dock.Children.Add(heading);
            dock.Children.Add(body);
            Content = dock;
            Closed += (s, e) => { if (current == this) current = null; };
        }

        /// <summary>Opens the keyboard (or brings it to the front).</summary>
        public static PianoKeyboardWindow Open(StudioHost host, Window owner = null)
        {
            if (current != null)
            {
                current.Activate();
                return current;
            }
            current = new PianoKeyboardWindow(host);
            ShowOver(current, owner);
            return current;
        }

        internal static PianoKeyboardWindow Current => current;

        /// <summary>The machine the keyboard plays now (tests), or null.</summary>
        internal IMachine Playing => playing;

        /// <summary>What the heading says (tests).</summary>
        internal string Heading => heading.Text;

        /// <summary>The keyboard under the heading (tests), or null when the focus machine plays no notes.</summary>
        internal NeoBuzz.Editor.MachineKeyboard Keyboard => NeoBuzz.Editor.MachineKeyboardFrame.Of(body.Child);

        protected override void Attach()
        {
            if (Core != null) Core.PropertyChanged += OnCoreChanged;
            Rebuild();
        }

        protected override void Detach()
        {
            if (Core != null) Core.PropertyChanged -= OnCoreChanged;
            body.Child = null;
            playing = null;
        }

        void OnCoreChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == "MIDIFocusMachine" || e.PropertyName == nameof(ReBuzzCore.SongCore) || e.PropertyName == "Song")
                Avalonia.Threading.Dispatcher.UIThread.Post(Rebuild);
        }

        /// <summary>Points the keyboard at the focus machine now.</summary>
        internal void Rebuild()
        {
            var machine = Core?.MIDIFocusMachine as MachineCore;
            if (machine != null && machine.Hidden) machine = null;
            if (machine == playing && (machine == null || body.Child != null)) return;
            playing = machine;
            body.Child = null;
            if (machine == null)
            {
                heading.Text = "No machine has the MIDI focus: choose one in the MIDI bar.";
                return;
            }
            if (!KeyboardRoute.For(machine).Shows)
            {
                heading.Text = machine.Name + " plays no notes.";
                return;
            }
            heading.Text = "Playing " + machine.Name;
            // The same keyboard a machine window has; here it is always open.
            var frame = NeoBuzz.Editor.MachineKeyboardFrame.Wrap(machine, new Border { Height = 1 });
            body.Child = frame;
            NeoBuzz.Editor.MachineKeyboardFrame.Of(frame)?.SetShown(true);
        }
    }
}
