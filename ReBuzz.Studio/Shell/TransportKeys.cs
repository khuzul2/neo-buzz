// Fork: Space plays and stops the song in every view of the main window, as in most DAWs (F5 and F8
// still do, as in Buzz; on a Mac keyboard they need fn). Shift+Space on the Pattern tab plays the
// pattern editor's pattern on its own from the editor's cursor, and stops it. One command for both
// apps (ReBuzz/Core/TransportCommands.cs); ReBuzz.exe's main window has the same keys.
//
// The window takes Space before the focused control does (a tunnel handler), so a focused button or
// check box doesn't also click, except where Space is typed or chooses: a text box (the Song Notes
// editor, the transport's BPM box, the pattern editor's number boxes, the Session View's clip boxes,
// an editable drop-down) and an open drop-down or menu. Space isn't a key equivalent in the macOS
// menu bar: there it would reach the menu before any text box.

using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ReBuzz.Core;

namespace ReBuzz.Studio.Shell
{
    internal sealed class TransportKeys
    {
        readonly Func<ReBuzzCore> core;
        readonly Func<bool> patternShown;
        bool taken;     // the Space held now was the transport's: its repeats and its release are too

        /// <param name="core">The engine, or null while the window has no views yet.</param>
        /// <param name="patternShown">Whether the Pattern tab is the one shown (Shift+Space).</param>
        public TransportKeys(Window window, Func<ReBuzzCore> core, Func<bool> patternShown)
        {
            this.core = core;
            this.patternShown = patternShown;
            window.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            window.AddHandler(InputElement.KeyUpEvent, OnKeyUp, RoutingStrategies.Tunnel, handledEventsToo: true);
            window.Deactivated += (s, e) => taken = false;      // released in another window
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || e.Handled) return;
            if (taken)
            {
                e.Handled = true;           // held: Avalonia doesn't flag repeats, and one press is one toggle
                return;
            }
            bool shift = e.KeyModifiers == KeyModifiers.Shift;
            if ((e.KeyModifiers != KeyModifiers.None && !shift) || KeepsSpace(e.Source)) return;
            var c = core();
            if (c == null) return;
            if (shift)
            {
                if (!patternShown()) return;
                TransportCommands.TogglePatternPreview(c);
            }
            else
            {
                TransportCommands.TogglePlaying(c);
            }
            taken = true;
            e.Handled = true;
        }

        void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Space || !taken) return;
            taken = false;
            e.Handled = true;               // a focused button clicks when Space is released
        }

        /// <summary>
        /// Whether Space goes to the focused element (a key event's source) and not to the transport:
        /// a text box that can be typed in (also the one in a number box or an editable drop-down), an
        /// open drop-down, a menu.
        /// </summary>
        internal static bool KeepsSpace(object focused)
        {
            int steps = 0;
            for (var e = focused as StyledElement; e != null && steps++ < 256; e = Up(e))
            {
                switch (e)
                {
                    case TextBox box: return !box.IsReadOnly;
                    case NumericUpDown:
                    case AutoCompleteBox:
                    case MenuItem:
                        return true;
                    case ComboBox combo when combo.IsEditable || combo.IsDropDownOpen:
                        return true;
                }
            }
            return false;
        }

        // Up the visual tree to the window. From a popup's content (an open drop-down's, a menu's, shown in a
        // window of its own or a layer over the window) on to its Popup, in the owner's template. Not above a
        // window: its host's logical parent is the window again (Avalonia 12), a loop.
        static StyledElement Up(StyledElement e)
        {
            if (e.Parent is Popup popup) return popup;
            if (e is TopLevel) return null;
            return (e as Visual)?.GetVisualParent() as StyledElement ?? e.Parent;
        }
    }
}
