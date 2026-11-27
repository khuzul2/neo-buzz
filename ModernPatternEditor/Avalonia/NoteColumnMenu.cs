// Fork (docs/specs/notes.md section 9, CU6b): the column header's menu for chords. A right click on a column header of the tracker opens it:
// Add Note Column puts one more note+velocity pair beside the others, Remove Note Column takes the last one off (refused with the reason in the
// status bar while it would hide a note). The pairs are the Notes lane's view (a note sits on the lowest pair free at its time), the count is
// saved with the clip and each change is one undo step. The same two commands are on Alt++ and Alt+- and in the grid's menu.

using Avalonia.Controls;

namespace WDE.ModernPatternEditor.AvaloniaView
{
    internal static class NoteColumnMenu
    {
        /// <summary>The menu for a header, as it would open now (items are enabled by what the clip can do).</summary>
        public static ContextMenu Create(ClipControl c)
        {
            const string mod = "Alt";
            int shown = c.CurrentClip?.NoteColumnCount ?? 0;
            var add = new MenuItem { Header = "Add Note Column    " + mod + "++", IsEnabled = c.AddNoteColumnCommand.CanExecute(null) };
            add.Click += (s, e) => c.AddNoteColumnCommand.Execute(null);
            var remove = new MenuItem { Header = "Remove Note Column    " + mod + "+-", IsEnabled = c.RemoveNoteColumnCommand.CanExecute(null) && shown > 1 };
            remove.Click += (s, e) => c.RemoveNoteColumnCommand.Execute(null);
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = shown == 0 ? "No note columns shown" : shown == 1 ? "1 note column" : shown + " note columns", IsEnabled = false });
            menu.Items.Add(add);
            menu.Items.Add(remove);
            return menu;
        }
    }
}
