// Fork (docs/specs/studio-mvp.md S5): the Session View's interaction, ported from the WPF view's
// SessionView.Actions.cs: selection, launching, recording, clip edits, drag, keys and menus.
// Engine calls and undo are the WPF view's (SessionLayout, ISession).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal sealed partial class SessionView
    {
        /// <summary>
        /// The view's keys, as the shortcuts window lists them (ux-overhaul.md §4: one keymap, StudioKeymap), and how the mouse
        /// works here.
        /// </summary>
        internal static string KeyboardHelp =>
            string.Join("\n", Shell.Commands.StudioKeymap.ViewKeys().Where(k => k.Section == "Session").Select(k => k.Keys + "\t" + k.What)) + "\n\n" +
            "Mouse: ▶ launches (hold it for Gate and Repeat clips); click selects and shows the clip in the clip panel; " +
            $"double-click edits a clip or creates one in an empty slot ({ModName}+double-click: its notes in the piano roll); " +
            $"drag a clip to move it within its track (hold {ModName} to copy); right-click for menus and MIDI learn.\n" +
            "Mixer: drag a fader, pan or send (Shift for fine), use the wheel, double-click to reset.";

        /// <summary>A key of the keymap as the menus show it ("⇧⌫", "Ctrl+D").</summary>
        static string KeyOf(string keys) => Shell.Commands.KeyText.Of(keys);

        static string DeleteKeys => OperatingSystem.IsMacOS() ? "Back" : "Delete";

        // ------------------------------------------------------------------ selection and playing

        internal void Select(int track, int scene)
        {
            selected = (track, scene);
            Focus();                              // commits a marker being typed for the clip shown so far
            clipPanel.Follow(selected);
        }

        SlotCell CellAt(int track, int scene) => slots.FirstOrDefault(c => c.Track == track && c.Scene == scene);

        bool SelectOnLaunch => session is not SessionCore core || core.SelectOnLaunch;

        /// <summary>A press of a slot's launch control (▶, Enter): launches its clip with its settings.</summary>
        internal void LaunchSlot(SlotCell cell)
        {
            if (SelectOnLaunch) Select(cell.Track, cell.Scene);
            if (cell.Sequence == null) return;
            if (cell.Pattern != null)
                layout.LaunchClip(cell.Sequence, cell.Pattern, 127, cell.Scene);
            else
                session.Stop(cell.Sequence);     // an empty slot works as the track's stop button, as in Live
            RefreshState();
        }

        /// <summary>The release of a slot's launch control (0.5): Gate clips stop, Repeat clips play on.</summary>
        void ReleaseSlot(SlotCell cell)
        {
            if (cell.Sequence != null && cell.Pattern != null)
                layout.ReleaseClip(cell.Sequence, cell.Pattern);
            RefreshState();
        }

        SlotCell enterHeld;                          // slot launched with Enter, released on key up
        readonly HashSet<Key> keysDown = new HashSet<Key>();     // held keys (Avalonia doesn't flag repeats)

        void OnKeyUp(object sender, KeyEventArgs e)
        {
            keysDown.Remove(e.Key);
            if (e.Source is Visual v && FindParent<TextBox>(v) != null) return;
            if (HandleKeyUp(e.Key))
                e.Handled = true;
        }

        /// <summary>A key released: Enter releases the slot it launched. Returns whether the key was used.</summary>
        internal bool HandleKeyUp(Key key)
        {
            if (key != Key.Enter || enterHeld == null) return false;
            ReleaseHeldKey();
            return true;
        }

        void ReleaseHeldKey()
        {
            var cell = enterHeld;
            enterHeld = null;
            if (cell != null)
                ReleaseSlot(cell);
        }

        internal void LaunchScene(int scene)
        {
            layout.LaunchScene(scene);
            if (SelectOnLaunch) SelectLaunched(-1, scene);
            RefreshState();
        }

        /// <summary>
        /// A slot (or, with track -1, a scene) was launched (0.6, Live's Select on Launch): the
        /// selection follows, a scene keeping the selected track.
        /// </summary>
        internal void SelectLaunched(int track, int scene)
        {
            if (scene < 0 || scene >= sceneCount) return;
            if (track < 0) track = selected?.Track ?? 0;
            if (track < 0 || track >= Math.Max(1, trackCount)) return;
            selected = (track, scene);
            clipPanel.Follow(selected);
        }

        void ShowClipPanel()
        {
            if (!layout.Ensure().ShowClipPanel)
                layout.SetShowClipPanel(true);
        }

        internal void StopTrack(int track)
        {
            if (layout.SequenceOf(track) is ISequence s)
            {
                layout.StopRecording(s);                     // 0.7: a take recording until stopped gets its length
                session.Stop(s);
            }
            RefreshState();
        }

        /// <summary>The clip's notes in the piano roll (0.7.1).</summary>
        internal PianoRollWindow EditNotes(ISequence track, IPattern pattern) =>
            PianoRollWindow.Show(TopLevel.GetTopLevel(this) as Window, buzz, layout, track, pattern);

        /// <summary>Moves the clip's notes to its MIDI note lanes in the Pattern View, or back (0.7.1).</summary>
        void MoveNotes(ISequence track, IPattern pattern, bool toPatternView)
        {
            var problem = toPatternView ? layout.MoveNotesToPatternView(track, pattern) : layout.MoveNotesFromPatternView(track, pattern);
            if (problem != null)
                _ = Dialogs.Info(problem, toPatternView ? "Move notes to the Pattern View" : "Move notes from the Pattern View");
            RefreshState();
        }

        // ------------------------------------------------------------------ recording (0.7)

        internal void ToggleArm(int track)
        {
            layout.SetArmed(track, !layout.IsArmed(track));
            RefreshState();
        }

        internal void RecordInto(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            layout.RecordTake(cell.Track, cell.Scene);
            RefreshState();
        }

        void Capture()
        {
            layout.CaptureMidi(selected?.Scene ?? 0);
            RefreshState();
        }

        void ToggleSessionRecord()
        {
            layout.SetSessionRecord(!layout.IsSessionRecording, selected?.Scene ?? 0);
            RefreshState();
        }

        // The shell's Edit menu and shortcuts (IStudioEditTarget): Undo and Redo are the grid's own history, as on Windows.
        // Fork (ux-overhaul.md §4): Duplicate, Delete and Rename act on the selected slot, as the view's own keys do.

        public bool Duplicate()
        {
            if (selected is not { } at || layout.PatternAt(at.Track, at.Scene) == null) return false;
            DuplicateClip(at.Track, at.Scene);
            return true;
        }

        public bool Delete()
        {
            if (selected is not { } at || layout.PatternAt(at.Track, at.Scene) == null) return false;
            layout.ClearSlot(at.Track, at.Scene);
            return true;
        }

        public bool Rename()
        {
            if (selected is not { } at || layout.PatternAt(at.Track, at.Scene) is not { } pattern) return false;
            _ = RenameClip(pattern);
            return true;
        }

        public bool Undo()
        {
            if (!layout.UndoStack.CanUndo) return false;
            layout.Undo();
            return true;
        }

        public bool Redo()
        {
            if (!layout.UndoStack.CanRedo) return false;
            layout.Redo();
            return true;
        }

        internal void SlotDoubleClicked(SlotCell cell, KeyModifiers mods)
        {
            // Until the pattern editor is in the studio (S6), a clip's notes open in the piano roll.
            if (cell.Pattern != null && (mods.HasFlag(KeyModifiers.Control) || !StudioViews.Has("Pattern")))
                EditNotes(cell.Sequence, cell.Pattern);
            else if (cell.Pattern != null)
                EditPattern(cell.Pattern);
            else
                CreateClip(cell.Track, cell.Scene);
        }

        // ------------------------------------------------------------------ clip edits

        void CreateClip(int track, int scene)
        {
            if (layout.PatternAt(track, scene) != null) return;
            if (layout.CreateClip(track, scene) != null)
                Select(track, scene);
        }

        void DuplicateClip(int track, int scene)
        {
            if (layout.DuplicateClip(track, scene, out int target) != null)
                Select(track, target);
        }

        async Task RenameClip(IPattern pattern)
        {
            if (pattern == null) return;
            string name = await Prompt("Rename clip", $"New name for pattern '{pattern.Name}' of {pattern.Machine?.Name}", pattern.Name);
            if (name == null) return;
            if (!layout.RenameClip(pattern, name) && name.Trim() != pattern.Name)
                await Dialogs.Info($"'{name.Trim()}' is empty or already used by another pattern of {pattern.Machine?.Name}.", "Rename clip");
        }

        async Task DeleteClip(IPattern pattern)
        {
            if (pattern == null) return;
            bool arranged = buzz.Song.Sequences.Any(s => s.Events.Values.Any(e => e.Pattern == pattern));
            if (arranged)
            {
                var answer = await Dialogs.Ask(
                    $"Pattern '{pattern.Name}' is also used in the arrangement. Deleting it removes it there too, and undo only brings the pattern back.\n\nDelete it?",
                    "Delete pattern", UserButtons.YesNo, DialogChoices.Delete);
                if (answer != UserAnswer.Yes) return;
            }
            layout.DeleteClip(pattern);
        }

        /// <summary>The clip in the pattern editor: the Pattern tab (step S6 brings its editor).</summary>
        void EditPattern(IPattern pattern)
        {
            if (pattern == null) return;
            buzz.SetPatternEditorPattern(pattern);
            buzz.ActiveView = BuzzView.PatternView;
            if (this.FindAncestorOfType<TabControl>() is TabControl tabs)
            {
                var tab = tabs.Items.OfType<TabItem>().FirstOrDefault(t => t.Header as string == "Pattern");
                if (tab != null) tabs.SelectedItem = tab;
            }
        }

        async Task InsertSceneIntoArrangement(int scene)
        {
            int ticksPerBar = Math.Max(1, buzz.TPB * session.BeatsPerBar);
            int length = SceneToArrangement.SceneLength(layout, scene, ticksPerBar);
            int position = buzz.SongCore.PlayPosition / ticksPerBar * ticksPerBar;
            var doc = layout.Ensure();
            string name = scene < doc.Scenes.Count && !string.IsNullOrEmpty(doc.Scenes[scene].Name) ? doc.Scenes[scene].Name : $"scene {scene + 1}";
            var answer = await ArrangementInsertDialog.Ask(name, position / ticksPerBar + 1, length / ticksPerBar);
            if (answer is not { } a) return;
            SceneToArrangement.Insert(buzz, layout, scene, (a.Bar - 1) * ticksPerBar, a.Bars * ticksPerBar, a.Insert);
        }

        /// <summary>Scene tempo (0.5): set when the scene launches.</summary>
        async Task AskSceneTempo(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            int current = doc.Scenes[scene].Tempo;
            string text = await Prompt("Scene tempo",
                $"Tempo (BPM, 16–500) set when the scene launches, on its launch beat.\nEmpty for none. Now: {buzz.BPM} BPM.",
                current > 0 ? current.ToString() : "");
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) { layout.SetSceneTempo(scene, 0); return; }
            if (int.TryParse(text, out int bpm) && bpm >= 16 && bpm <= 500)
                layout.SetSceneTempo(scene, bpm);
            else
                await Dialogs.Info($"'{text}' is not a tempo between 16 and 500.", "Scene tempo");
        }

        /// <summary>Scene time signature (0.5): beats per bar set when the scene launches.</summary>
        async Task AskSceneBeats(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            int current = doc.Scenes[scene].BeatsPerBar;
            string text = await Prompt("Scene beats per bar",
                "Beats per bar (1–64) set when the scene launches; bars count from there.\nEmpty for none.",
                current > 0 ? current.ToString() : "");
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) { layout.SetSceneBeatsPerBar(scene, 0); return; }
            if (int.TryParse(text, out int beats) && beats >= 1 && beats <= 64)
                layout.SetSceneBeatsPerBar(scene, beats);
            else
                await Dialogs.Info($"'{text}' is not a number of beats between 1 and 64.", "Scene beats per bar");
        }

        async Task RenameScene(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            string name = await Prompt("Rename scene", "Scene name", doc.Scenes[scene].Name);
            if (name != null)
                layout.RenameScene(scene, name);
        }

        /// <summary>A line of text, with a label saying what it is for; null when cancelled. Tests answer through Dialogs.TextResponder.</summary>
        static async Task<string> Prompt(string title, string label, string initial)
        {
            if (Dialogs.TextResponder != null) return Dialogs.TextResponder(title, initial);
            var owner = Dialogs.Owner;
            var box = new TextBox { Text = initial ?? "", MinWidth = 280 };
            string result = null;
            var window = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                CanResize = false,
                WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
            };
            var ok = new Button { Content = "OK", MinWidth = 72, IsDefault = true };
            var cancel = new Button { Content = "Cancel", MinWidth = 72, IsCancel = true };
            ok.Click += (s, e) => { result = box.Text; window.Close(); };
            cancel.Click += (s, e) => window.Close();
            window.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 },
                    box,
                    new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
                },
            };
            window.Opened += (s, e) => { box.Focus(); box.SelectAll(); };
            if (owner != null) await window.ShowDialog(owner);
            else
            {
                var closed = new TaskCompletionSource();
                window.Closed += (s, e) => closed.TrySetResult();
                window.Show();
                await closed.Task;
            }
            return result;
        }

        // ------------------------------------------------------------------ drag (move/copy within a track)

        internal void DragOver(SlotCell source, Point position)
        {
            var target = slots.FirstOrDefault(c => c.Track == source.Track && BoundsInView(c).Contains(position));
            dropTarget = target == null || target.Scene == source.Scene ? null : (target.Track, target.Scene);
        }

        internal void EndDrag(SlotCell source, Point? position, bool copy)
        {
            if (position is Point p)
                DragOver(source, p);
            var target = dropTarget;
            dropTarget = null;
            if (target is { } t && layout.MoveSlot(source.Track, source.Scene, t.Scene, copy))
                selected = t;
            RefreshState();
        }

        Rect BoundsInView(Control element)
        {
            var origin = element.TranslatePoint(new Point(0, 0), this) ?? new Point(-1e6, -1e6);
            return new Rect(origin, element.Bounds.Size);
        }

        // ------------------------------------------------------------------ keyboard

        /// <summary>
        /// The view's shortcuts with Control (and Command on macOS) as key bindings of its own.
        /// Avalonia matches key bindings from the focused element up before it raises KeyDown,
        /// so the main window's (Ctrl+Z: the song's undo) would otherwise take them; this way
        /// the view gets its keys first, as the docked WPF view does. Not while typing in a box.
        /// </summary>
        void BindShortcuts()
        {
            var commands = new List<KeyModifiers> { KeyModifiers.Control };
            if (OperatingSystem.IsMacOS()) commands.Add(KeyModifiers.Meta);
            var shortcuts = new (Key Key, KeyModifiers With)[]
            {
                (Key.Z, KeyModifiers.None), (Key.Y, KeyModifiers.None), (Key.Z, KeyModifiers.Shift), (Key.I, KeyModifiers.None),
                (Key.D, KeyModifiers.None), (Key.R, KeyModifiers.None), (Key.M, KeyModifiers.Shift), (Key.M, KeyModifiers.Alt), (Key.Z, KeyModifiers.Alt),
            };
            foreach (var command in commands)
                foreach (var (key, with) in shortcuts)
                {
                    var mods = command | with;
                    KeyBindings.Add(new KeyBinding
                    {
                        Gesture = new KeyGesture(key, mods),
                        Command = new Command(() => HandleKey(key, Normalize(mods)), () => attached && !TypingInBox()),
                    });
                }
        }

        /// <summary>Whether the keys go to a text box (or an open drop-down) in the view.</summary>
        bool TypingInBox()
        {
            var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
            return focused != null && (FindParent<TextBox>(focused) != null || FindParent<ComboBox>(focused) is ComboBox { IsDropDownOpen: true });
        }

        void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Handled) return;
            bool repeat = !keysDown.Add(e.Key);
            if (e.Source is Visual v && (FindParent<TextBox>(v) != null || FindParent<ComboBox>(v) is ComboBox { IsDropDownOpen: true })) return;
            var mods = Normalize(e.KeyModifiers);
            // Held keys repeat: a held Enter or scene key launches once (Repeat clips repeat by themselves).
            if (repeat && mods == KeyModifiers.None &&
                (e.Key == Key.Enter && selected != null || e.Key >= Key.D1 && e.Key <= Key.D9 || e.Key >= Key.NumPad1 && e.Key <= Key.NumPad9))
            {
                e.Handled = true;
                return;
            }
            if (HandleKey(e.Key, mods))
                e.Handled = true;
        }

        /// <summary>Runs a keyboard command (Control is Command on macOS); returns whether the key was used.</summary>
        internal bool HandleKey(Key key, KeyModifiers mods)
        {
            bool ctrl = mods == KeyModifiers.Control;
            bool none = mods == KeyModifiers.None;
            var sel = selected;

            if (key == Key.Escape && midi?.IsLearning == true) { midi.CancelLearn(); return true; }

            if (none && key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                MoveSelection(key);
                return true;
            }
            if (none && key == Key.Space)
            {
                // The main window takes Space first in every view (TransportKeys); the same command.
                TransportCommands.TogglePlaying(buzz);
                return true;
            }
            if (none && (key >= Key.D1 && key <= Key.D9 || key >= Key.NumPad1 && key <= Key.NumPad9))
            {
                LaunchScene(key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1);
                return true;
            }
            if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && key == Key.M) { ToggleMixer(); return true; }
            if (mods == (KeyModifiers.Control | KeyModifiers.Alt) && key == Key.Z) { UndoLaunch(); return true; }
            if (ctrl && key == Key.Z) { layout.Undo(); return true; }
            if ((ctrl && key == Key.Y) || (mods == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.Z)) { layout.Redo(); return true; }
            if (ctrl && key == Key.I)
            {
                layout.InsertScene(sel is { } s ? s.Scene + 1 : layout.SceneCount);
                return true;
            }

            if (sel is not { } at) return false;
            var pattern = layout.PatternAt(at.Track, at.Scene);

            if (none && key == Key.Enter)
            {
                ReleaseHeldKey();
                if (CellAt(at.Track, at.Scene) is SlotCell cell)
                {
                    LaunchSlot(cell);
                    enterHeld = cell.Pattern != null ? cell : null;
                }
                return true;
            }
            // Fork (ux-overhaul.md §4): ⌫ deletes in every view: here the clip leaves its slot (⌫ stopped the track; Enter on an
            // empty slot, its ■, or the track menu do that); ⇧⌫ deletes its pattern, after asking.
            if (none && key is Key.Back or Key.Delete) { layout.ClearSlot(at.Track, at.Scene); return true; }
            if (mods == KeyModifiers.Shift && key is Key.Back or Key.Delete) { _ = DeleteClip(pattern); return true; }
            if (mods == (KeyModifiers.Control | KeyModifiers.Shift) && key == Key.M) { CreateClip(at.Track, at.Scene); return true; }
            if (ctrl && key == Key.D) { DuplicateClip(at.Track, at.Scene); return true; }
            if ((none && key == Key.F2) || (ctrl && key == Key.R)) { _ = RenameClip(pattern); return true; }
            return false;
        }

        void MoveSelection(Key key)
        {
            // Over the shown tracks: folded group members are skipped.
            var visible = VisibleTracks();
            if (visible.Count == 0 || sceneCount == 0) return;
            int i = selected is { } cur ? visible.IndexOf(cur.Track) : -1;
            int s = selected?.Scene ?? 0;
            if (i < 0)
            {
                i = 0;
            }
            else
            {
                if (key == Key.Left) i--;
                else if (key == Key.Right) i++;
                else if (key == Key.Up) s--;
                else if (key == Key.Down) s++;
            }
            selected = (visible[Math.Clamp(i, 0, visible.Count - 1)], Math.Clamp(s, 0, sceneCount - 1));
            if (CellAt(selected.Value.Track, selected.Value.Scene) is SlotCell cell)
                cell.BringIntoView();
            RefreshState();
        }

        static T FindParent<T>(Visual v) where T : class
        {
            while (v != null && v is not T)
                v = v.GetVisualParent();
            return v as T;
        }

        // ------------------------------------------------------------------ menus

        /// <summary>The menu opened last (tests pick its items).</summary>
        internal ContextMenu LastMenu { get; private set; }

        internal void ShowSlotMenu(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            var menu = new ContextMenu();
            var pattern = cell.Pattern;
            var machine = cell.Sequence?.Machine;

            if (pattern != null)
            {
                menu.Items.Add(Item("Launch", () => LaunchSlot(cell), "Enter"));
                menu.Items.Add(Item("Clip settings", ShowClipPanel));
                menu.Items.Add(Item("Edit pattern", () => EditPattern(pattern), "double-click"));
                menu.Items.Add(Item("Edit notes (piano roll)…", () => EditNotes(cell.Sequence, pattern)));
                if (layout.CanMoveNotes(pattern))
                {
                    int clipNotes = layout.NotesOf(pattern).Count;
                    if (clipNotes > 0)
                        menu.Items.Add(Item($"Move notes to the Pattern View ({clipNotes})", () => MoveNotes(cell.Sequence, pattern, toPatternView: true)));
                    int laneNotes = layout.PatternViewNotesOf(pattern).Count;
                    if (laneNotes > 0)
                        menu.Items.Add(Item($"Move notes from the Pattern View ({laneNotes})", () => MoveNotes(cell.Sequence, pattern, toPatternView: false)));
                }
                menu.Items.Add(Item("Rename…", () => _ = RenameClip(pattern), KeyOf("Cmd+R")));
                menu.Items.Add(Item("Duplicate", () => DuplicateClip(cell.Track, cell.Scene), KeyOf("Cmd+D")));
                menu.Items.Add(ColorMenu("Colour", PatternColors.Get(pattern), i => layout.SetClipColor(pattern, i)));
                int notes = layout.RecordedNoteCount(pattern);
                if (notes > 0)
                    menu.Items.Add(Item($"Clear recorded notes ({notes})", () => layout.ClearRecordedNotes(pattern)));
                int moves = layout.RecordedAutomationCount(pattern);
                if (moves > 0)
                    menu.Items.Add(Item($"Clear recorded automation ({moves})", () => layout.ClearRecordedAutomation(pattern)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove from slot", () => layout.ClearSlot(cell.Track, cell.Scene), KeyOf(DeleteKeys)));
                menu.Items.Add(Item("Delete pattern…", () => _ = DeleteClip(pattern), KeyOf("Shift+" + DeleteKeys)));
            }
            else
            {
                menu.Items.Add(Item("Create clip", () => CreateClip(cell.Track, cell.Scene), KeyOf("Shift+Cmd+M"), () => machine != null));
                menu.Items.Add(Item("Record a take here", () => RecordInto(cell), null, () => layout.IsArmed(cell.Track)));
            }

            if (machine != null)
            {
                var assign = new MenuItem { Header = $"Assign pattern of {machine.Name}", Tag = "Assign pattern" };
                foreach (var p in machine.Patterns.ToList())
                {
                    var choice = p;
                    assign.Items.Add(new MenuItem
                    {
                        Header = choice.Name,
                        Tag = choice.Name,
                        ToggleType = MenuItemToggleType.CheckBox,
                        IsChecked = choice == pattern,
                        Command = new Command(() => layout.SetSlot(cell.Track, cell.Scene, choice))
                    });
                }
                if (assign.Items.Count == 0)
                    assign.Items.Add(new MenuItem { Header = "(machine has no patterns)", IsEnabled = false });
                menu.Items.Add(new Separator());
                menu.Items.Add(assign);
            }

            menu.Items.Add(new Separator());
            AddMidiItems(menu, SessionTarget.Slot, cell.Track, cell.Scene);
            OpenMenu(menu, cell);
        }

        static string ClipCellFollow(SessionDocument.FollowSettings f) => SlotCell.FollowSummary(f, false);

        internal void ShowSceneMenu(SceneCell cell)
        {
            var doc = layout.Ensure();
            int scene = cell.Scene;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Launch scene", () => LaunchScene(scene), scene < 9 ? (scene + 1).ToString() : null));
            menu.Items.Add(Item("Rename…", () => _ = RenameScene(scene)));
            menu.Items.Add(ColorMenu("Colour", scene < doc.Scenes.Count ? doc.Scenes[scene].ColorIndex : -1, i => layout.SetSceneColor(scene, i)));
            var info = scene < doc.Scenes.Count ? doc.Scenes[scene] : null;
            menu.Items.Add(Item(info?.Tempo > 0 ? $"Tempo… ({info.Tempo} BPM)" : "Tempo…", () => _ = AskSceneTempo(scene), tag: "Tempo…"));
            menu.Items.Add(Item(info?.BeatsPerBar > 0 ? $"Beats per bar… ({info.BeatsPerBar})" : "Beats per bar…", () => _ = AskSceneBeats(scene), tag: "Beats per bar…"));
            menu.Items.Add(Item(info?.Follow?.On == true ? $"Follow action… ({ClipCellFollow(info.Follow)})" : "Follow action…", () =>
            {
                Select(selected?.Track ?? 0, scene);
                ShowClipPanel();
            }, tag: "Follow action…"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert scene above", () => layout.InsertScene(scene)));
            menu.Items.Add(Item("Insert scene below", () => layout.InsertScene(scene + 1), KeyOf("Cmd+I")));
            menu.Items.Add(Item("Duplicate scene", () => layout.DuplicateScene(scene)));
            menu.Items.Add(Item("Delete scene", () => layout.RemoveScene(scene)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert into arrangement…", () => _ = InsertSceneIntoArrangement(scene), null,
                () => doc.SceneSlots(scene).Any()));
            menu.Items.Add(new Separator());
            AddMidiItems(menu, SessionTarget.Scene, 0, scene);
            OpenMenu(menu, cell);
        }

        internal void ShowTrackMenu(TrackHeader header)
        {
            var doc = layout.Ensure();
            int track = header.Track;
            var menu = new ContextMenu();
            menu.Items.Add(ColorMenu("Track colour", track < doc.Tracks.Count ? doc.Tracks[track].ColorIndex : -1, i => layout.SetTrackColor(track, i)));
            var machine = header.Machine;
            if (machine != null)
            {
                // The track is its machine's sequence: renaming it renames the machine (undoable).
                menu.Items.Add(Item("Rename…", () => ReBuzz.Studio.Views.Machines.MachineEdits.Rename(machine)));
                menu.Items.Add(Check("Arm", layout.IsArmed(track), () => ToggleArm(track),
                    "Arm for recording: MIDI you play goes to this track's machine and is recorded (the header's ●)"));
                menu.Items.Add(Item("Stop clips", () => StopTrack(track)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Check("Mute", machine.IsMuted, () => machine.IsMuted = !machine.IsMuted));
                menu.Items.Add(Check("Solo", machine.IsSoloed, () => machine.IsSoloed = !machine.IsSoloed));
                menu.Items.Add(new Separator());
                var remove = Item("Remove track", () => RemoveTrack(track));
                ToolTip.SetTip(remove, "Removes the machine's track (its sequence, with its arrangement) from the song; the machine stays.\nEdit ▸ Undo brings it back.");
                menu.Items.Add(remove);
            }
            OpenMenu(menu, header);
        }

        /// <summary>Removes a track: its sequence, as the Sequence View's Delete Track does (one step of the song's undo).</summary>
        void RemoveTrack(int track)
        {
            var seq = layout.SequenceOf(track);
            if (seq?.Machine == null) return;
            buzz.SongCore.ActionStack.Do(new ReBuzz.Studio.Views.Sequence.DeleteSequenceAction(seq));
        }

        void ShowMidiOnlyMenu(Control anchor, SessionTarget target, int track, int scene, string what)
        {
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = what, IsEnabled = false });
            AddMidiItems(menu, target, track, scene);
            OpenMenu(menu, anchor);
        }

        void AddMidiItems(ContextMenu menu, SessionTarget target, int track, int scene)
        {
            if (midi == null) return;
            var binding = midi.FindBinding(target, track, scene);
            menu.Items.Add(Item("MIDI learn…", () => midi.BeginLearn(target, track, scene)));
            menu.Items.Add(Item(binding is MidiBinding b ? $"Clear MIDI mapping ({b.Control})" : "Clear MIDI mapping",
                () => midi.Unbind(target, track, scene), null, () => binding != null, tag: "Clear MIDI mapping"));
        }

        void ShowAddTrackMenu(Control anchor)
        {
            var menu = new ContextMenu();
            var machines = layout.MachinesWithoutTrack().ToList();
            foreach (var m in machines)
            {
                var machine = m;
                menu.Items.Add(Item(machine.Name, () => layout.AddTrackFor(machine)));
            }
            if (machines.Count == 0)
                menu.Items.Add(new MenuItem { Header = "Every machine already has a track", IsEnabled = false });
            OpenMenu(menu, anchor);
        }

        void ShowSettingsMenu(Control anchor)
        {
            var doc = layout.Ensure();
            var menu = new ContextMenu();

            var length = new MenuItem { Header = "New clip length", Tag = "New clip length" };
            foreach (int bars in new[] { 1, 2, 4, 8 })
            {
                int b = bars;
                length.Items.Add(Check(b == 1 ? "1 bar" : $"{b} bars", doc.ClipBars == b, () => layout.SetClipBars(b)));
            }
            menu.Items.Add(length);

            // 0.7: recording.
            var takeLength = new MenuItem { Header = "Record length", Tag = "Record length" };
            foreach (int bars in new[] { 0, 1, 2, 4, 8 })
            {
                int b = bars;
                takeLength.Items.Add(Check(b == 0 ? "Until stopped" : b == 1 ? "1 bar" : $"{b} bars", doc.RecordBars == b, () => layout.SetRecordBars(b)));
            }
            menu.Items.Add(takeLength);
            var countIn = new MenuItem { Header = "Count-in", Tag = "Count-in" };
            foreach (int bars in new[] { 0, 1, 2 })
            {
                int b = bars;
                countIn.Items.Add(Check(b == 0 ? "None" : b == 1 ? "1 bar" : $"{b} bars", doc.CountInBars == b, () => layout.SetCountInBars(b)));
            }
            menu.Items.Add(countIn);
            menu.Items.Add(Check("Metronome", doc.Metronome, () => layout.SetMetronome(!doc.Metronome),
                "A click on every beat while the session plays (louder on the bar line). Heard, never recorded."));
            menu.Items.Add(new Separator());
            menu.Items.Add(Check("Scenes stop other clips", doc.ScenesStopOtherClips, () => layout.SetScenesStopOtherClips(!doc.ScenesStopOtherClips),
                "On: launching a scene also stops the tracks it has no clip for (as if its empty slots\n" +
                "had stop buttons). Off: those tracks play on. Saved with the song."));
            if (session is SessionCore core)
            {
                menu.Items.Add(Check("Select on launch", core.SelectOnLaunch, () => core.SetSelectOnLaunch(!core.SelectOnLaunch),
                    "On: launching a clip or scene (mouse, keys or MIDI) selects it and shows it in the clip panel."));
                double ms = buzz.OutputLatencySamples * 1000.0 / Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec);
                menu.Items.Add(Check(ms > 0 ? $"Time launches to what you hear ({ms:F0} ms latency)" : "Time launches to what you hear",
                    core.LatencyCompensation, () => core.SetLatencyCompensation(!core.LatencyCompensation),
                    "On: a launch pressed just before a beat or bar line, as you hear it, lands on that line, even if\n" +
                    "the engine has already rendered past it (the clip starts in step, skipping what already played).\n" +
                    "Off: launches wait for the next line after what the engine has rendered.", tag: "Time launches to what you hear"));
            }
            AddSyncMenu(menu);
            AddControllersMenu(menu);
            menu.Items.Add(new Separator());
            AddMixerMenuItems(menu, doc);

            if (midi != null)
            {
                menu.Items.Add(new Separator());
                var move = new MenuItem { Header = "MIDI learn: move the note grid", IsEnabled = midi.ActiveMap?.Grid != null };
                move.Items.Add(Item("Up", () => midi.BeginLearn(SessionTarget.GridUp, 0, 0)));
                move.Items.Add(Item("Down", () => midi.BeginLearn(SessionTarget.GridDown, 0, 0)));
                move.Items.Add(Item("Left", () => midi.BeginLearn(SessionTarget.GridLeft, 0, 0)));
                move.Items.Add(Item("Right", () => midi.BeginLearn(SessionTarget.GridRight, 0, 0)));
                menu.Items.Add(move);
                menu.Items.Add(Item("MIDI learn play/stop", () => midi.BeginLearn(SessionTarget.Transport, 0, 0)));
                menu.Items.Add(Item("MIDI learn launch quantization", () => midi.BeginLearn(SessionTarget.Quantization, 0, 0)));
                var perform = new MenuItem { Header = "MIDI learn: performance" };
                perform.Items.Add(Item("Undo the last launch", () => midi.BeginLearn(SessionTarget.UndoLaunch, 0, 0)));
                perform.Items.Add(Item("Follow actions on/off", () => midi.BeginLearn(SessionTarget.FollowActions, 0, 0)));
                perform.Items.Add(Item("Tap tempo", () => midi.BeginLearn(SessionTarget.TapTempo, 0, 0)));
                perform.Items.Add(Item("Nudge down (hold)", () => midi.BeginLearn(SessionTarget.NudgeDown, 0, 0)));
                perform.Items.Add(Item("Nudge up (hold)", () => midi.BeginLearn(SessionTarget.NudgeUp, 0, 0)));
                perform.Items.Add(Item("Crossfader (a knob or fader)", () => midi.BeginLearn(SessionTarget.Crossfader, 0, 0)));
                menu.Items.Add(perform);
                menu.Items.Add(Check("Store MIDI mapping in this song", midi.UsesSongMap, () => midi.SetUseSongMap(!midi.UsesSongMap),
                    "On: this song keeps its own mapping, used instead of yours when it's open.\nOff: the song uses your mapping (shared by all songs)."));
                menu.Items.Add(Item("Clear all MIDI mappings", () => midi.ClearAll(), null, () => midi.ActiveMap?.IsEmpty == false));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Keyboard shortcuts…", () => Shell.Commands.ShortcutsWindow.Open(TopLevel.GetTopLevel(this) as Window).Search = "Session"));
            OpenMenu(menu, anchor);
        }

        /// <summary>Control surfaces (0.8, ported in S7): the controllers the MIDI ports offer, on or off.</summary>
        void AddControllersMenu(ContextMenu menu)
        {
            var controllers = buzz.SessionControllers;
            if (controllers == null) return;
            var item = new MenuItem { Header = "Controllers", Tag = "Controllers" };
            var available = controllers.Available();
            foreach (var candidate in available)
            {
                var script = candidate.Script;
                bool on = controllers.Wanted.Contains(script.Name);
                bool connected = controllers.IsConnected(script.Name);
                item.Items.Add(Check($"{script.Name} ({candidate.InputName})" + (on && !connected ? " (not answering)" : ""), on, () =>
                {
                    if (on) controllers.TurnOff(script);
                    else
                    {
                        string problem = controllers.TurnOn(script);
                        if (problem != null) _ = Dialogs.Info(problem, script.Name);
                    }
                    RefreshState();
                },
                "On: its pads launch the slots in its session ring (the red box), its lights follow the session.\n" +
                "Its MIDI ports are then its own: leave them off in Preferences ▸ MIDI.", tag: script.Name));
            }
            foreach (var name in controllers.Wanted.Where(n => available.All(a => a.Script.Name != n)).ToList())
            {
                var script = controllers.Scripts.FirstOrDefault(s => s.Name == name);
                if (script == null) continue;
                item.Items.Add(Check($"{name} (not connected)", true, () => controllers.TurnOff(script), tag: name));
            }
            if (item.Items.Count == 0)
            {
                var none = new MenuItem { Header = "No controller found", Tag = "No controller found", IsEnabled = false };
                ToolTip.SetTip(none, "Known: " + string.Join(", ", controllers.Scripts.Select(s => s.Name)) +
                                     ".\nMore can be added as scripts (.dll) in the Controllers folder next to " + Product.Name + ".");
                item.Items.Add(none);
            }
            foreach (var problem in controllers.ScriptProblems)
                item.Items.Add(new MenuItem { Header = "Script not loaded: " + problem, IsEnabled = false });
            menu.Items.Add(item);
        }

        /// <summary>Sync (0.8, ported in S7): Ableton Link, MIDI clock in and out. Preferences ▸ Sync has them too.</summary>
        void AddSyncMenu(ContextMenu menu)
        {
            var sync = buzz.SessionSync;
            if (sync == null) return;
            var item = new MenuItem { Header = sync.Status.Length > 0 ? $"Sync ({sync.Status})" : "Sync", Tag = "Sync" };
            void Report(string title)
            {
                if (sync.Problem != null) _ = Dialogs.Info(sync.Problem, title);
                RefreshState();
            }
            item.Items.Add(Check(!sync.LinkEnabled ? "Link" : sync.LinkPeers > 0 ? $"Link ({sync.LinkPeers} peer{(sync.LinkPeers == 1 ? "" : "s")})" : "Link (no peers yet)",
                sync.LinkEnabled, () => { sync.SetLink(!sync.LinkEnabled); Report("Link"); },
                "On: tempo and bar phase follow Live and other Link apps on the network; alone, they follow the session.\n" +
                "A start waits for Link's next bar line, so it lands in step. Runs Link in a helper program (ReBuzzLink).", tag: "Link"));
            var startStop = Check("Start and stop with Link peers", sync.LinkStartStop, () => sync.SetLinkStartStop(!sync.LinkStartStop),
                "On: play and stop are shared with peers that have start/stop sync on (Live: Link › Start Stop Sync).");
            startStop.IsEnabled = sync.LinkEnabled;
            item.Items.Add(startStop);
            item.Items.Add(new Separator());
            item.Items.Add(Check("Follow MIDI clock in", sync.FollowMidiClock, () => { sync.SetFollowMidiClock(!sync.FollowMidiClock); Report("MIDI clock in"); },
                "On: tempo and beat follow the MIDI clock coming in (any open MIDI input), and so do Start, Continue and Stop.\n" +
                "Turns Link off."));
            var outs = new MenuItem { Header = "Send MIDI clock to", Tag = "Send MIDI clock to" };
            var open = sync.Outputs;
            foreach (var (_, name) in open)
            {
                string device = name;
                bool on = sync.ClockOutNames.Contains(device);
                outs.Items.Add(Check(device, on, () => sync.SetClockOut(device, !on)));
            }
            foreach (var missing in sync.ClockOutNames.Where(n => open.All(o => o.Name != n)).ToList())
                outs.Items.Add(Check($"{missing} (not open)", true, () => sync.SetClockOut(missing, false), tag: missing));
            if (outs.Items.Count == 0)
                outs.Items.Add(new MenuItem { Header = "No MIDI outputs open (Preferences ▸ MIDI)", IsEnabled = false });
            item.Items.Add(outs);
            menu.Items.Add(new Separator());
            menu.Items.Add(item);
        }

        MenuItem ColorMenu(string header, int current, Action<int> set)
        {
            var item = new MenuItem { Header = header, Tag = header };
            item.Items.Add(Check("None", current < 0, () => set(-1)));
            // Fork (ux-overhaul.md §3.1): the state colours aren't offered (a colour in use stays, ticked).
            int count = SessionPalette.ColorChoices.Length;
            foreach (var (index, name, colour) in SessionPalette.OfferedColours(current))
            {
                var choice = Check(name, current >= 0 && current % count == index, () => set(index));
                choice.Icon = new Border { Width = 14, Height = 14, Background = SessionPalette.BrushOf(colour) };
                item.Items.Add(choice);
            }
            return item;
        }

        /// <summary>
        /// A menu command. Its Tag is its name (what tests look for); a gesture shows on the right,
        /// as text, since some are mouse gestures.
        /// </summary>
        static MenuItem Item(string header, Action action, string gesture = null, Func<bool> canExecute = null, string tag = null)
        {
            object content = header;
            if (gesture != null)
            {
                var row = new DockPanel { LastChildFill = true };
                var g = new TextBlock { Text = gesture, Foreground = SessionPalette.DimText, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
                DockPanel.SetDock(g, Dock.Right);
                row.Children.Add(g);
                row.Children.Add(new TextBlock { Text = header, VerticalAlignment = VerticalAlignment.Center });
                content = row;
            }
            return new MenuItem { Header = content, Tag = tag ?? header, Command = new Command(action, canExecute) };
        }

        static MenuItem Check(string header, bool isChecked, Action action, string tip = null, string tag = null)
        {
            var item = new MenuItem
            {
                Header = header,
                Tag = tag ?? header,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = isChecked,
                Command = new Command(action),
            };
            if (tip != null) ToolTip.SetTip(item, tip);
            return item;
        }

        void OpenMenu(ContextMenu menu, Control anchor)
        {
            LastMenu = menu;
            menu.Placement = PlacementMode.Bottom;
            menu.Open(anchor);
        }

        /// <summary>Runs the item of <see cref="LastMenu"/> named <paramref name="path"/> ("Colour/Red"), as a click would; false when it is missing or disabled.</summary>
        internal bool ClickMenuItem(string path)
        {
            IEnumerable<object> items = LastMenu?.Items;
            MenuItem item = null;
            foreach (var name in path.Split('/'))
            {
                item = items?.OfType<MenuItem>().FirstOrDefault(i => i.Tag as string == name);
                if (item == null) return false;
                items = item.Items;
            }
            if (item?.Command == null || !item.Command.CanExecute(null)) return false;
            LastMenu.Close();
            item.Command.Execute(null);
            return true;
        }
    }
}
