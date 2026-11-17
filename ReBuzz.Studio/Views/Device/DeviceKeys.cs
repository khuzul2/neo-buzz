// Fork (docs/specs/ux-overhaul.md, the parameter panel): the computer's keys play the machine, as the retired parameter window's did.
// The same toggle ("Keyboard Sends MIDI Notes") and the same setting ("KeyboardMIDI" in the "Parameter Window" section, off by default).
// Z S X D C V G B H N J M, then Q 2 W 3 E R 5 T 6 Y 7 U I 9 O 0 P send MIDI note on and off to the machine at its base octave; * and /
// move the octave. A window of its own listens window-wide; the docked panel only while it holds the keyboard focus (the piano roll
// and the pattern editor use the same keys).

using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal sealed class DeviceKeys
    {
        public const string Key = "KeyboardMIDI", Section = "Parameter Window";

        /// <summary>The old window's one setting: the keys send MIDI notes.</summary>
        public static bool Enabled
        {
            get
            {
                try { return StudioHost.Current?.Registry?.Read(Key, false, Section) ?? false; }
                catch (Exception) { return false; }
            }
            set
            {
                try { StudioHost.Current?.Registry?.Write(Key, value, Section); }
                catch (Exception) { /* a setting that can't be saved is not worth a failed click */ }
                EnabledChanged?.Invoke();
            }
        }

        /// <summary>The setting changed (the panel bar's toggle and the Presets menu show the same state).</summary>
        public static event Action EnabledChanged;

        public const string Tip = "The computer's keys play the machine while this panel has the keyboard focus (the old window's setting)";

        readonly IMachine machine;
        readonly Control owner;
        readonly bool windowWide;
        readonly Dictionary<int, int> down = new();
        TopLevel top;

        public DeviceKeys(IMachine machine, Control owner, bool windowWide)
        {
            this.machine = machine;
            this.owner = owner;
            this.windowWide = windowWide;
        }

        ReBuzzCore Core => DeviceEdits.CoreOf(machine);

        /// <summary>The notes held now (tests).</summary>
        public int Held => down.Count;

        public void Attach()
        {
            top = TopLevel.GetTopLevel(owner);
            if (top == null) return;
            top.AddHandler(InputElement.KeyDownEvent, KeyDown, RoutingStrategies.Tunnel, handledEventsToo: false);
            top.AddHandler(InputElement.KeyUpEvent, KeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
            top.AddHandler(InputElement.TextInputEvent, TextInput);
            if (top is Window w) { w.Activated += Activated; w.Deactivated += Deactivated; }
        }

        public void Detach()
        {
            NotesOff();
            if (top == null) return;
            top.RemoveHandler(InputElement.KeyDownEvent, KeyDown);
            top.RemoveHandler(InputElement.KeyUpEvent, KeyUp);
            top.RemoveHandler(InputElement.TextInputEvent, TextInput);
            if (top is Window w) { w.Activated -= Activated; w.Deactivated -= Deactivated; }
            top = null;
        }

        bool Listening()
        {
            if (!Enabled || machine.DLL?.Info == null) return false;
            if (top?.FocusManager?.GetFocusedElement() is TextBox) return false;
            return windowWide || owner.IsKeyboardFocusWithin;
        }

        void KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled || e.KeyModifiers != KeyModifiers.None || machine.DLL.Info.Type != MachineType.Generator || !Listening()) return;
            int i = PianoKeys.IndexOf(e.PhysicalKey);
            if (i < 0) return;
            e.Handled = true;
            if (down.ContainsKey(i)) return;
            int note = 12 * machine.BaseOctave + i;
            if (Core is { } core)
            {
                core.MIDIFocusMachine = machine;
                core.SendMIDIInput(MIDI.Encode(MIDI.NoteOn, note, 100));
            }
            down[i] = note;
        }

        void KeyUp(object sender, KeyEventArgs e)
        {
            int i = PianoKeys.IndexOf(e.PhysicalKey);
            if (i < 0 || !down.TryGetValue(i, out int note)) return;
            down.Remove(i);
            Core?.SendMIDIInput(MIDI.Encode(MIDI.NoteOff, note, 0));
        }

        void TextInput(object sender, TextInputEventArgs e)
        {
            if (!Listening() || machine.DLL.Info.Type != MachineType.Generator) return;
            if (e.Text == "*" && machine.BaseOctave < 9) { machine.BaseOctave++; e.Handled = true; }
            else if (e.Text == "/" && machine.BaseOctave > 0) { machine.BaseOctave--; e.Handled = true; }
        }

        void Activated(object sender, EventArgs e)
        {
            if (Core is { } core && (machine.DLL.Info.Type == MachineType.Generator || machine.IsControlMachine)) core.MIDIFocusMachine = machine;
        }

        void Deactivated(object sender, EventArgs e) => NotesOff();

        public void NotesOff()
        {
            foreach (var note in down.Values) Core?.SendMIDIInput(MIDI.Encode(MIDI.NoteOff, note, 0));
            down.Clear();
        }
    }
}
