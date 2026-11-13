// Fork (docs/specs/ux-overhaul.md §4): the studio's one keymap. The menus (both menu bars), the shortcuts window and the
// tooltips are made from it, so a key is written once: the app's commands (with their menu, title and keys, the first the
// one shown in menus, the others alternates such as Buzz's F-keys), and each view's own keys (the tracker's note entry,
// the Session View's scene digits…), which the views handle themselves and the shortcuts window lists by view.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace ReBuzz.Studio.Shell.Commands
{
    /// <summary>One of the app's commands: where it is, what it's called, its keys, and a line of help.</summary>
    internal sealed class CommandInfo
    {
        public string Id { get; init; }
        public string Menu { get; init; }
        /// <summary>Its menu text, with the in-window menu's access key ("Cu_t").</summary>
        public string Title { get; init; }

        /// <summary>Its text without the access key ("Cut").</summary>
        public string Text => Title?.Replace("_", "");

        /// <summary>Its keys on Windows and Linux ("Cmd+D|F7"): the first is shown in menus, the others are alternates.</summary>
        public string Keys { get; init; }

        /// <summary>Its keys on a Mac, when they differ.</summary>
        public string MacKeys { get; init; }

        /// <summary>
        /// Whether the first key is a macOS menu-bar key equivalent. Off for keys a view or a text box also has (Space, ⌫,
        /// the single-key transport keys): the menu bar would take them first; the item's text names them instead.
        /// </summary>
        public bool Native { get; init; } = true;

        /// <summary>Where a key that isn't everywhere acts ("Machines, Session and Song Notes").</summary>
        public string Where { get; init; }

        public string Help { get; init; }

        public string KeysFor(bool mac) => mac && MacKeys != null ? MacKeys : Keys;

        /// <summary>The key a menu shows (the first), or null.</summary>
        public KeyGesture Gesture(bool? mac = null) => KeyText.Parse(KeyText.Split(KeysFor(mac ?? KeyText.IsMac)).FirstOrDefault(), mac);

        /// <summary>Every key, the first one first.</summary>
        public IEnumerable<KeyGesture> Gestures(bool? mac = null) => KeyText.Split(KeysFor(mac ?? KeyText.IsMac)).Select(k => KeyText.Parse(k, mac));

        /// <summary>The keys as the user reads them: "R or F7".</summary>
        public string KeyLabel(bool? mac = null) => KeyText.Of(KeysFor(mac ?? KeyText.IsMac), mac);

        /// <summary>A tooltip: "Record (R or F7)", and the help line under it.</summary>
        public string Tip(string extra = null)
        {
            string keys = KeyLabel();
            string tip = keys.Length > 0 ? $"{Text.TrimEnd('…')} ({keys})" : Text.TrimEnd('…');
            if (!string.IsNullOrEmpty(Help)) tip += "\n" + Help;
            if (!string.IsNullOrEmpty(extra)) tip += "\n" + extra;
            return tip;
        }
    }

    /// <summary>A view's own key, for the shortcuts window: "Machines", "M", "mute the selected machines".</summary>
    internal sealed record ViewKey(string Section, string Keys, string What);

    internal static class StudioKeymap
    {
        public const string SingleKeyViews = "Machines (letters there search the machine browser when no machine is selected: use the F-key or the button), Session and Song Notes (the Sequence and the Pattern editor type letters: use the F-key or the button there)";

        static CommandInfo C(string id, string menu, string title, string keys = null, string mac = null, bool native = true, string where = null, string help = null) =>
            new CommandInfo { Id = id, Menu = menu, Title = title, Keys = keys, MacKeys = mac, Native = native, Where = where, Help = help };

        /// <summary>The app's commands, in menu order.</summary>
        public static IReadOnlyList<CommandInfo> Commands { get; } = new[]
        {
            C("file.new", "File", "_New", "Cmd+N"),
            C("file.open", "File", "_Open…", "Cmd+O"),
            C("file.close", "File", "_Close Window", "Cmd+W", help: "Closes the window in front; on the main window, closes the song (it asks to save first)"),
            C("file.save", "File", "_Save", "Cmd+S"),
            C("file.saveas", "File", "Save _As…", "Shift+Cmd+S"),
            C("file.revert", "File", "Re_vert to Saved", help: "Opens the saved song again, after asking: the changes since are lost"),
            C("file.exit", "File", "E_xit", "Cmd+Q"),

            C("edit.undo", "Edit", "_Undo", "Cmd+Z", help: "The selected view's last edit first, then the song's"),
            C("edit.redo", "Edit", "_Redo", "Cmd+Y", "Shift+Cmd+Z"),
            C("edit.cut", "Edit", "Cu_t", "Cmd+X"),
            C("edit.copy", "Edit", "_Copy", "Cmd+C"),
            C("edit.paste", "Edit", "_Paste", "Cmd+V"),
            C("edit.duplicate", "Edit", "_Duplicate", "Cmd+D", help: "Machines, arrangement blocks and clips, placed right after themselves"),
            C("edit.delete", "Edit", "De_lete", "Delete|Back", "Back|Delete", native: false, help: "The selection; in the Session View the clip leaves its slot (with Shift its pattern goes)"),
            C("edit.selectall", "Edit", "Select _All", "Cmd+A"),
            C("edit.deselect", "Edit", "Deselect All", "Shift+Cmd+A|Esc"),
            C("edit.rename", "Edit", "Re_name…", "Cmd+R", help: "The selected machine, track or clip"),
            C("edit.preferences", "Edit", "Pr_eferences…", "Cmd+Comma"),

            C("transport.playstop", "Transport", "Play or St_op", "Space", native: false, help: "In every view, not while typing in a box"),
            C("transport.play", "Transport", "_Play", "F5"),
            C("transport.stop", "Transport", "_Stop", "F8", help: "Stop again to silence everything"),
            C("transport.record", "Transport", "_Record", "R|F7", native: false, where: SingleKeyViews),
            C("transport.loop", "Transport", "_Loop", "C", native: false, where: SingleKeyViews),
            C("transport.metronome", "Transport", "_Metronome", "K", native: false, where: SingleKeyViews, help: "A click on every beat, heard and never recorded"),
            C("transport.start", "Transport", "_Go to Start", "Home", native: false, where: SingleKeyViews),
            C("transport.restartaudio", "Transport", "Restart _Audio", "F12", help: "Opens the audio device again"),

            C("view.sequence", "View", "_Arrangement", "Cmd+1|F4", help: "The arrangement"),
            C("view.session", "View", "S_ession", "Cmd+2|Ctrl+Tab", help: "Ctrl+Tab again: back to the arrangement"),
            C("view.machines", "View", "_Machines", "Cmd+3|F3"),
            C("view.pattern", "View", "_Tracker", "Cmd+4", help: "The detail panel's Tracker, docked under the view"),
            C("view.maximise", "View", "_Maximise Tracker", "Shift+Cmd+F|F2", help: "The Tracker over the whole window, and back (F2 renames a clip in the Session View)"),
            C("view.notes", "View", "Song _Notes", "Cmd+5|F10", help: "The inspector's Song tab"),
            C("view.inspector", "View", "_Inspector", "Ctrl+Alt+I", "Alt+Cmd+I", help: "Shows or hides the panel on the right: the selected machine, pattern or clip, and the song"),
            C("view.browser", "View", "_Browser", "Ctrl+Alt+B", "Alt+Cmd+B", help: "Shows or hides the panel on the left: the machines to add, and the patterns of the selection"),
            C("view.detail", "View", "_Detail", "Ctrl+Alt+D", "Alt+Cmd+D", help: "Shows or hides the panel under the view: the Tracker and the piano roll"),
            C("view.pianoroll", "View", "_Piano Roll", "Ctrl+Alt+4", "Alt+Cmd+4", help: "The detail panel's piano roll: the Tracker's notes as a grid, edited in the same pattern"),
            C("view.mixer", "View", "Session Mi_xer", "Ctrl+Alt+M", "Alt+Cmd+M", help: "Shows or hides the mixer under the Session View's grid"),

            C("window.minimize", "Window", "_Minimize", null, "Cmd+M"),
            C("window.zoom", "Window", "_Zoom"),
            C("window.front", "Window", "_Bring All to Front"),
            C("window.agent", "Window", "_Agent Connection…", help: "Preferences ▸ Agent: lets an AI agent work in the studio"),

            C("help.shortcuts", "Help", "_Keyboard Shortcuts…", "Cmd+Slash", help: "This list, searchable"),
            C("help.about", "Help", "_About " + Product.Name),
            C("app.about", "App", "About " + Product.Name),
            C("app.preferences", "App", "Preferences…", null, "Cmd+Comma"),
        };

        public static CommandInfo Get(string id) => Commands.First(c => c.Id == id);

        /// <summary>A command's tooltip: "Record (R or F7)" and its help line.</summary>
        public static string Tip(string id, string extra = null) => Get(id).Tip(extra);

        /// <summary>Each view's own keys, which the views handle themselves, as a Mac (or not; default: this platform) writes them.</summary>
        public static IEnumerable<ViewKey> ViewKeys(bool? onMac = null)
        {
            bool mac = onMac ?? KeyText.IsMac;
            string K(string keys) => KeyText.Of(keys, mac);
            yield return new ViewKey("Transport", K("Shift+Space"), "play the pattern from the cursor, or stop it (Pattern)");

            const string M = "Machines";
            yield return new ViewKey(M, "Shift+drag", "from one machine to another: connect them");
            yield return new ViewKey(M, "Double-click", "a machine: its editor, or its parameters");
            yield return new ViewKey(M, K("Cmd+D"), "duplicate the selected machines");
            yield return new ViewKey(M, K(mac ? "Back|Delete" : "Delete|Back"), "delete the selected machines");
            yield return new ViewKey(M, K("Cmd+A"), "select all");
            yield return new ViewKey(M, K("Shift+Cmd+A|Esc"), "deselect all");
            yield return new ViewKey(M, K(mac ? "M" : "M|Ctrl+M"), "mute the selected machines");
            yield return new ViewKey(M, "S", "solo the selected generators");
            yield return new ViewKey(M, "B", "bypass the selected effects");
            yield return new ViewKey(M, K("Cmd+U"), "unmute every machine");
            yield return new ViewKey(M, K("Cmd+R"), "rename the selected machine");
            yield return new ViewKey(M, (mac ? "⌘" : "Ctrl") + " + wheel, or pinch", "zoom");

            const string A = "Sequence (the arrangement)";
            yield return new ViewKey(A, "Arrows, Page Up/Down, Home, End", "move the cursor (Shift: select)");
            yield return new ViewKey(A, "0–9, a–z", "place the pattern with that key (the pattern list shows the keys)");
            yield return new ViewKey(A, ", (comma)  - (minus)  _  .", "break, mute, thru, clear the step");
            yield return new ViewKey(A, K("Enter"), "edit the pattern under the cursor (Pattern)");
            yield return new ViewKey(A, K("Insert") + " / " + K("Delete"), "insert / delete a bar on the track");
            yield return new ViewKey(A, K("Back"), "clear the step before the cursor (with a selection: clear it)");
            yield return new ViewKey(A, K("Cmd+D"), "duplicate the selection (or the block under the cursor) after itself");
            yield return new ViewKey(A, K("Cmd+X") + " " + K("Cmd+C") + " " + K("Cmd+V"), "cut, copy, paste");
            yield return new ViewKey(A, K("Cmd+I") + " / " + K(mac ? "Shift+Cmd+Back" : "Shift+Cmd+Delete"), "insert / delete a bar on every track");
            yield return new ViewKey(A, K("Cmd+Up") + " / " + K("Cmd+Down"), "move the track up / down");
            yield return new ViewKey(A, K("Cmd+Delete"), "delete the track");
            yield return new ViewKey(A, K("Cmd+M") + " / " + K("Cmd+L"), "mute / solo the track's machine");
            yield return new ViewKey(A, K("Cmd+B") + " / " + K("Cmd+E"), "loop start / song end at the cursor");
            yield return new ViewKey(A, K("Cmd+T"), "time signature from the cursor");
            yield return new ViewKey(A, K("Cmd+R"), "rename the track's machine");
            yield return new ViewKey(A, K("Cmd+Enter"), "the menu");
            yield return new ViewKey(A, K("Esc"), "deselect");

            const string S = "Session";
            yield return new ViewKey(S, "Arrows", "move the selection");
            yield return new ViewKey(S, K("Enter"), "launch the selected slot (an empty one stops its track)");
            yield return new ViewKey(S, "1–9", "launch scene 1–9");
            yield return new ViewKey(S, K(mac ? "Back|Delete" : "Delete|Back"), "remove the clip from its slot (its pattern stays)");
            yield return new ViewKey(S, K(mac ? "Shift+Back|Shift+Delete" : "Shift+Delete|Shift+Back"), "delete the clip's pattern (asks first)");
            yield return new ViewKey(S, K("Cmd+D"), "duplicate the clip into the slot below");
            yield return new ViewKey(S, K("Cmd+R") + " or F2", "rename the clip");
            yield return new ViewKey(S, K("Shift+Cmd+M"), "create a clip in the selected slot");
            yield return new ViewKey(S, K("Cmd+I"), "insert a scene below the selection");
            yield return new ViewKey(S, K("Cmd+Z") + " / " + K(mac ? "Shift+Cmd+Z" : "Cmd+Y"), "undo / redo grid edits");
            yield return new ViewKey(S, K("Alt+Cmd+Z"), "undo the last launch");
            yield return new ViewKey(S, K(mac ? "Alt+Cmd+M" : "Ctrl+Alt+M"), "show / hide the mixer");
            yield return new ViewKey(S, K("Esc"), "cancel MIDI learn");
            yield return new ViewKey(S, (mac ? "⌘" : "Ctrl") + " + double-click", "a clip: its notes in the piano roll");

            const string R = "Piano roll";
            yield return new ViewKey(R, "Double-click", "add a note, or delete one");
            yield return new ViewKey(R, "Drag", "move notes (a note's end: its length; empty space: select). The Session View's roll: " + (mac ? "⌥" : "Alt+") + "drag changes velocity");
            yield return new ViewKey(R, "Shift or " + (mac ? "⌥" : "Alt") + " while dragging", "off the grid (the docked piano roll takes either; the Session View's, Shift)");
            yield return new ViewKey(R, K(mac ? "Back|Delete" : "Delete|Back"), "delete the selected notes");
            yield return new ViewKey(R, K("Cmd+A"), "select all notes");
            yield return new ViewKey(R, "Left / Right", "move the selected notes by a grid step (Shift: lengthen or shorten them)");
            yield return new ViewKey(R, "Up / Down", "transpose the selected notes by a semitone (Shift: an octave; " + K("Cmd+Up") + " / " + K("Cmd+Down") + ": velocity)");
            yield return new ViewKey(R, K("Cmd+D"), "duplicate the selected notes after themselves (docked piano roll)");
            yield return new ViewKey(R, "Q", "quantize the selected notes");

            const string N = "Song Notes";
            yield return new ViewKey(N, K("Cmd+B") + " " + K("Cmd+I") + " " + K("Cmd+K"), "bold, italic, link");

            const string W = "Machine windows";
            yield return new ViewKey(W, K("Cmd+K"), "show / hide the keyboard under a machine's window (for that machine type)");
            yield return new ViewKey(W, K("Left") + " / " + K("Right"), "the keyboard an octave down / up (with its keys focused)");
        }

        /// <summary>The Pattern editor's own keys (its help panel's, from the editor's view), or a pointer to them.</summary>
        public static IEnumerable<ViewKey> PatternKeys(IEnumerable<string[]> editorRows, bool? onMac = null)
        {
            bool mac = onMac ?? KeyText.IsMac;
            const string P = "Pattern";
            var rows = editorRows?.ToList();
            if (rows == null || rows.Count == 0)
            {
                yield return new ViewKey(P, "F1", "the editor's keys, in its help panel");
                yield break;
            }
            foreach (var r in rows)
                yield return new ViewKey(P, mac ? r[0].Replace("Ctrl+", "⌘").Replace("Shift+", "⇧").Replace("Alt+", "⌥") : r[0], r[1]);
        }
    }
}
