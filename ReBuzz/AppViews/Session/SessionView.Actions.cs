using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ReBuzz.AppViews.Session
{
    internal sealed partial class SessionView
    {
        internal const string KeyboardHelp =
            "Arrows\tmove the selection\n" +
            "Enter\tlaunch the selected slot (stops the track if empty)\n" +
            "Space\tstart/stop the transport\n" +
            "1 – 9\tlaunch scene 1 – 9\n" +
            "Backspace\tstop the selected track\n" +
            "Delete\tremove the clip from the slot\n" +
            "Shift+Delete\tdelete the clip's pattern\n" +
            "Ctrl+Shift+M\tcreate a clip in the selected slot\n" +
            "Ctrl+D\tduplicate the clip into the slot below\n" +
            "F2\trename the clip's pattern\n" +
            "Ctrl+I\tinsert a scene below the selection\n" +
            "Ctrl+Z / Ctrl+Y\tundo / redo grid edits\n" +
            "Esc\tcancel MIDI learn\n" +
            "Ctrl+Tab\tswitch between the Session and Sequence views (from the Session window: back to the main window)\n\n" +
            "Mouse: ▶ launches; click selects; double-click edits a clip or creates one in an empty slot; " +
            "drag a clip to move it within its track (hold Ctrl to copy); right-click for menus and MIDI learn.";

        // ------------------------------------------------------------------ selection and playing

        internal void Select(int track, int scene)
        {
            selected = (track, scene);
            Focus();
        }

        SlotCell CellAt(int track, int scene) => slots.FirstOrDefault(c => c.Track == track && c.Scene == scene);

        void LaunchSlot(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            if (cell.Sequence == null) return;
            if (cell.Pattern != null)
                session.Launch(cell.Sequence, cell.Pattern);
            else
                session.Stop(cell.Sequence);     // an empty slot works as the track's stop button, as in Live
            RefreshState();
        }

        void LaunchScene(int scene)
        {
            layout.LaunchScene(scene);
            RefreshState();
        }

        void StopTrack(int track)
        {
            if (layout.SequenceOf(track) is ISequence s)
                session.Stop(s);
            RefreshState();
        }

        void SlotDoubleClicked(SlotCell cell)
        {
            if (cell.Pattern != null)
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

        void RenameClip(IPattern pattern)
        {
            if (pattern == null) return;
            string name = PromptWindow.Ask(Window.GetWindow(this), "Rename clip", $"New name for pattern '{pattern.Name}' of {pattern.Machine?.Name}", pattern.Name);
            if (name == null) return;
            if (!layout.RenameClip(pattern, name) && name.Trim() != pattern.Name)
                MessageBox.Show(Window.GetWindow(this), $"'{name.Trim()}' is empty or already used by another pattern of {pattern.Machine?.Name}.", "Rename clip", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        void DeleteClip(IPattern pattern)
        {
            if (pattern == null) return;
            bool arranged = buzz.Song.Sequences.Any(s => s.Events.Values.Any(e => e.Pattern == pattern));
            if (arranged)
            {
                var answer = MessageBox.Show(Window.GetWindow(this),
                    $"Pattern '{pattern.Name}' is also used in the arrangement. Deleting it removes it there too, and undo only brings the pattern back.\n\nDelete it?",
                    "Delete pattern", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.Yes) return;
            }
            layout.DeleteClip(pattern);
        }

        void EditPattern(IPattern pattern)
        {
            if (pattern == null) return;
            buzz.SetPatternEditorPattern(pattern);
            buzz.ActiveView = BuzzView.PatternView;
            Window.GetWindow(this)?.Owner?.Activate();
        }

        void InsertSceneIntoArrangement(int scene)
        {
            int ticksPerBar = Math.Max(1, buzz.TPB * session.BeatsPerBar);
            int length = SceneToArrangement.SceneLength(layout, scene, ticksPerBar);
            int position = buzz.SongCore.PlayPosition / ticksPerBar * ticksPerBar;
            var doc = layout.Ensure();
            string name = scene < doc.Scenes.Count && !string.IsNullOrEmpty(doc.Scenes[scene].Name) ? doc.Scenes[scene].Name : $"scene {scene + 1}";
            var answer = ArrangementInsertWindow.Ask(Window.GetWindow(this), name, position / ticksPerBar + 1, length / ticksPerBar);
            if (answer is not { } a) return;
            SceneToArrangement.Insert(buzz, layout, scene, (a.Bar - 1) * ticksPerBar, a.Bars * ticksPerBar, a.Insert);
        }

        void RenameScene(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            string name = PromptWindow.Ask(Window.GetWindow(this), "Rename scene", "Scene name", doc.Scenes[scene].Name);
            if (name != null)
                layout.RenameScene(scene, name);
        }

        // ------------------------------------------------------------------ drag (move/copy within a track)

        void DragOver(SlotCell source, Point position)
        {
            var target = slots.FirstOrDefault(c => c.Track == source.Track && Bounds(c).Contains(position));
            dropTarget = target == null || target.Scene == source.Scene ? null : (target.Track, target.Scene);
        }

        void EndDrag(SlotCell source, Point? position, bool copy)
        {
            if (position is Point p)
                DragOver(source, p);
            var target = dropTarget;
            dropTarget = null;
            if (target is { } t && layout.MoveSlot(source.Track, source.Scene, t.Scene, copy))
                selected = t;
            RefreshState();
        }

        Rect Bounds(FrameworkElement element) =>
            element.TransformToAncestor(this).TransformBounds(new Rect(element.RenderSize));

        // ------------------------------------------------------------------ keyboard

        void OnKeyDown(object sender, KeyEventArgs e) => HandleKeyEvent(e);

        /// <summary>
        /// Handles a key aimed at this view (focus inside it, not typing in a box). The main
        /// window calls this first when the view is docked, since its own shortcuts would
        /// otherwise take Ctrl+Z and friends before the key reaches the view.
        /// </summary>
        internal bool HandleKeyEvent(KeyEventArgs e)
        {
            if (e.Handled || !IsKeyboardFocusWithin) return false;
            if (e.OriginalSource is TextBox) return false;
            if (e.OriginalSource is DependencyObject d && FindParent<ComboBox>(d) is ComboBox combo && combo.IsDropDownOpen) return false;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (!HandleKey(key, Keyboard.Modifiers)) return false;
            e.Handled = true;
            return true;
        }

        /// <summary>Runs a keyboard command; returns whether the key was used.</summary>
        internal bool HandleKey(Key key, ModifierKeys mods)
        {
            bool ctrl = mods == ModifierKeys.Control;
            bool none = mods == ModifierKeys.None;
            var sel = selected;

            if (key == Key.Escape && midi?.IsLearning == true) { midi.CancelLearn(); return true; }

            if (none && key is Key.Left or Key.Right or Key.Up or Key.Down)
            {
                MoveSelection(key);
                return true;
            }
            if (none && key == Key.Space)
            {
                buzz.Playing = !buzz.PlayRequested;
                return true;
            }
            if (none && (key >= Key.D1 && key <= Key.D9 || key >= Key.NumPad1 && key <= Key.NumPad9))
            {
                LaunchScene(key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1);
                return true;
            }
            if (ctrl && key == Key.Z) { layout.Undo(); return true; }
            if ((ctrl && key == Key.Y) || (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Z)) { layout.Redo(); return true; }
            if (ctrl && key == Key.I)
            {
                layout.InsertScene(sel is { } s ? s.Scene + 1 : layout.SceneCount);
                return true;
            }

            if (sel is not { } at) return false;
            var pattern = layout.PatternAt(at.Track, at.Scene);

            if (none && key == Key.Enter)
            {
                if (CellAt(at.Track, at.Scene) is SlotCell cell) LaunchSlot(cell);
                return true;
            }
            if (none && key == Key.Back) { StopTrack(at.Track); return true; }
            if (none && key == Key.Delete) { layout.ClearSlot(at.Track, at.Scene); return true; }
            if (mods == ModifierKeys.Shift && key == Key.Delete) { DeleteClip(pattern); return true; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.M) { CreateClip(at.Track, at.Scene); return true; }
            if (ctrl && key == Key.D) { DuplicateClip(at.Track, at.Scene); return true; }
            if ((none && key == Key.F2) || (ctrl && key == Key.R)) { RenameClip(pattern); return true; }
            return false;
        }

        void MoveSelection(Key key)
        {
            if (trackCount == 0 || sceneCount == 0) return;
            var (t, s) = selected ?? (0, 0);
            if (selected != null)
            {
                if (key == Key.Left) t--;
                else if (key == Key.Right) t++;
                else if (key == Key.Up) s--;
                else if (key == Key.Down) s++;
            }
            selected = (Math.Clamp(t, 0, trackCount - 1), Math.Clamp(s, 0, sceneCount - 1));
            if (CellAt(selected.Value.Track, selected.Value.Scene) is SlotCell cell)
                cell.BringIntoView();
            RefreshState();
        }

        static T FindParent<T>(DependencyObject d) where T : DependencyObject
        {
            while (d != null && d is not T)
                d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
            return d as T;
        }

        // ------------------------------------------------------------------ menus

        void ShowSlotMenu(SlotCell cell)
        {
            Select(cell.Track, cell.Scene);
            var menu = new ContextMenu();
            var pattern = cell.Pattern;
            var machine = cell.Sequence?.Machine;

            if (pattern != null)
            {
                menu.Items.Add(Item("Launch", () => LaunchSlot(cell), "Enter"));
                menu.Items.Add(Item("Edit pattern", () => EditPattern(pattern), "double-click"));
                menu.Items.Add(Item("Rename…", () => RenameClip(pattern), "F2"));
                menu.Items.Add(Item("Duplicate", () => DuplicateClip(cell.Track, cell.Scene), "Ctrl+D"));
                menu.Items.Add(ColorMenu("Colour", PatternColors.Get(pattern), i => layout.SetClipColor(pattern, i)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove from slot", () => layout.ClearSlot(cell.Track, cell.Scene), "Del"));
                menu.Items.Add(Item("Delete pattern…", () => DeleteClip(pattern), "Shift+Del"));
            }
            else
            {
                menu.Items.Add(Item("Create clip", () => CreateClip(cell.Track, cell.Scene), "Ctrl+Shift+M", () => machine != null));
            }

            if (machine != null)
            {
                var assign = new MenuItem { Header = $"Assign pattern of {machine.Name}" };
                foreach (var p in machine.Patterns.ToList())
                {
                    var choice = p;
                    assign.Items.Add(new MenuItem { Header = choice.Name, IsChecked = choice == pattern, Command = new RelayCommand(() => layout.SetSlot(cell.Track, cell.Scene, choice)) });
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

        void ShowSceneMenu(SceneCell cell)
        {
            var doc = layout.Ensure();
            int scene = cell.Scene;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Launch scene", () => LaunchScene(scene), scene < 9 ? (scene + 1).ToString() : null));
            menu.Items.Add(Item("Rename…", () => RenameScene(scene)));
            menu.Items.Add(ColorMenu("Colour", scene < doc.Scenes.Count ? doc.Scenes[scene].ColorIndex : -1, i => layout.SetSceneColor(scene, i)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert scene above", () => layout.InsertScene(scene)));
            menu.Items.Add(Item("Insert scene below", () => layout.InsertScene(scene + 1), "Ctrl+I"));
            menu.Items.Add(Item("Duplicate scene", () => layout.DuplicateScene(scene)));
            menu.Items.Add(Item("Delete scene", () => layout.RemoveScene(scene)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Insert into arrangement…", () => InsertSceneIntoArrangement(scene), null,
                () => doc.SceneSlots(scene).Any()));
            menu.Items.Add(new Separator());
            AddMidiItems(menu, SessionTarget.Scene, 0, scene);
            OpenMenu(menu, cell);
        }

        void ShowTrackMenu(TrackHeader header)
        {
            var doc = layout.Ensure();
            int track = header.Track;
            var menu = new ContextMenu();
            menu.Items.Add(ColorMenu("Track colour", track < doc.Tracks.Count ? doc.Tracks[track].ColorIndex : -1, i => layout.SetTrackColor(track, i)));
            if (header.Machine != null)
            {
                menu.Items.Add(new MenuItem { Header = "Mute", IsChecked = header.Machine.IsMuted, Command = new RelayCommand(() => header.Machine.IsMuted = !header.Machine.IsMuted) });
                menu.Items.Add(new MenuItem { Header = "Solo", IsChecked = header.Machine.IsSoloed, Command = new RelayCommand(() => header.Machine.IsSoloed = !header.Machine.IsSoloed) });
            }
            OpenMenu(menu, header);
        }

        void ShowMidiOnlyMenu(FrameworkElement anchor, SessionTarget target, int track, int scene, string what)
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
                () => midi.Unbind(target, track, scene), null, () => binding != null));
        }

        void ShowAddTrackMenu(FrameworkElement anchor)
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

        void ShowSettingsMenu(FrameworkElement anchor)
        {
            var doc = layout.Ensure();
            var menu = new ContextMenu();

            var length = new MenuItem { Header = "New clip length" };
            foreach (int bars in new[] { 1, 2, 4, 8 })
            {
                int b = bars;
                length.Items.Add(new MenuItem { Header = b == 1 ? "1 bar" : $"{b} bars", IsChecked = doc.ClipBars == b, Command = new RelayCommand(() => layout.SetClipBars(b)) });
            }
            menu.Items.Add(length);

            if (midi != null)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item(midi.ActiveMap?.Grid != null ? "MIDI note grid… (on)" : "MIDI note grid…", ShowNoteGridDialog));
                var move = new MenuItem { Header = "MIDI learn: move the note grid", IsEnabled = midi.ActiveMap?.Grid != null };
                move.Items.Add(Item("Up", () => midi.BeginLearn(SessionTarget.GridUp, 0, 0)));
                move.Items.Add(Item("Down", () => midi.BeginLearn(SessionTarget.GridDown, 0, 0)));
                move.Items.Add(Item("Left", () => midi.BeginLearn(SessionTarget.GridLeft, 0, 0)));
                move.Items.Add(Item("Right", () => midi.BeginLearn(SessionTarget.GridRight, 0, 0)));
                menu.Items.Add(move);
                menu.Items.Add(Item("MIDI learn play/stop", () => midi.BeginLearn(SessionTarget.Transport, 0, 0)));
                menu.Items.Add(Item("MIDI learn launch quantization", () => midi.BeginLearn(SessionTarget.Quantization, 0, 0)));
                menu.Items.Add(new MenuItem
                {
                    Header = "Store MIDI mapping in this song",
                    IsChecked = midi.UsesSongMap,
                    ToolTip = "On: this song keeps its own mapping, used instead of yours when it's open.\nOff: the song uses your mapping (shared by all songs).",
                    Command = new RelayCommand(() => midi.SetUseSongMap(!midi.UsesSongMap))
                });
                menu.Items.Add(Item("Clear all MIDI mappings", () => midi.ClearAll(), null, () => midi.ActiveMap?.IsEmpty == false));
            }

            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Keyboard shortcuts…", () =>
                MessageBox.Show(Window.GetWindow(this), KeyboardHelp, "Session View shortcuts", MessageBoxButton.OK, MessageBoxImage.None)));
            OpenMenu(menu, anchor);
        }

        void ShowNoteGridDialog()
        {
            if (midi == null) return;
            var current = midi.ActiveMap?.Grid;
            var origin = selected ?? (0, 0);
            var result = NoteGridWindow.Ask(Window.GetWindow(this), buzz, current, origin.Track, origin.Scene);
            if (result.Remove)
                midi.SetGrid(null);
            else if (result.Grid != null)
                midi.SetGrid(result.Grid);
        }

        MenuItem ColorMenu(string header, int current, Action<int> set)
        {
            var item = new MenuItem { Header = header };
            item.Items.Add(new MenuItem { Header = "None", IsChecked = current < 0, Command = new RelayCommand(() => set(-1)) });
            var choices = SessionPalette.ColorChoices;
            for (int i = 0; i < choices.Length; i++)
            {
                int index = i;
                item.Items.Add(new MenuItem
                {
                    Header = choices[i].Name,
                    IsChecked = current >= 0 && current % choices.Length == i,
                    Icon = new Rectangle { Width = 14, Height = 14, Fill = SessionPalette.BrushOf(choices[i].Color) },
                    Command = new RelayCommand(() => set(index))
                });
            }
            return item;
        }

        static MenuItem Item(string header, Action action, string gesture = null, Func<bool> canExecute = null) =>
            new MenuItem { Header = header, InputGestureText = gesture ?? "", Command = new RelayCommand(action, canExecute) };

        static void OpenMenu(ContextMenu menu, FrameworkElement anchor)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}
