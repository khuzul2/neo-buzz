using BuzzGUI.Interfaces;
using ReBuzz.Core;
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
            "Enter\tlaunch the selected slot (stops the track if empty); release matters for Gate and Repeat clips\n" +
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
            "Ctrl+Alt+Z\tundo the last launch (the previous clip comes back in step)\n" +
            "Ctrl+Alt+M\tshow / hide the mixer\n" +
            "Esc\tcancel MIDI learn\n" +
            "Ctrl+Tab\tswitch between the Session and Sequence views (from the Session window: back to the main window)\n\n" +
            "Mouse: ▶ launches (hold it for Gate and Repeat clips); click selects and shows the clip in the clip panel; " +
            "double-click edits a clip or creates one in an empty slot; " +
            "drag a clip to move it within its track (hold Ctrl to copy); right-click for menus and MIDI learn.\n" +
            "Mixer: drag a fader, pan or send (Shift for fine), use the wheel, double-click to reset.";

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
        void LaunchSlot(SlotCell cell)
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

        SlotCell enterHeld;     // slot launched with Enter, released on key up

        void OnKeyUp(object sender, KeyEventArgs e)
        {
            if (e.OriginalSource is not TextBox && HandleKeyUp(e.Key))
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

        void LaunchScene(int scene)
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

        void StopTrack(int track)
        {
            if (layout.SequenceOf(track) is ISequence s)
            {
                layout.StopRecording(s);                     // 0.7: a take recording until stopped gets its length
                session.Stop(s);
            }
            RefreshState();
        }

        /// <summary>The clip's notes in the piano roll (0.7.1).</summary>
        void EditNotes(ISequence track, IPattern pattern) =>
            PianoRollWindow.Show(Window.GetWindow(this), buzz, layout, track, pattern);

        /// <summary>Moves the clip's notes to its MIDI note lanes in the Pattern View, or back (0.7.1).</summary>
        void MoveNotes(ISequence track, IPattern pattern, bool toPatternView)
        {
            var problem = toPatternView ? layout.MoveNotesToPatternView(track, pattern) : layout.MoveNotesFromPatternView(track, pattern);
            if (problem != null)
                MessageBox.Show(Window.GetWindow(this), problem, toPatternView ? "Move notes to the Pattern View" : "Move notes from the Pattern View",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshState();
        }

        // ------------------------------------------------------------------ recording (0.7)

        void ToggleArm(int track)
        {
            layout.SetArmed(track, !layout.IsArmed(track));
            RefreshState();
        }

        void RecordInto(SlotCell cell)
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

        void SlotDoubleClicked(SlotCell cell)
        {
            if (cell.Pattern != null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
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

        /// <summary>Scene tempo (0.5): set when the scene launches.</summary>
        void AskSceneTempo(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            int current = doc.Scenes[scene].Tempo;
            string text = PromptWindow.Ask(Window.GetWindow(this), "Scene tempo",
                $"Tempo (BPM, 16–500) set when the scene launches, on its launch beat.\nEmpty for none. Now: {buzz.BPM} BPM.",
                current > 0 ? current.ToString() : "");
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) { layout.SetSceneTempo(scene, 0); return; }
            if (int.TryParse(text, out int bpm) && bpm >= 16 && bpm <= 500)
                layout.SetSceneTempo(scene, bpm);
            else
                MessageBox.Show(Window.GetWindow(this), $"'{text}' is not a tempo between 16 and 500.", "Scene tempo", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>Scene time signature (0.5): beats per bar set when the scene launches.</summary>
        void AskSceneBeats(int scene)
        {
            var doc = layout.Ensure();
            if (scene < 0 || scene >= doc.Scenes.Count) return;
            int current = doc.Scenes[scene].BeatsPerBar;
            string text = PromptWindow.Ask(Window.GetWindow(this), "Scene beats per bar",
                "Beats per bar (1–64) set when the scene launches; bars count from there.\nEmpty for none.",
                current > 0 ? current.ToString() : "");
            if (text == null) return;
            text = text.Trim();
            if (text.Length == 0) { layout.SetSceneBeatsPerBar(scene, 0); return; }
            if (int.TryParse(text, out int beats) && beats >= 1 && beats <= 64)
                layout.SetSceneBeatsPerBar(scene, beats);
            else
                MessageBox.Show(Window.GetWindow(this), $"'{text}' is not a number of beats between 1 and 64.", "Scene beats per bar", MessageBoxButton.OK, MessageBoxImage.Information);
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
            // Held keys repeat: a held Enter or scene key launches once (Repeat clips repeat by themselves).
            if (e.IsRepeat && Keyboard.Modifiers == ModifierKeys.None &&
                (key == Key.Enter && selected != null || key >= Key.D1 && key <= Key.D9 || key >= Key.NumPad1 && key <= Key.NumPad9))
            {
                e.Handled = true;
                return true;
            }
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
                // The main window takes Space first in every view (TransportKeys); the same command.
                TransportCommands.TogglePlaying(buzz);
                return true;
            }
            if (none && (key >= Key.D1 && key <= Key.D9 || key >= Key.NumPad1 && key <= Key.NumPad9))
            {
                LaunchScene(key >= Key.NumPad1 ? key - Key.NumPad1 : key - Key.D1);
                return true;
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.M) { ToggleMixer(); return true; }
            if (mods == (ModifierKeys.Control | ModifierKeys.Alt) && key == Key.Z) { UndoLaunch(); return true; }
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
                ReleaseHeldKey();
                if (CellAt(at.Track, at.Scene) is SlotCell cell)
                {
                    LaunchSlot(cell);
                    enterHeld = cell.Pattern != null ? cell : null;
                }
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
                menu.Items.Add(Item("Clip settings", ShowClipPanel));
                menu.Items.Add(Item("Edit pattern", () => EditPattern(pattern), "double-click"));
                menu.Items.Add(Item("Edit notes (piano roll)…", () => EditNotes(cell.Sequence, pattern), "Ctrl+double-click"));
                if (layout.CanMoveNotes(pattern))
                {
                    int clipNotes = layout.NotesOf(pattern).Count;
                    if (clipNotes > 0)
                        menu.Items.Add(Item($"Move notes to the Pattern View ({clipNotes})", () => MoveNotes(cell.Sequence, pattern, toPatternView: true)));
                    int laneNotes = layout.PatternViewNotesOf(pattern).Count;
                    if (laneNotes > 0)
                        menu.Items.Add(Item($"Move notes from the Pattern View ({laneNotes})", () => MoveNotes(cell.Sequence, pattern, toPatternView: false)));
                }
                menu.Items.Add(Item("Rename…", () => RenameClip(pattern), "F2"));
                menu.Items.Add(Item("Duplicate", () => DuplicateClip(cell.Track, cell.Scene), "Ctrl+D"));
                menu.Items.Add(ColorMenu("Colour", PatternColors.Get(pattern), i => layout.SetClipColor(pattern, i)));
                int notes = layout.RecordedNoteCount(pattern);
                if (notes > 0)
                    menu.Items.Add(Item($"Clear recorded notes ({notes})", () => layout.ClearRecordedNotes(pattern)));
                int moves = layout.RecordedAutomationCount(pattern);
                if (moves > 0)
                    menu.Items.Add(Item($"Clear recorded automation ({moves})", () => layout.ClearRecordedAutomation(pattern)));
                menu.Items.Add(new Separator());
                menu.Items.Add(Item("Remove from slot", () => layout.ClearSlot(cell.Track, cell.Scene), "Del"));
                menu.Items.Add(Item("Delete pattern…", () => DeleteClip(pattern), "Shift+Del"));
            }
            else
            {
                menu.Items.Add(Item("Create clip", () => CreateClip(cell.Track, cell.Scene), "Ctrl+Shift+M", () => machine != null));
                menu.Items.Add(Item("Record a take here", () => RecordInto(cell), null, () => layout.IsArmed(cell.Track)));
            }
            // 1.0: audio files into the slot (a new audio track for them if this track isn't one).
            menu.Items.Add(Item(SessionLayout.IsClipPlayer(machine) ? "Import audio…" : "Import audio (new audio track)…",
                () => ImportAudioInto(cell.Track, cell.Scene), "or drop files", () => layout.CanMakeAudioTracks));
            if (pattern != null && layout.IsAudioClip(pattern))
                menu.Items.Add(Item("Warp markers…", () => EditWarpMarkers(cell.Sequence, pattern)));

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

        static string ClipCellFollow(SessionDocument.FollowSettings f) => SlotCell.FollowSummary(f, false);

        void ShowSceneMenu(SceneCell cell)
        {
            var doc = layout.Ensure();
            int scene = cell.Scene;
            var menu = new ContextMenu();
            menu.Items.Add(Item("Launch scene", () => LaunchScene(scene), scene < 9 ? (scene + 1).ToString() : null));
            menu.Items.Add(Item("Rename…", () => RenameScene(scene)));
            menu.Items.Add(ColorMenu("Colour", scene < doc.Scenes.Count ? doc.Scenes[scene].ColorIndex : -1, i => layout.SetSceneColor(scene, i)));
            var info = scene < doc.Scenes.Count ? doc.Scenes[scene] : null;
            menu.Items.Add(Item(info?.Tempo > 0 ? $"Tempo… ({info.Tempo} BPM)" : "Tempo…", () => AskSceneTempo(scene)));
            menu.Items.Add(Item(info?.BeatsPerBar > 0 ? $"Beats per bar… ({info.BeatsPerBar})" : "Beats per bar…", () => AskSceneBeats(scene)));
            menu.Items.Add(Item(info?.Follow?.On == true ? $"Follow action… ({ClipCellFollow(info.Follow)})" : "Follow action…", () =>
            {
                Select(selected?.Track ?? 0, scene);
                ShowClipPanel();
            }));
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
                if (SessionLayout.IsClipPlayer(header.Machine))
                {
                    // 1.0: what an audio track's takes record.
                    var machine = header.Machine;
                    bool master = SessionLayout.IsResampling(machine);
                    menu.Items.Add(new Separator());
                    menu.Items.Add(new MenuItem { Header = "Record its input (machines connected to it)", IsChecked = !master, Command = new RelayCommand(() => SessionLayout.SetInputOf(machine, 0)) });
                    menu.Items.Add(new MenuItem { Header = "Record the master (resampling)", IsChecked = master, Command = new RelayCommand(() => SessionLayout.SetInputOf(machine, 1)) });
                }
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
            // 1.0: an audio track (a Session Clip Player): drop audio files on its slots.
            menu.Items.Add(Item("New audio track", NewAudioTrack, null, () => layout.CanMakeAudioTracks));
            menu.Items.Add(Item("New resampling track (records the master)", NewResamplingTrack, null, () => layout.CanMakeAudioTracks));
            menu.Items.Add(new Separator());
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

            // 0.7: recording.
            var takeLength = new MenuItem { Header = "Record length" };
            foreach (int bars in new[] { 0, 1, 2, 4, 8 })
            {
                int b = bars;
                takeLength.Items.Add(new MenuItem
                {
                    Header = b == 0 ? "Until stopped" : b == 1 ? "1 bar" : $"{b} bars",
                    IsChecked = doc.RecordBars == b,
                    Command = new RelayCommand(() => layout.SetRecordBars(b))
                });
            }
            menu.Items.Add(takeLength);
            var countIn = new MenuItem { Header = "Count-in" };
            foreach (int bars in new[] { 0, 1, 2 })
            {
                int b = bars;
                countIn.Items.Add(new MenuItem
                {
                    Header = b == 0 ? "None" : b == 1 ? "1 bar" : $"{b} bars",
                    IsChecked = doc.CountInBars == b,
                    Command = new RelayCommand(() => layout.SetCountInBars(b))
                });
            }
            menu.Items.Add(countIn);
            menu.Items.Add(new MenuItem
            {
                Header = "Metronome",
                IsChecked = doc.Metronome,
                ToolTip = "A click on every beat while the session plays (louder on the bar line). Heard, never recorded.",
                Command = new RelayCommand(() => layout.SetMetronome(!doc.Metronome))
            });
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem
            {
                Header = "Scenes stop other clips",
                IsChecked = doc.ScenesStopOtherClips,
                ToolTip = "On: launching a scene also stops the tracks it has no clip for (as if its empty slots\n" +
                          "had stop buttons). Off: those tracks play on. Saved with the song.",
                Command = new RelayCommand(() => layout.SetScenesStopOtherClips(!doc.ScenesStopOtherClips))
            });
            if (session is SessionCore performer)
            {
                menu.Items.Add(new MenuItem
                {
                    Header = "Select on launch",
                    IsChecked = performer.SelectOnLaunch,
                    ToolTip = "On: launching a clip or scene (mouse, keys or MIDI) selects it and shows it in the clip panel.",
                    Command = new RelayCommand(() => performer.SetSelectOnLaunch(!performer.SelectOnLaunch))
                });
            }
            if (session is SessionCore core)
            {
                double ms = buzz.OutputLatencySamples * 1000.0 / Math.Max(1, ReBuzzCore.masterInfo.SamplesPerSec);
                menu.Items.Add(new MenuItem
                {
                    Header = ms > 0 ? $"Time launches to what you hear ({ms:F0} ms latency)" : "Time launches to what you hear",
                    IsChecked = core.LatencyCompensation,
                    ToolTip = "On: a launch pressed just before a beat or bar line, as you hear it, lands on that line, even if\n" +
                              "the engine has already rendered past it (the clip starts in step, skipping what already played).\n" +
                              "Off: launches wait for the next line after what the engine has rendered.",
                    Command = new RelayCommand(() => core.SetLatencyCompensation(!core.LatencyCompensation))
                });
            }
            AddSyncMenu(menu);
            AddControllersMenu(menu);
            menu.Items.Add(new Separator());
            AddMixerMenuItems(menu, doc);

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
                var perform = new MenuItem { Header = "MIDI learn: performance" };
                perform.Items.Add(Item("Undo the last launch", () => midi.BeginLearn(SessionTarget.UndoLaunch, 0, 0)));
                perform.Items.Add(Item("Follow actions on/off", () => midi.BeginLearn(SessionTarget.FollowActions, 0, 0)));
                perform.Items.Add(Item("Tap tempo", () => midi.BeginLearn(SessionTarget.TapTempo, 0, 0)));
                perform.Items.Add(Item("Nudge down (hold)", () => midi.BeginLearn(SessionTarget.NudgeDown, 0, 0)));
                perform.Items.Add(Item("Nudge up (hold)", () => midi.BeginLearn(SessionTarget.NudgeUp, 0, 0)));
                perform.Items.Add(Item("Crossfader (a knob or fader)", () => midi.BeginLearn(SessionTarget.Crossfader, 0, 0)));
                menu.Items.Add(perform);
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

        /// <summary>Control surfaces (0.8): the controllers the MIDI ports offer, on or off.</summary>
        void AddControllersMenu(ContextMenu menu)
        {
            var controllers = buzz.SessionControllers;
            if (controllers == null) return;
            var item = new MenuItem { Header = "Controllers" };
            var available = controllers.Available();
            foreach (var candidate in available)
            {
                var script = candidate.Script;
                bool on = controllers.Wanted.Contains(script.Name);
                bool connected = controllers.IsConnected(script.Name);
                item.Items.Add(new MenuItem
                {
                    Header = $"{script.Name} ({candidate.InputName})" + (on && !connected ? " (not answering)" : ""),
                    IsChecked = on,
                    ToolTip = "On: its pads launch the slots in its session ring (the red box), its lights follow the session.\n" +
                              "Its MIDI ports are then its own: leave them off in " + Product.Name + "'s MIDI settings.",
                    Command = new RelayCommand(() =>
                    {
                        if (on) controllers.TurnOff(script);
                        else
                        {
                            string problem = controllers.TurnOn(script);
                            if (problem != null)
                                MessageBox.Show(Window.GetWindow(this), problem, script.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                        }
                        RefreshState();
                    })
                });
            }
            foreach (var name in controllers.Wanted.Where(n => available.All(a => a.Script.Name != n)).ToList())
            {
                var script = controllers.Scripts.FirstOrDefault(s => s.Name == name);
                if (script == null) continue;
                item.Items.Add(new MenuItem { Header = $"{name} (not connected)", IsChecked = true, Command = new RelayCommand(() => controllers.TurnOff(script)) });
            }
            if (item.Items.Count == 0)
                item.Items.Add(new MenuItem
                {
                    Header = "No controller found",
                    IsEnabled = false,
                    ToolTip = "Known: " + string.Join(", ", controllers.Scripts.Select(s => s.Name)) +
                              ".\nMore can be added as scripts (.dll) in the Controllers folder next to " + Product.WindowsExe + "."
                });
            foreach (var problem in controllers.ScriptProblems)
                item.Items.Add(new MenuItem { Header = "Script not loaded: " + problem, IsEnabled = false });
            menu.Items.Add(item);
        }

        /// <summary>Sync (0.8): Ableton Link, MIDI clock in and out.</summary>
        void AddSyncMenu(ContextMenu menu)
        {
            var sync = buzz.SessionSync;
            if (sync == null) return;
            var item = new MenuItem { Header = sync.Status.Length > 0 ? $"Sync ({sync.Status})" : "Sync" };
            void Report(string title)
            {
                if (sync.Problem != null)
                    MessageBox.Show(Window.GetWindow(this), sync.Problem, title, MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshState();
            }
            item.Items.Add(new MenuItem
            {
                Header = !sync.LinkEnabled ? "Ableton Link" : sync.LinkPeers > 0 ? $"Ableton Link ({sync.LinkPeers} peer{(sync.LinkPeers == 1 ? "" : "s")})" : "Ableton Link (no peers yet)",
                IsChecked = sync.LinkEnabled,
                ToolTip = "On: tempo and bar phase follow Live and other Link apps on the network; alone, they follow the session.\n" +
                          "A start waits for Link's next bar line, so it lands in step. Runs Link in a helper program (ReBuzzLink).",
                Command = new RelayCommand(() => { sync.SetLink(!sync.LinkEnabled); Report("Ableton Link"); })
            });
            item.Items.Add(new MenuItem
            {
                Header = "Start and stop with Link peers",
                IsChecked = sync.LinkStartStop,
                IsEnabled = sync.LinkEnabled,
                ToolTip = "On: play and stop are shared with peers that have start/stop sync on (Live: Link › Start Stop Sync).",
                Command = new RelayCommand(() => sync.SetLinkStartStop(!sync.LinkStartStop))
            });
            item.Items.Add(new Separator());
            item.Items.Add(new MenuItem
            {
                Header = "Follow MIDI clock in",
                IsChecked = sync.FollowMidiClock,
                ToolTip = "On: tempo and beat follow the MIDI clock coming in (any open MIDI input), and so do Start, Continue and Stop.\n" +
                          "Turns Link off.",
                Command = new RelayCommand(() => { sync.SetFollowMidiClock(!sync.FollowMidiClock); Report("MIDI clock in"); })
            });
            var outs = new MenuItem { Header = "Send MIDI clock to" };
            var open = sync.Outputs;
            foreach (var (_, name) in open)
            {
                string device = name;
                bool on = sync.ClockOutNames.Contains(device);
                outs.Items.Add(new MenuItem { Header = device, IsChecked = on, Command = new RelayCommand(() => sync.SetClockOut(device, !on)) });
            }
            foreach (var missing in sync.ClockOutNames.Where(n => open.All(o => o.Name != n)).ToList())
                outs.Items.Add(new MenuItem { Header = $"{missing} (not open)", IsChecked = true, Command = new RelayCommand(() => sync.SetClockOut(missing, false)) });
            if (outs.Items.Count == 0)
                outs.Items.Add(new MenuItem { Header = "No MIDI outputs open (Preferences › MIDI)", IsEnabled = false });
            item.Items.Add(outs);
            menu.Items.Add(new Separator());
            menu.Items.Add(item);
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
