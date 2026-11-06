using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ReBuzz.AppViews.Session
{
    internal sealed partial class SessionView
    {
        /// <summary>
        /// One slot of the grid. A clip shows its pattern name on its colour; its ▶ area launches
        /// on mouse down (and releases on mouse up, for Gate and Repeat clips), the rest selects
        /// (double-click edits, drag moves within the track). Clips with launch settings show
        /// them in short on the right (0.5). An empty slot has a small stop button for its
        /// track; double-click creates a clip.
        /// </summary>
        internal sealed class SlotCell : Border
        {
            const double DragThreshold = 4;

            readonly SessionView view;
            readonly Rectangle progress;
            readonly TextBlock glyph;
            readonly TextBlock label;
            readonly TextBlock flags;
            readonly FrameworkElement stopButton;     // empty slot: stops the track
            readonly FrameworkElement recordButton;   // empty slot of an armed track: records a take (0.7)
            bool hover;
            Point? pressPoint;
            bool dragging;

            public int Track { get; }
            public int Scene { get; }
            public ISequence Sequence { get; }
            public IPattern Pattern { get; }

            public SlotCell(SessionView view, int track, int scene, ISequence sequence, IPattern pattern, string midiText,
                            SessionDocument.ClipSettings settings = null)
            {
                this.view = view;
                Track = track;
                Scene = scene;
                Sequence = sequence;
                Pattern = pattern;

                Margin = new Thickness(0, 0, Gap, Gap);
                BorderThickness = new Thickness(1);
                SnapsToDevicePixels = true;
                Background = SessionPalette.Empty;
                ToolTip = Tip(pattern, midiText, settings);

                progress = new Rectangle
                {
                    Fill = SessionPalette.Progress,
                    Height = 3,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Width = 0,
                    IsHitTestVisible = false
                };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                if (pattern != null)
                {
                    glyph = new TextBlock { Text = "▶", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                    var launchArea = new Border { Background = SessionPalette.Transparent, Cursor = Cursors.Hand, Child = glyph };
                    // Press launches; the release (button up, or the capture lost) is the clip's release.
                    bool held = false;
                    launchArea.MouseLeftButtonDown += (s, e) =>
                    {
                        view.LaunchSlot(this);
                        held = launchArea.CaptureMouse();
                        e.Handled = true;
                    };
                    launchArea.MouseLeftButtonUp += (s, e) =>
                    {
                        if (launchArea.IsMouseCaptured) launchArea.ReleaseMouseCapture();
                        e.Handled = true;
                    };
                    launchArea.LostMouseCapture += (s, e) =>
                    {
                        if (held)
                        {
                            held = false;
                            view.ReleaseSlot(this);
                        }
                        e.Handled = true;
                    };
                    Grid.SetColumn(launchArea, 0);
                    grid.Children.Add(launchArea);

                    string marks = Flags(settings);
                    if (marks.Length > 0)
                    {
                        flags = new TextBlock
                        {
                            Text = marks,
                            FontSize = 9,
                            Margin = new Thickness(0, 0, 4, 0),
                            VerticalAlignment = VerticalAlignment.Center,
                            Opacity = 0.75,
                            IsHitTestVisible = false
                        };
                        Grid.SetColumn(flags, 2);
                        grid.Children.Add(flags);
                    }

                    label = new TextBlock
                    {
                        Text = (settings?.Audio != null ? "∿ " : "") + pattern.Name,         // 1.0: an audio clip
                        Margin = new Thickness(0, 0, 4, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        IsHitTestVisible = false
                    };
                    Grid.SetColumn(label, 1);
                    grid.Children.Add(label);
                }
                else
                {
                    var stop = new Border
                    {
                        Width = 13,
                        Height = 13,
                        Background = SessionPalette.Header,
                        Cursor = Cursors.Hand,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        ToolTip = "Stop this track",
                        Child = new Rectangle { Width = 5, Height = 5, Fill = SessionPalette.DimText, IsHitTestVisible = false }
                    };
                    stop.MouseLeftButtonDown += (s, e) => { view.Select(Track, Scene); view.StopTrack(Track); e.Handled = true; };
                    Grid.SetColumnSpan(stop, 3);
                    grid.Children.Add(stop);
                    stopButton = stop;

                    var record = new Border
                    {
                        Width = 13,
                        Height = 13,
                        Background = SessionPalette.Header,
                        Cursor = Cursors.Hand,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Visibility = Visibility.Collapsed,
                        ToolTip = "Record a take here (the track is armed): a new clip, recording what you play\n" +
                                  "from the next launch point (⚙: record length, count-in, metronome)",
                        Child = new Ellipse { Width = 7, Height = 7, Fill = SessionPalette.Record, IsHitTestVisible = false }
                    };
                    record.MouseLeftButtonDown += (s, e) => { view.RecordInto(this); e.Handled = true; };
                    Grid.SetColumnSpan(record, 3);
                    grid.Children.Add(record);
                    recordButton = record;
                }

                Grid.SetColumnSpan(progress, 3);
                grid.Children.Add(progress);
                Child = grid;

                MouseEnter += (s, e) => hover = true;
                MouseLeave += (s, e) => hover = false;
                MouseLeftButtonDown += OnLeftDown;
                MouseMove += OnMove;
                MouseLeftButtonUp += OnLeftUp;
                LostMouseCapture += (s, e) => EndDrag(null, false);
                MouseRightButtonUp += (s, e) => { view.ShowSlotMenu(this); e.Handled = true; };
            }

            static string Tip(IPattern pattern, string midiText, SessionDocument.ClipSettings settings)
            {
                string tip = pattern != null
                    ? $"{pattern.Name} ({pattern.Length} ticks)\n▶ launches · double-click edits · drag moves (Ctrl copies) · right-click for more"
                    : "Empty slot\n■ stops the track · double-click creates a clip · right-click for more";
                if (pattern != null && settings != null && !settings.IsDefault)
                    tip += "\n" + Summary(settings);
                return midiText == null ? tip : tip + "\n" + midiText;
            }

            /// <summary>Short marks for non-default clip settings: G T R (mode), L (legato), 1× (no loop), v (velocity), ◆ (markers), q (quantization), → (follow action).</summary>
            internal static string Flags(SessionDocument.ClipSettings c)
            {
                if (c == null || c.IsDefault) return "";
                var parts = new System.Collections.Generic.List<string>();
                if (c.Mode != nameof(ClipLaunchMode.Trigger) && c.Mode.Length > 0) parts.Add(c.Mode.Substring(0, 1));
                if (c.Legato) parts.Add("L");
                if (!c.Loop) parts.Add("1×");
                if (c.Start != 0 || c.LoopStart != 0 || c.LoopEnd != 0) parts.Add("◆");
                if (c.Velocity != 0) parts.Add("v");
                if (c.Quantization != SessionDocument.ClipSettings.GlobalQuantization) parts.Add("q");
                if (c.Follow?.On == true) parts.Add("→");
                return string.Join(" ", parts);
            }

            static string Summary(SessionDocument.ClipSettings c)
            {
                var parts = new System.Collections.Generic.List<string> { c.Mode };
                if (c.Quantization != SessionDocument.ClipSettings.GlobalQuantization)
                {
                    int i = Array.FindIndex(ClipPanel.Quantizations, q => q.Value == c.Quantization);
                    parts.Add("quantize " + (i >= 0 ? ClipPanel.Quantizations[i].Label : c.Quantization));
                }
                if (c.Legato) parts.Add("legato");
                parts.Add(c.Loop ? "loop" : "one shot");
                if (c.Start != 0 || c.LoopStart != 0 || c.LoopEnd != 0)
                    parts.Add($"start {c.Start}, loop {c.LoopStart}–{(c.LoopEnd == 0 ? "end" : c.LoopEnd.ToString())}");
                if (c.Velocity != 0) parts.Add($"velocity {c.Velocity}%");
                string text = "Clip: " + string.Join(" · ", parts);
                if (c.Audio is { } a)
                    text += $"\nAudio: wave {a.Wave + 1:X2} · {a.Bpm:0.##} BPM · {a.Warp}" + (a.Transpose != 0 || a.Detune != 0 ? $" · {a.Semitones:+0.##;-0.##} st" : "") +
                            (a.GainDb != 0 ? $" · {a.GainDb:+0.#;-0.#} dB" : "") + (a.Markers.Count >= 2 ? $" · {a.Markers.Count} warp markers" : "");
                if (c.Follow?.On == true) text += "\nFollow action: " + FollowSummary(c.Follow, true);
                return text;
            }

            /// <summary>"Next 70% / Other 30%, after 2 loops" (0.6).</summary>
            internal static string FollowSummary(SessionDocument.FollowSettings f, bool clip)
            {
                string Name(string action, int jump)
                {
                    int i = Array.FindIndex(ClipPanel.FollowEditor.Actions, x => x.Value == action);
                    string label = i >= 0 ? ClipPanel.FollowEditor.Actions[i].Label : action;
                    return action == "Jump" && jump >= 0 ? $"{label} to {jump + 1}" : label;
                }
                string actions = f.ChanceA >= 100 ? Name(f.A, f.JumpA)
                    : f.ChanceA <= 0 ? Name(f.B, f.JumpB)
                    : $"{Name(f.A, f.JumpA)} {f.ChanceA}% / {Name(f.B, f.JumpB)} {100 - f.ChanceA}%";
                string when = clip && f.Linked
                    ? (f.Multiplier > 1 ? $"after {f.Multiplier} loops" : "at the end")
                    : $"after {f.Time}";
                return actions + ", " + when;
            }

            void OnLeftDown(object sender, MouseButtonEventArgs e)
            {
                view.Select(Track, Scene);
                if (e.ClickCount == 2)
                {
                    view.SlotDoubleClicked(this);
                }
                else if (Pattern != null)
                {
                    pressPoint = e.GetPosition(view);
                    CaptureMouse();
                }
                e.Handled = true;
            }

            void OnMove(object sender, MouseEventArgs e)
            {
                if (pressPoint is not Point start || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(view);
                if (!dragging && (Math.Abs(p.X - start.X) > DragThreshold || Math.Abs(p.Y - start.Y) > DragThreshold))
                    dragging = true;
                if (dragging)
                    view.DragOver(this, p);
            }

            void OnLeftUp(object sender, MouseButtonEventArgs e)
            {
                if (pressPoint == null) return;
                EndDrag(e.GetPosition(view), Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
                e.Handled = true;
            }

            void EndDrag(Point? drop, bool copy)
            {
                bool wasDragging = dragging;
                pressPoint = null;
                dragging = false;
                if (IsMouseCaptured) ReleaseMouseCapture();
                if (wasDragging)
                    view.EndDrag(this, drop, copy);
            }

            public void Refresh(ISession session, bool blinkOn, Brush border)
            {
                if (Pattern == null || Sequence == null)
                {
                    Background = hover ? SessionPalette.EmptyHover : SessionPalette.Empty;
                    BorderBrush = border ?? SessionPalette.Transparent;
                    if (stopButton != null && recordButton != null)
                    {
                        bool armed = Sequence != null && view.layout.IsArmedTrack(Sequence);
                        stopButton.Visibility = armed ? Visibility.Collapsed : Visibility.Visible;
                        recordButton.Visibility = armed ? Visibility.Visible : Visibility.Collapsed;
                    }
                    return;
                }

                var color = SessionPalette.ColorBrush(PatternColors.Get(Pattern));
                var state = view.layout.SlotState(Sequence, Pattern, Track, Scene);   // only the slot it plays from
                bool sounding = state == ClipState.Playing || state == ClipState.Stopping;
                // Uncoloured clips turn green while playing (as in 0.1); coloured ones keep their
                // colour, lightened, with the green ▶, outline and progress bar.
                var background = color == null
                    ? (sounding ? SessionPalette.Playing : SessionPalette.Clip)
                    : (sounding ? SessionPalette.Lighten(color, 0.2) : color);
                if (hover) background = SessionPalette.Lighten(background, 0.1);
                var text = SessionPalette.TextOn(background);

                Background = background;
                label.Foreground = text;
                if (flags != null) flags.Foreground = text;
                switch (state)
                {
                    case ClipState.Playing:
                        glyph.Foreground = color == null ? text : SessionPalette.Progress;
                        border ??= color == null ? null : SessionPalette.Playing;
                        break;
                    case ClipState.Queued:
                        glyph.Foreground = blinkOn ? SessionPalette.Progress : text;
                        border ??= blinkOn ? SessionPalette.Progress : null;
                        break;
                    case ClipState.Stopping:
                        glyph.Foreground = blinkOn ? SessionPalette.Stopping : (color == null ? text : SessionPalette.Progress);
                        border ??= color == null ? null : SessionPalette.Playing;
                        break;
                    default:
                        glyph.Foreground = text;
                        break;
                }
                // 0.7: a take waiting or recording, or the clip an armed track overdubs: red.
                if (view.layout.IsRecordingInto(Sequence, Pattern))
                {
                    glyph.Text = "●";
                    glyph.Foreground = state == ClipState.Playing || blinkOn ? SessionPalette.Record : text;
                    border ??= SessionPalette.Record;
                }
                else if (glyph.Text != "▶")
                {
                    glyph.Text = "▶";
                }
                BorderBrush = border ?? SessionPalette.Transparent;

                if (state == ClipState.Playing || state == ClipState.Stopping)
                {
                    int pos = session.GetClipPosition(Sequence);
                    int len = Math.Max(1, Pattern.Length);
                    progress.Width = pos < 0 ? 0 : Math.Max(0, ActualWidth - 2) * (pos + 1) / len;
                }
                else
                {
                    progress.Width = 0;
                }
            }
        }

        /// <summary>Scene launch button: colour strip, ▶ and name.</summary>
        internal sealed class SceneCell : Border
        {
            public int Scene { get; }

            public SceneCell(SessionView view, int scene, SessionDocument.Scene info, string midiText)
            {
                Scene = scene;
                Margin = new Thickness(0, 0, 0, Gap);
                BorderThickness = new Thickness(1);
                Background = SessionPalette.Scene;
                Cursor = Cursors.Hand;
                string name = info?.Name;
                string settings = Settings(info);
                ToolTip = $"Scene {scene + 1}: click launches the row" + (scene < 9 ? $" (key {scene + 1})" : "") +
                          (settings.Length > 0 ? $"\nSets {settings} on its launch beat" : "") +
                          "\nright-click for more (name, colour, tempo, beats per bar)" + (midiText == null ? "" : "\n" + midiText);

                var strip = new Rectangle { Width = 4, Fill = SessionPalette.ColorBrush(info?.ColorIndex ?? -1) ?? SessionPalette.Transparent, IsHitTestVisible = false };
                var text = new TextBlock
                {
                    Text = "▶  " + (string.IsNullOrEmpty(name) ? (scene + 1).ToString() : name),
                    Margin = new Thickness(6, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = string.IsNullOrEmpty(name) ? SessionPalette.DimText : SessionPalette.Text,
                    IsHitTestVisible = false
                };
                var panel = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(strip, Dock.Left);
                panel.Children.Add(strip);
                if (settings.Length > 0)
                {
                    var tag = new TextBlock
                    {
                        Text = settings,
                        FontSize = 10,
                        Foreground = SessionPalette.DimText,
                        Margin = new Thickness(0, 0, 5, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    };
                    DockPanel.SetDock(tag, Dock.Right);
                    panel.Children.Add(tag);
                }
                panel.Children.Add(text);
                Child = panel;

                MouseEnter += (s, e) => Background = SessionPalette.SceneHover;
                MouseLeave += (s, e) => Background = SessionPalette.Scene;
                MouseLeftButtonDown += (s, e) => { view.LaunchScene(Scene); e.Handled = true; };
                MouseRightButtonUp += (s, e) => { view.ShowSceneMenu(this); e.Handled = true; };
            }

            public void Refresh(Brush border) => BorderBrush = border ?? SessionPalette.Transparent;

            /// <summary>"140 BPM · 3/4" for a scene's tempo and beats per bar (0.5); "" for none.</summary>
            internal static string Settings(SessionDocument.Scene info)
            {
                if (info == null) return "";
                string tempo = info.Tempo > 0 ? $"{info.Tempo} BPM" : "";
                string beats = info.BeatsPerBar > 0 ? $"{info.BeatsPerBar}/4" : "";
                string text = tempo.Length > 0 && beats.Length > 0 ? tempo + " · " + beats : tempo + beats;
                if (info.Follow?.On == true) text = text.Length > 0 ? text + " · →" : "→";     // 0.6: a follow action
                return text;
            }
        }

        /// <summary>Track header: colour strip, machine name, mute and solo.</summary>
        internal sealed class TrackHeader : Border
        {
            readonly SessionView view;
            readonly Chip mute;
            readonly Chip solo;
            readonly Chip arm;         // 0.7: record arm (tracks only)

            public int Track { get; }
            public IMachine Machine { get; }
            /// <summary>Index of the return this header is for, or -1 for a track.</summary>
            public int Return { get; init; } = -1;

            public TrackHeader(SessionView view, int track, IMachine machine, string name, int colorIndex)
            {
                this.view = view;
                Track = track;
                Machine = machine;
                Background = SessionPalette.Header;
                Margin = new Thickness(0, 0, Gap, Gap);
                ToolTip = name + (track < 0 ? "\nright-click for more" : "\nright-click: colour, rename, arm, stop, remove the track");

                var strip = new Rectangle { Height = 3, Fill = SessionPalette.ColorBrush(colorIndex) ?? SessionPalette.Transparent, IsHitTestVisible = false };
                mute = new Chip("M", SessionPalette.Mute, "Mute", () => { if (Machine != null) Machine.IsMuted = !Machine.IsMuted; });
                solo = new Chip("S", SessionPalette.Solo, "Solo", () => { if (Machine != null) Machine.IsSoloed = !Machine.IsSoloed; });
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
                if (track >= 0)
                {
                    arm = new Chip("●", SessionPalette.Record,
                        "Arm for recording: MIDI you play goes to this track's machine and is recorded\n" +
                        "(● in its empty slots records a take; ◉ Overdub records into what it plays)",
                        () => view.ToggleArm(Track));
                    chips.Children.Add(arm);
                }
                chips.Children.Add(mute);
                chips.Children.Add(solo);
                var title = new TextBlock
                {
                    Text = name,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                var row = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(chips, Dock.Right);
                row.Children.Add(chips);
                row.Children.Add(title);
                var panel = new DockPanel { LastChildFill = true };
                DockPanel.SetDock(strip, Dock.Top);
                panel.Children.Add(strip);
                panel.Children.Add(row);
                Child = panel;

                MouseRightButtonUp += (s, e) =>
                {
                    if (Return >= 0) view.ShowReturnMenu(this);
                    else view.ShowTrackMenu(this);
                    e.Handled = true;
                };
            }

            public void Refresh()
            {
                mute.SetActive(Machine?.IsMuted == true);
                solo.SetActive(Machine?.IsSoloed == true);
                arm?.SetActive(Track >= 0 && Return < 0 && view.layout.IsArmed(Track));
            }
        }

        /// <summary>Small toggle (mute, solo).</summary>
        internal sealed class Chip : Border
        {
            readonly Brush activeBrush;
            readonly TextBlock text;
            bool active;

            public Chip(string label, Brush activeBrush, string tooltip, Action onClick)
            {
                this.activeBrush = activeBrush;
                Width = 18;
                Height = 16;
                Margin = new Thickness(2, 0, 0, 0);
                Background = SessionPalette.Scene;
                Cursor = Cursors.Hand;
                ToolTip = tooltip;
                text = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.DimText };
                Child = text;
                MouseLeftButtonDown += (s, e) => { onClick(); e.Handled = true; };
            }

            public void SetActive(bool value)
            {
                if (value == active && Background != null) return;
                active = value;
                Background = active ? activeBrush : SessionPalette.Scene;
                text.Foreground = active ? SessionPalette.DarkText : SessionPalette.DimText;
            }
        }

        /// <summary>
        /// Flat dark button that fits the grid (WPF's default button looks out of place here).
        /// <see cref="SetLit"/> gives it a colour, for buttons that show a state (Rec, Back to arrangement).
        /// </summary>
        internal sealed class FlatButton : Border
        {
            readonly Action onClick;
            readonly TextBlock text;
            Brush baseBrush = SessionPalette.Scene;
            bool hover;

            public HorizontalAlignment HorizontalContentAlignment
            {
                get => text.HorizontalAlignment;
                set => text.HorizontalAlignment = value;
            }

            public string Text
            {
                get => text.Text;
                set => text.Text = value;
            }

            public FlatButton(string label, Action onClick, string tooltip)
            {
                this.onClick = onClick;
                Background = baseBrush;
                BorderThickness = new Thickness(1);
                BorderBrush = SessionPalette.Transparent;
                Margin = new Thickness(0, 0, 6, 0);
                Padding = new Thickness(10, 3, 10, 3);
                Cursor = Cursors.Hand;
                ToolTip = tooltip;
                text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Child = text;
                MouseEnter += (s, e) => { hover = true; UpdateBackground(); };
                MouseLeave += (s, e) => { hover = false; UpdateBackground(); };
                MouseLeftButtonDown += (s, e) => { if (IsEnabled) this.onClick(); e.Handled = true; };
                IsEnabledChanged += (s, e) => { Opacity = IsEnabled ? 1.0 : 0.4; UpdateBackground(); };
            }

            /// <summary>Colours the button (null: back to normal).</summary>
            public void SetLit(Brush brush)
            {
                var b = brush ?? SessionPalette.Scene;
                if (ReferenceEquals(b, baseBrush)) return;
                baseBrush = b;
                text.Foreground = brush == null ? SessionPalette.Text : SessionPalette.TextOn(brush);
                UpdateBackground();
            }

            void UpdateBackground() =>
                Background = hover && IsEnabled ? SessionPalette.Lighten(baseBrush, 0.12) : baseBrush;
        }

        /// <summary>Menu command. A MenuItem with a Command takes IsEnabled from CanExecute.</summary>
        sealed class RelayCommand : ICommand
        {
            readonly Action action;
            readonly Func<bool> canExecute;
            public RelayCommand(Action action, Func<bool> canExecute = null) { this.action = action; this.canExecute = canExecute; }
            public event EventHandler CanExecuteChanged { add { } remove { } }
            public bool CanExecute(object parameter) => canExecute?.Invoke() ?? true;
            public void Execute(object parameter) => action();
        }
    }
}
