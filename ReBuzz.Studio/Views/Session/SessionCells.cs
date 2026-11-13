// Fork (docs/specs/studio-mvp.md S5): the Session View's cells, ported from the WPF view's
// SessionCells.cs: clip slots, scene buttons, track headers, chips (mute, solo, arm) and flat buttons.

using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Session
{
    using Control = Avalonia.Controls.Control;

    internal sealed partial class SessionView
    {
        static readonly Cursor Hand = new Cursor(StandardCursorType.Hand);

        static bool IsLeft(PointerPressedEventArgs e, Visual relativeTo) => e.GetCurrentPoint(relativeTo).Properties.IsLeftButtonPressed;

        /// <summary>
        /// One slot of the grid. A clip shows its pattern name on its colour; its ▶ area launches
        /// on press (and releases on release, for Gate and Repeat clips), the rest selects
        /// (double-click edits, drag moves within the track). Clips with launch settings show
        /// them in short on the right (0.5). An empty slot has a small stop button for its
        /// track (a record button on an armed track); double-click creates a clip.
        /// </summary>
        internal sealed class SlotCell : Border
        {
            const double DragThreshold = 4;

            readonly SessionView view;
            readonly Border progress;
            readonly Border chip;
            readonly TextBlock glyph;
            readonly TextBlock label;
            readonly TextBlock flags;
            readonly Control stopButton;      // empty slot: stops the track
            readonly Control recordButton;    // empty slot of an armed track: records a take (0.7)
            Point? pressPoint;
            bool dragging;

            public int Track { get; }
            public int Scene { get; }
            public ISequence Sequence { get; }
            public IPattern Pattern { get; }

            /// <summary>The ▶ area (tests press it).</summary>
            internal Control LaunchArea { get; }

            /// <summary>The empty slot's ● (tests press it).</summary>
            internal Control RecordButton => recordButton;

            public SlotCell(SessionView view, int track, int scene, ISequence sequence, IPattern pattern, string midiText,
                            SessionDocument.ClipSettings settings = null)
            {
                this.view = view;
                Track = track;
                Scene = scene;
                Sequence = sequence;
                Pattern = pattern;

                Margin = new Thickness(0, 0, Gap, Gap);
                BorderThickness = new Thickness(2);      // fork (ux-overhaul.md §3.1): a 2 pt outline for the state and the selection
                Background = SessionPalette.Empty;
                ToolTip.SetTip(this, Tip(view, pattern, midiText, settings));

                progress = new Border
                {
                    Background = SessionPalette.Progress,
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
                    // A dark chip under the glyph while the slot has a state (ux-overhaul.md §3.1): the state colour reads on any clip colour.
                    chip = new Border { Margin = new Thickness(2), CornerRadius = new CornerRadius(3), Background = SessionPalette.Transparent, Child = glyph, IsHitTestVisible = false };
                    var launchArea = new Border { Background = SessionPalette.Transparent, Cursor = Hand, Child = chip };
                    // Press launches; the release (button up, or the capture lost) is the clip's release.
                    bool held = false;
                    launchArea.PointerPressed += (s, e) =>
                    {
                        if (!IsLeft(e, launchArea)) return;
                        view.LaunchSlot(this);
                        e.Pointer.Capture(launchArea);
                        held = true;
                        e.Handled = true;
                    };
                    void Release()
                    {
                        if (!held) return;
                        held = false;
                        view.ReleaseSlot(this);
                    }
                    launchArea.PointerReleased += (s, e) =>
                    {
                        if (e.InitialPressMouseButton != MouseButton.Left) return;
                        Release();
                        e.Handled = true;
                    };
                    launchArea.PointerCaptureLost += (s, e) => Release();
                    Grid.SetColumn(launchArea, 0);
                    grid.Children.Add(launchArea);
                    LaunchArea = launchArea;

                    string marks = Flags(settings);
                    if (marks.Length > 0)
                    {
                        flags = new TextBlock
                        {
                            Text = marks,
                            FontSize = 11,
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
                        Cursor = Hand,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = new Border { Width = 5, Height = 5, Background = SessionPalette.DimText, IsHitTestVisible = false }
                    };
                    ToolTip.SetTip(stop, "Stop this track");
                    stop.PointerPressed += (s, e) =>
                    {
                        if (!IsLeft(e, stop)) return;
                        view.Select(Track, Scene);
                        view.StopTrack(Track);
                        e.Handled = true;
                    };
                    Grid.SetColumnSpan(stop, 3);
                    grid.Children.Add(stop);
                    stopButton = stop;

                    var record = new Border
                    {
                        Width = 13,
                        Height = 13,
                        Background = SessionPalette.Header,
                        Cursor = Hand,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsVisible = false,
                        Child = new Ellipse { Width = 7, Height = 7, Fill = SessionPalette.Record, IsHitTestVisible = false }
                    };
                    ToolTip.SetTip(record, "Record a take here (the track is armed): a new clip, recording what you play\n" +
                                           "from the next launch point (⚙: record length, count-in, metronome)");
                    record.PointerPressed += (s, e) =>
                    {
                        if (!IsLeft(e, record)) return;
                        view.RecordInto(this);
                        e.Handled = true;
                    };
                    Grid.SetColumnSpan(record, 3);
                    grid.Children.Add(record);
                    recordButton = record;
                }

                Grid.SetColumnSpan(progress, 3);
                grid.Children.Add(progress);
                Child = grid;

                PointerPressed += OnPressed;
                PointerMoved += OnMoved;
                PointerReleased += OnReleased;
                PointerCaptureLost += (s, e) => EndDrag(null, false);
            }

            static string Tip(SessionView view, IPattern pattern, string midiText, SessionDocument.ClipSettings settings)
            {
                // Fork (ux-overhaul.md §2): its length in bars and beats (it was ticks).
                string tip = pattern != null
                    ? $"{pattern.Name} ({Shell.TimeText.Length(pattern.Length, view.buzz.TPB, view.session.BeatsPerBar)})\n▶ launches · double-click edits · drag moves ({ModName} copies) · right-click for more"
                    : "Empty slot\n■ stops the track · double-click creates a clip · right-click for more";
                if (pattern != null && settings != null && !settings.IsDefault)
                    tip += "\n" + Summary(view, settings);
                return midiText == null ? tip : tip + "\n" + midiText;
            }

            /// <summary>Short marks for non-default clip settings: G T R (mode), L (legato), 1× (no loop), v (velocity), ◆ (markers), q (quantization), → (follow action).</summary>
            internal static string Flags(SessionDocument.ClipSettings c)
            {
                if (c == null || c.IsDefault) return "";
                var parts = new List<string>();
                if (c.Mode != nameof(ClipLaunchMode.Trigger) && c.Mode.Length > 0) parts.Add(c.Mode.Substring(0, 1));
                if (c.Legato) parts.Add("L");
                if (!c.Loop) parts.Add("1×");
                if (c.Start != 0 || c.LoopStart != 0 || c.LoopEnd != 0) parts.Add("◆");
                if (c.Velocity != 0) parts.Add("v");
                if (c.Quantization != SessionDocument.ClipSettings.GlobalQuantization) parts.Add("q");
                if (c.Follow?.On == true) parts.Add("→");
                return string.Join(" ", parts);
            }

            static string Summary(SessionView view, SessionDocument.ClipSettings c)
            {
                string Place(int t) => Shell.TimeText.Position(t, view.buzz.TPB, view.session.BeatsPerBar);
                var parts = new List<string> { c.Mode };
                if (c.Quantization != SessionDocument.ClipSettings.GlobalQuantization)
                {
                    int i = Array.FindIndex(ClipPanel.Quantizations, q => q.Value == c.Quantization);
                    parts.Add("quantize " + (i >= 0 ? ClipPanel.Quantizations[i].Label : c.Quantization));
                }
                if (c.Legato) parts.Add("legato");
                parts.Add(c.Loop ? "loop" : "one shot");
                if (c.Start != 0 || c.LoopStart != 0 || c.LoopEnd != 0)
                    parts.Add($"start {Place(c.Start)}, loop {Place(c.LoopStart)}–{(c.LoopEnd == 0 ? "end" : Place(c.LoopEnd))}");
                if (c.Velocity != 0) parts.Add($"velocity {c.Velocity}%");
                string text = "Clip: " + string.Join(" · ", parts);
                if (c.Audio is { } a)
                    text += $"\nAudio: wave {a.Wave + 1:X2} · {a.Bpm:0.##} BPM · {a.Warp}";
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

            void OnPressed(object sender, PointerPressedEventArgs e)
            {
                if (!IsLeft(e, this)) return;
                view.Select(Track, Scene);
                if (e.ClickCount == 2)
                {
                    view.SlotDoubleClicked(this, Normalize(e.KeyModifiers));
                }
                else if (Pattern != null)
                {
                    pressPoint = e.GetPosition(view);
                    e.Pointer.Capture(this);
                }
                e.Handled = true;
            }

            void OnMoved(object sender, PointerEventArgs e)
            {
                if (pressPoint is not Point start || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                var p = e.GetPosition(view);
                if (!dragging && (Math.Abs(p.X - start.X) > DragThreshold || Math.Abs(p.Y - start.Y) > DragThreshold))
                    dragging = true;
                if (dragging)
                    view.DragOver(this, p);
            }

            void OnReleased(object sender, PointerReleasedEventArgs e)
            {
                if (e.InitialPressMouseButton == MouseButton.Right)
                {
                    view.ShowSlotMenu(this);
                    e.Handled = true;
                    return;
                }
                if (pressPoint == null) return;
                EndDrag(e.GetPosition(view), Normalize(e.KeyModifiers).HasFlag(KeyModifiers.Control));
                e.Handled = true;
            }

            void EndDrag(Point? drop, bool copy)
            {
                bool wasDragging = dragging;
                pressPoint = null;
                dragging = false;
                if (wasDragging)
                    view.EndDrag(this, drop, copy);
            }

            public void Refresh(ISession session, bool blinkOn, IBrush border)
            {
                bool hover = IsPointerOver;
                if (Pattern == null || Sequence == null)
                {
                    Background = hover ? SessionPalette.EmptyHover : SessionPalette.Empty;
                    BorderBrush = border ?? SessionPalette.Transparent;
                    if (stopButton != null && recordButton != null)
                    {
                        bool armed = Sequence != null && view.layout.IsArmedTrack(Sequence);
                        stopButton.IsVisible = !armed;
                        recordButton.IsVisible = armed;
                    }
                    return;
                }

                var color = SessionPalette.ColorBrush(PatternColors.Get(Pattern));
                var state = view.layout.SlotState(Sequence, Pattern, Track, Scene);   // only the slot it plays from
                // Fork (ux-overhaul.md §3.1): a clip keeps its own fill in every state (an uncoloured one turned green while it
                // played, the same green a user can pick); its state is the chip under ▶ or ●, a 2 pt outline and the progress
                // bar, in the reserved state colours. Waiting for the launch point (queued, stopping, a take about to record) blinks.
                // P3h (decision 86): a clip with no colour of its own wears its machine's identity colour (not written into the song).
                var background = color ?? SessionPalette.DefaultFill(Sequence.Machine);
                if (hover) background = SessionPalette.Lighten(background, 0.1);
                var text = SessionPalette.TextOn(background);

                Background = background;
                label.Foreground = text;
                if (flags != null) flags.Foreground = text;
                IBrush stateBrush = null;
                bool waiting = false;
                switch (state)
                {
                    case ClipState.Playing:
                        stateBrush = SessionPalette.Playing;
                        break;
                    case ClipState.Queued:
                    case ClipState.Stopping:
                        stateBrush = SessionPalette.Playing;
                        waiting = true;
                        break;
                }
                // 0.7: a take waiting or recording, or the clip an armed track overdubs: red.
                bool recording = view.layout.IsRecordingInto(Sequence, Pattern);
                if (recording)
                {
                    stateBrush = SessionPalette.Record;
                    waiting = state != ClipState.Playing;
                    if (glyph.Text != "●") glyph.Text = "●";
                }
                else if (glyph.Text != "▶")
                {
                    glyph.Text = "▶";
                }
                bool lit = stateBrush != null && (!waiting || blinkOn);
                chip.Background = stateBrush != null ? SessionPalette.StateChip : SessionPalette.Transparent;
                glyph.Foreground = lit ? stateBrush : stateBrush != null ? SessionPalette.LabelText : text;
                bool sounding = state == ClipState.Playing || state == ClipState.Stopping;
                border ??= stateBrush != null && (sounding || lit) ? stateBrush : null;
                BorderBrush = border ?? SessionPalette.Transparent;
                progress.Background = recording ? SessionPalette.Record : SessionPalette.Progress;
                ShownState = stateBrush == null ? "" : (recording ? "recording" : state.ToString().ToLowerInvariant()) + (waiting ? (lit ? " (on)" : " (off)") : "");

                if (state == ClipState.Playing || state == ClipState.Stopping)
                {
                    int pos = session.GetClipPosition(Sequence);
                    int len = Math.Max(1, Pattern.Length);
                    progress.Width = pos < 0 ? 0 : Math.Max(0, Bounds.Width - 4) * (pos + 1) / len;
                }
                else
                {
                    progress.Width = 0;
                }
            }

            /// <summary>What the slot shows now, for tests: its glyph and outline.</summary>
            internal string Glyph => glyph?.Text;
            internal IBrush Outline => BorderBrush;
            internal IBrush GlyphColour => glyph?.Foreground;
            internal IBrush ChipColour => chip?.Background;

            /// <summary>The state drawn now (tests): "", "playing", "queued (on)", "stopping (off)", "recording"…</summary>
            internal string ShownState { get; private set; } = "";
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
                Cursor = Hand;
                string name = info?.Name;
                string settings = Settings(info);
                ToolTip.SetTip(this, $"Scene {scene + 1}: click launches the row" + (scene < 9 ? $" (key {scene + 1})" : "") +
                                     (settings.Length > 0 ? $"\nSets {settings} on its launch beat" : "") +
                                     "\nright-click for more (name, colour, tempo, beats per bar)" + (midiText == null ? "" : "\n" + midiText));

                var strip = new Border { Width = 4, Background = SessionPalette.ColorBrush(info?.ColorIndex ?? -1) ?? SessionPalette.Transparent, IsHitTestVisible = false };
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
                        FontSize = 11,
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

                PointerEntered += (s, e) => Background = SessionPalette.SceneHover;
                PointerExited += (s, e) => Background = SessionPalette.Scene;
                PointerPressed += (s, e) =>
                {
                    if (!IsLeft(e, this)) return;
                    view.LaunchScene(Scene);
                    e.Handled = true;
                };
                PointerReleased += (s, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Right) return;
                    view.ShowSceneMenu(this);
                    e.Handled = true;
                };
            }

            public void Refresh(IBrush border) => BorderBrush = border ?? SessionPalette.Transparent;

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

        /// <summary>Track header: colour strip, machine name, arm, mute and solo.</summary>
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

            internal Chip Arm => arm;
            internal Chip Mute => mute;
            internal Chip Solo => solo;

            public TrackHeader(SessionView view, int track, IMachine machine, string name, int colorIndex)
            {
                this.view = view;
                Track = track;
                Machine = machine;
                Background = SessionPalette.Header;
                Margin = new Thickness(0, 0, Gap, Gap);
                ToolTip.SetTip(this, name + (track < 0 ? "\nright-click for more" : "\nright-click: colour, rename, arm, stop, remove the track"));

                var strip = new Border { Height = 3, Background = SessionPalette.ColorBrush(colorIndex) ?? SessionPalette.IdentityBrush(machine) ?? SessionPalette.Transparent, IsHitTestVisible = false };
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
                    FontWeight = FontWeight.SemiBold,
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

                PointerReleased += (s, e) =>
                {
                    if (e.InitialPressMouseButton != MouseButton.Right) return;
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

        /// <summary>Small toggle (arm, mute, solo, cue, crossfader side).</summary>
        internal sealed class Chip : Border
        {
            readonly IBrush activeBrush;
            readonly TextBlock text;
            bool active;
            bool shown;

            public Chip(string label, IBrush activeBrush, string tooltip, Action onClick)
            {
                this.activeBrush = activeBrush;
                // Fork (ux-overhaul.md §3.2): 20×20 pt, an 11 pt label at 4.6:1 (was 18×16 pt, 10 pt at 3.5:1, gallery mac-040).
                Width = StudioTokens.MinControl;
                Height = StudioTokens.MinControl;
                Margin = new Thickness(2, 0, 0, 0);
                Background = SessionPalette.Scene;
                Cursor = Hand;
                ToolTip.SetTip(this, tooltip);
                text = new TextBlock { Text = label, FontSize = StudioTokens.MinFontSize, FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.LabelText };
                Child = text;
                PointerPressed += (s, e) =>
                {
                    if (!IsLeft(e, this)) return;
                    onClick();
                    e.Handled = true;
                };
            }

            public bool IsActive => active;

            public void SetActive(bool value)
            {
                if (value == active && shown) return;
                shown = true;
                active = value;
                Background = active ? activeBrush : SessionPalette.Scene;
                text.Foreground = active ? SessionPalette.TextOn(activeBrush) : SessionPalette.LabelText;
            }
        }

        /// <summary>
        /// Flat dark button that fits the grid (the theme's buttons look out of place here).
        /// <see cref="SetLit"/> gives it a colour, for buttons that show a state (Rec, Back to arrangement).
        /// </summary>
        internal sealed class FlatButton : Border
        {
            readonly Action onClick;
            readonly TextBlock text;
            IBrush baseBrush = SessionPalette.Scene;
            IBrush lit;

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

            /// <summary>The colour it is lit with, or null.</summary>
            public IBrush Lit => lit;

            public FlatButton(string label, Action onClick, string tooltip)
            {
                this.onClick = onClick;
                Background = baseBrush;
                BorderThickness = new Thickness(1);
                BorderBrush = SessionPalette.Transparent;
                Margin = new Thickness(0, 0, 6, 0);
                Padding = new Thickness(10, 3, 10, 3);
                Cursor = Hand;
                if (!string.IsNullOrEmpty(tooltip)) ToolTip.SetTip(this, tooltip);
                text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Foreground = SessionPalette.Text };
                Child = text;
                PointerEntered += (s, e) => UpdateBackground();
                PointerExited += (s, e) => UpdateBackground();
                PointerPressed += (s, e) =>
                {
                    if (!IsLeft(e, this)) return;
                    Click();
                    e.Handled = true;
                };
            }

            /// <summary>What a press does (tests call it as a press would).</summary>
            public void Click()
            {
                if (IsEnabled) onClick();
            }

            protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
            {
                base.OnPropertyChanged(change);
                if (change.Property == IsEnabledProperty)
                {
                    Opacity = IsEnabled ? 1.0 : 0.4;
                    UpdateBackground();
                }
            }

            /// <summary>Colours the button (null: back to normal).</summary>
            public void SetLit(IBrush brush)
            {
                var b = brush ?? SessionPalette.Scene;
                if (ReferenceEquals(b, baseBrush)) return;
                lit = brush;
                baseBrush = b;
                text.Foreground = brush == null ? SessionPalette.Text : SessionPalette.TextOn(brush);
                UpdateBackground();
            }

            void UpdateBackground() =>
                Background = IsPointerOver && IsEnabled ? SessionPalette.Lighten(baseBrush, 0.12) : baseBrush;
        }
    }
}
