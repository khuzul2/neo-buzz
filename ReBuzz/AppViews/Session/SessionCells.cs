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
        /// on mouse down, the rest selects (double-click edits, drag moves within the track). An
        /// empty slot has a small stop button for its track; double-click creates a clip.
        /// </summary>
        internal sealed class SlotCell : Border
        {
            const double DragThreshold = 4;

            readonly SessionView view;
            readonly Rectangle progress;
            readonly TextBlock glyph;
            readonly TextBlock label;
            bool hover;
            Point? pressPoint;
            bool dragging;

            public int Track { get; }
            public int Scene { get; }
            public ISequence Sequence { get; }
            public IPattern Pattern { get; }

            public SlotCell(SessionView view, int track, int scene, ISequence sequence, IPattern pattern, string midiText)
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
                ToolTip = Tip(pattern, midiText);

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

                if (pattern != null)
                {
                    glyph = new TextBlock { Text = "▶", FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
                    var launchArea = new Border { Background = SessionPalette.Transparent, Cursor = Cursors.Hand, Child = glyph };
                    launchArea.MouseLeftButtonDown += (s, e) => { view.LaunchSlot(this); e.Handled = true; };
                    Grid.SetColumn(launchArea, 0);
                    grid.Children.Add(launchArea);

                    label = new TextBlock
                    {
                        Text = pattern.Name,
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
                    Grid.SetColumnSpan(stop, 2);
                    grid.Children.Add(stop);
                }

                Grid.SetColumnSpan(progress, 2);
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

            static string Tip(IPattern pattern, string midiText)
            {
                string tip = pattern != null
                    ? $"{pattern.Name} ({pattern.Length} ticks)\n▶ launches · double-click edits · drag moves (Ctrl copies) · right-click for more"
                    : "Empty slot\n■ stops the track · double-click creates a clip · right-click for more";
                return midiText == null ? tip : tip + "\n" + midiText;
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
                    return;
                }

                var color = SessionPalette.ColorBrush(PatternColors.Get(Pattern)) ?? SessionPalette.Clip;
                var state = session.GetState(Sequence, Pattern);
                var background = state == ClipState.Playing || state == ClipState.Stopping ? SessionPalette.Lighten(color, 0.2) : color;
                if (hover) background = SessionPalette.Lighten(background, 0.1);
                var text = SessionPalette.TextOn(background);

                Background = background;
                label.Foreground = text;
                switch (state)
                {
                    case ClipState.Playing:
                        glyph.Foreground = SessionPalette.Progress;
                        border ??= SessionPalette.Playing;
                        break;
                    case ClipState.Queued:
                        glyph.Foreground = blinkOn ? SessionPalette.Progress : text;
                        border ??= blinkOn ? SessionPalette.Progress : null;
                        break;
                    case ClipState.Stopping:
                        glyph.Foreground = blinkOn ? SessionPalette.Stopping : SessionPalette.Progress;
                        border ??= SessionPalette.Playing;
                        break;
                    default:
                        glyph.Foreground = text;
                        break;
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
                ToolTip = $"Scene {scene + 1}: click launches the row" + (scene < 9 ? $" (key {scene + 1})" : "") +
                          "\nright-click for more" + (midiText == null ? "" : "\n" + midiText);

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
                panel.Children.Add(text);
                Child = panel;

                MouseEnter += (s, e) => Background = SessionPalette.SceneHover;
                MouseLeave += (s, e) => Background = SessionPalette.Scene;
                MouseLeftButtonDown += (s, e) => { view.LaunchScene(Scene); e.Handled = true; };
                MouseRightButtonUp += (s, e) => { view.ShowSceneMenu(this); e.Handled = true; };
            }

            public void Refresh(Brush border) => BorderBrush = border ?? SessionPalette.Transparent;
        }

        /// <summary>Track header: colour strip, machine name, mute and solo.</summary>
        internal sealed class TrackHeader : Border
        {
            readonly Chip mute;
            readonly Chip solo;

            public int Track { get; }
            public IMachine Machine { get; }

            public TrackHeader(SessionView view, int track, IMachine machine, string name, int colorIndex)
            {
                Track = track;
                Machine = machine;
                Background = SessionPalette.Header;
                Margin = new Thickness(0, 0, Gap, Gap);
                ToolTip = name + "\nright-click: track colour";

                var strip = new Rectangle { Height = 3, Fill = SessionPalette.ColorBrush(colorIndex) ?? SessionPalette.Transparent, IsHitTestVisible = false };
                mute = new Chip("M", SessionPalette.Mute, "Mute", () => { if (Machine != null) Machine.IsMuted = !Machine.IsMuted; });
                solo = new Chip("S", SessionPalette.Solo, "Solo", () => { if (Machine != null) Machine.IsSoloed = !Machine.IsSoloed; });
                var chips = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
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

                MouseRightButtonUp += (s, e) => { view.ShowTrackMenu(this); e.Handled = true; };
            }

            public void Refresh()
            {
                mute.SetActive(Machine?.IsMuted == true);
                solo.SetActive(Machine?.IsSoloed == true);
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

        /// <summary>Flat dark button that fits the grid (WPF's default button looks out of place here).</summary>
        internal sealed class FlatButton : Border
        {
            readonly Action onClick;
            readonly TextBlock text;

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
                Background = SessionPalette.Scene;
                BorderThickness = new Thickness(1);
                BorderBrush = SessionPalette.Transparent;
                Margin = new Thickness(0, 0, 6, 0);
                Padding = new Thickness(10, 3, 10, 3);
                Cursor = Cursors.Hand;
                ToolTip = tooltip;
                text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
                Child = text;
                MouseEnter += (s, e) => { if (IsEnabled) Background = SessionPalette.SceneHover; };
                MouseLeave += (s, e) => Background = SessionPalette.Scene;
                MouseLeftButtonDown += (s, e) => { if (IsEnabled) this.onClick(); e.Handled = true; };
                IsEnabledChanged += (s, e) => Opacity = IsEnabled ? 1.0 : 0.4;
            }
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
