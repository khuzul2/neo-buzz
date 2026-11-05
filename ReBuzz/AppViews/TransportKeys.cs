using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork: Space plays and stops the song in every view of the main window, as in most DAWs (F5 and
    /// F8 still do, as in Buzz). Shift+Space in the pattern view plays the pattern editor's pattern on
    /// its own from the editor's cursor, and stops it, as upstream's Shift+Space did; upstream's plain
    /// Space there (the pattern solo) is the transport's now. One command for both apps
    /// (Core/TransportCommands.cs); the studio's main window has the same keys.
    ///
    /// The main window's PreviewKeyDown asks here first, before the docked Session View and the
    /// focused control, so a focused button or check box doesn't also click, except where Space is
    /// typed or chooses: a text box (Song Info, the machine list's search, the Session View's clip
    /// boxes, an editable drop-down's box), a password box, an open drop-down and a menu.
    /// </summary>
    internal static class TransportKeys
    {
        internal enum Command
        {
            /// <summary>Not the transport's: the key goes on to the views and the focused control.</summary>
            None,
            /// <summary>Space held: the transport's, done already on the first press.</summary>
            Repeat,
            TogglePlaying,
            PatternPreview,
        }

        /// <summary>What a key pressed in the main window does with the transport.</summary>
        internal static Command For(Key key, ModifierKeys modifiers, bool isRepeat, bool patternView, DependencyObject focused)
        {
            if (key != Key.Space) return Command.None;
            bool shift = modifiers == ModifierKeys.Shift;
            if ((modifiers != ModifierKeys.None && !shift) || (shift && !patternView) || KeepsSpace(focused)) return Command.None;
            if (isRepeat) return Command.Repeat;
            return shift ? Command.PatternPreview : Command.TogglePlaying;
        }

        /// <summary>
        /// Whether Space goes to the focused element (a key event's original source): a text box or
        /// rich text box that can be typed in (also an editable drop-down's), a password box, an open
        /// drop-down, a menu.
        /// </summary>
        internal static bool KeepsSpace(DependencyObject focused)
        {
            int steps = 0;
            for (var d = focused; d != null && steps++ < 256; d = Up(d))
            {
                switch (d)
                {
                    case TextBoxBase box: return !box.IsReadOnly;
                    case PasswordBox:
                    case MenuItem:
                        return true;
                    case ComboBox combo when combo.IsEditable || combo.IsDropDownOpen:
                        return true;
                    case ComboBoxItem item when ItemsControl.ItemsControlFromItemContainer(item) is ComboBox { IsDropDownOpen: true }:
                        return true;
                }
            }
            return false;
        }

        // Up the visual tree, and the logical one where it ends (a content element). From a popup's content
        // (an open drop-down's, a menu's: a window of its own) on to its Popup, in the owner's template.
        static DependencyObject Up(DependencyObject d)
        {
            if (d is FrameworkElement { Parent: Popup popup }) return popup;
            return (d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
        }
    }
}
