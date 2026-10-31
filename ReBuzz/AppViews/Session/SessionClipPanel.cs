using BuzzGUI.Interfaces;
using ReBuzz.Core.Session;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ReBuzz.AppViews.Session
{
    internal sealed partial class SessionView
    {
        /// <summary>
        /// Clip panel (docs/specs/session-view.md 0.5, Live's clip view): the launch settings of
        /// the selected slot's clip, and (0.6) its follow actions and its scene row's settings
        /// (tempo, beats per bar, scene follow actions). Edits are undoable (the layout's
        /// history); markers apply at once to the clip if it plays, the rest on its next launch.
        /// </summary>
        internal sealed class ClipPanel : Border
        {
            internal const double PanelWidth = 232;

            static readonly (string Value, string Label)[] Modes =
            {
                (nameof(ClipLaunchMode.Trigger), "Trigger"),
                (nameof(ClipLaunchMode.Gate), "Gate"),
                (nameof(ClipLaunchMode.Toggle), "Toggle"),
                (nameof(ClipLaunchMode.Repeat), "Repeat"),
            };

            internal static readonly (string Value, string Label)[] Quantizations =
            {
                (SessionDocument.ClipSettings.GlobalQuantization, "Global"),
                (nameof(LaunchQuantization.None), "None"),
                (nameof(LaunchQuantization.Sixteenth), "1/16"),
                (nameof(LaunchQuantization.Eighth), "1/8"),
                (nameof(LaunchQuantization.Beat), "1 beat"),
                (nameof(LaunchQuantization.HalfNote), "2 beats"),
                (nameof(LaunchQuantization.Bar), "1 bar"),
                (nameof(LaunchQuantization.TwoBars), "2 bars"),
                (nameof(LaunchQuantization.FourBars), "4 bars"),
                (nameof(LaunchQuantization.EightBars), "8 bars"),
            };

            readonly SessionView view;
            readonly TextBlock title;
            readonly TextBlock info;
            readonly TextBlock status;
            readonly TextBlock hint;
            readonly StackPanel body;
            readonly ComboBox mode;
            readonly ComboBox quantize;
            readonly CheckBox legato;
            readonly CheckBox loop;
            readonly TextBox start;
            readonly TextBox loopStart;
            readonly TextBox loopEnd;
            readonly TextBlock loopStartLabel;
            readonly TextBlock loopEndLabel;
            readonly TextBlock region;
            readonly MixerBar velocity;
            readonly FollowEditor clipFollow;
            readonly StackPanel sceneBody;
            readonly TextBlock sceneTitle;
            readonly TextBox sceneTempo;
            readonly TextBox sceneBeats;
            readonly FollowEditor sceneFollow;
            bool loading;

            (int Track, int Scene)? shownSlot;
            ISequence shownSequence;
            IPattern shownPattern;
            int shownLength;
            int shownNotes;              // 0.7: notes recorded into the clip's engine MIDI column

            public ClipPanel(SessionView view)
            {
                this.view = view;
                Width = PanelWidth;
                Background = SessionPalette.Bar;
                BorderBrush = SessionPalette.Header;
                BorderThickness = new Thickness(1, 0, 0, 0);
                Padding = new Thickness(10, 8, 10, 8);

                title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Text = "Clip" };
                info = new TextBlock { Foreground = SessionPalette.DimText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
                status = new TextBlock { Foreground = SessionPalette.DimText, Margin = new Thickness(0, 2, 0, 6), FontFamily = new FontFamily("Consolas") };
                hint = new TextBlock { Foreground = SessionPalette.DimText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

                mode = Combo(Modes, () => Commit(c => c.Mode = Modes[mode.SelectedIndex].Value),
                    "What pressing and releasing the clip's launch button (mouse, Enter, MIDI note) does:\n" +
                    "Trigger: press launches, release does nothing.\n" +
                    "Gate: press launches, release stops it (on the clip's launch quantization).\n" +
                    "Toggle: press launches, the next press stops it.\n" +
                    "Repeat: while held, the clip restarts at its launch quantization; release lets it play on.\n" +
                    "Scenes launch clips as Trigger.");
                quantize = Combo(Quantizations, () => Commit(c => c.Quantization = Quantizations[quantize.SelectedIndex].Value),
                    "Launch quantization of this clip; Global uses the session's (top bar).\n" +
                    "Also the rate of Repeat. Scenes use the session's quantization.");
                legato = Check("Legato",
                    "The clip starts where the clip it replaces on the track has got to,\n" +
                    "instead of from its start (Live's legato: switch variations without losing the phrase).",
                    () => Commit(c => c.Legato = legato.IsChecked == true));
                loop = Check("Loop",
                    "On: after the end the clip repeats its loop region.\nOff: it plays once, from the start marker to the end, and the track stops.",
                    () => Commit(c => c.Loop = loop.IsChecked == true));

                velocity = new MixerBar(false, false, SessionPalette.Fader)
                {
                    Height = 18,
                    Caption = "Velocity",
                    DefaultValue = 0,
                    Margin = new Thickness(0, 6, 0, 2),
                    ToolTip = "How much the launch velocity (a MIDI note's) sets the clip's volume.\n" +
                              "0%: none. 100%: full; the softest note is silent. Mouse, keys and scenes launch at full velocity.\n" +
                              "Drag (Shift for fine), wheel, double-click resets."
                };
                velocity.Changed += v =>
                {
                    velocity.ValueText = $"{(int)Math.Round(v * 100)}%";
                    if (!velocity.IsDragging) CommitVelocity();
                };
                velocity.LostMouseCapture += (s, e) => CommitVelocity();

                start = NumberBox("Start marker in ticks: where the clip starts playing.");
                loopStart = NumberBox("Loop start in ticks: where the clip goes back to after the loop end.");
                loopEnd = NumberBox("Loop end in ticks (the tick after the last one played); 0 or empty for the clip's end.\nWithout loop: where the clip ends.");
                loopStartLabel = FieldLabel("Loop start");
                loopEndLabel = FieldLabel("Loop end");
                region = new TextBlock { Foreground = SessionPalette.DimText, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0), FontSize = 11 };

                clipFollow = new FollowEditor(true, change => Commit(c => change(c.Follow)));

                var reset = new FlatButton("Reset", () => Commit(c =>
                {
                    var d = new SessionDocument.ClipSettings();
                    c.Mode = d.Mode; c.Quantization = d.Quantization; c.Legato = d.Legato; c.Loop = d.Loop;
                    c.Start = d.Start; c.LoopStart = d.LoopStart; c.LoopEnd = d.LoopEnd; c.Velocity = d.Velocity;
                    c.Follow = d.Follow;
                }), "Back to the defaults: Trigger, global quantization, loop over the whole clip, no legato, velocity or follow actions")
                {
                    Margin = new Thickness(0, 10, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Left
                };

                body = new StackPanel
                {
                    Children =
                    {
                        Section("Launch"),
                        Row("Mode", mode),
                        Row("Quantize", quantize),
                        legato,
                        velocity,
                        Section("Region"),
                        loop,
                        Row(FieldLabel("Start"), start),
                        Row(loopStartLabel, loopStart),
                        Row(loopEndLabel, loopEnd),
                        region,
                        Section("Follow action"),
                        clipFollow,
                        reset
                    }
                };

                // The selected slot's scene row (0.6): tempo, beats per bar, scene follow actions.
                sceneTitle = Section("Scene");
                sceneTempo = NumberBox("Tempo (BPM, 16–500) set when the scene launches, on its launch beat; empty for none.", CommitScene);
                sceneBeats = NumberBox("Beats per bar (1–64) set when the scene launches; bars count from there. Empty for none.", CommitScene);
                sceneFollow = new FollowEditor(false, change => CommitSceneFollow(change));
                sceneBody = new StackPanel
                {
                    Margin = new Thickness(0, 6, 0, 0),
                    Children = { sceneTitle, Row("Tempo", sceneTempo), Row("Beats/bar", sceneBeats), sceneFollow }
                };

                Child = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                    Content = new StackPanel { Children = { title, info, status, hint, body, sceneBody } }
                };
                Load();
            }

            /// <summary>The clip shown, or null.</summary>
            public IPattern Pattern => shownPattern;

            // For tests: the controls, as a user would use them.
            internal ComboBox ModeBox => mode;
            internal ComboBox QuantizeBox => quantize;
            internal CheckBox LegatoBox => legato;
            internal CheckBox LoopBox => loop;
            internal TextBox StartBox => start;
            internal TextBox LoopStartBox => loopStart;
            internal TextBox LoopEndBox => loopEnd;
            internal MixerBar VelocityBar => velocity;
            internal string RegionText => region.Text;
            internal string StatusText => status.Text;
            internal void CommitTypedMarkers() => CommitMarkers();
            internal FollowEditor ClipFollow => clipFollow;
            internal FollowEditor SceneFollow => sceneFollow;
            internal TextBox SceneTempoBox => sceneTempo;
            internal void CommitSceneFields() => CommitScene();

            /// <summary>Shows the clip of a slot (reloads when it changed, or when <paramref name="force"/>).</summary>
            public void Follow((int Track, int Scene)? slot, bool force = false)
            {
                var layout = view.layout;
                var seq = slot is { } s ? layout.SequenceOf(s.Track) : null;
                var pattern = slot is { } p && seq != null ? layout.PatternAt(p.Track, p.Scene) : null;
                if (force || slot != shownSlot || !ReferenceEquals(seq, shownSequence) || !ReferenceEquals(pattern, shownPattern) ||
                    (pattern != null && (pattern.Length != shownLength || layout.RecordedNoteCount(pattern) != shownNotes)))
                {
                    shownSlot = slot;
                    shownSequence = seq;
                    shownPattern = pattern;
                    Load();
                }
                UpdateStatus();
            }

            void Load()
            {
                loading = true;
                try
                {
                    LoadScene();
                    if (shownPattern == null)
                    {
                        title.Text = "Clip";
                        info.Text = "";
                        status.Text = "";
                        hint.Text = shownSlot == null
                            ? "Select a clip to see how it launches: mode, quantization, legato, loop region, velocity."
                            : "Empty slot. Double-click it to create a clip.";
                        hint.Visibility = Visibility.Visible;
                        body.Visibility = Visibility.Collapsed;
                        return;
                    }

                    var c = view.layout.ClipOf(shownPattern);
                    shownLength = Math.Max(1, shownPattern.Length);
                    shownNotes = view.layout.RecordedNoteCount(shownPattern);
                    int moves = view.layout.RecordedAutomationCount(shownPattern);
                    title.Text = shownPattern.Name;
                    info.Text = $"{shownPattern.Machine?.Name} · {Describe(shownLength)}" +
                                (shownNotes > 0 ? $" · {shownNotes} recorded note{(shownNotes == 1 ? "" : "s")}" : "") +
                                (moves > 0 ? $" · {moves} automation point{(moves == 1 ? "" : "s")}" : "");
                    info.ToolTip = shownNotes > 0 || moves > 0
                        ? "Recorded notes and automation are in the clip's own columns, which the engine plays to the machine.\n" +
                          "Edit them in the piano roll (Ctrl+double-click the slot), or move them to the Pattern View's note lanes and back (right-click the slot)."
                        : null;
                    hint.Visibility = Visibility.Collapsed;
                    body.Visibility = Visibility.Visible;

                    mode.SelectedIndex = Math.Max(0, Array.FindIndex(Modes, m => m.Value == c.Mode));
                    quantize.SelectedIndex = Math.Max(0, Array.FindIndex(Quantizations, q => q.Value == c.Quantization));
                    legato.IsChecked = c.Legato;
                    loop.IsChecked = c.Loop;
                    velocity.Value = c.Velocity / 100.0;
                    velocity.ValueText = $"{c.Velocity}%";
                    start.Text = c.Start.ToString();
                    loopStart.Text = c.LoopStart.ToString();
                    loopEnd.Text = c.LoopEnd == 0 ? "" : c.LoopEnd.ToString();
                    loopStartLabel.Opacity = c.Loop ? 1 : 0.4;
                    loopStart.IsEnabled = c.Loop;
                    loopEndLabel.Text = c.Loop ? "Loop end" : "End";

                    var (s, ls, end) = new ClipShape(c.Start, c.LoopStart, c.LoopEnd, c.Loop).Resolve(shownLength);
                    region.Text = c.Loop
                        ? $"Plays from {s}, loops {ls}–{end} of {shownLength} ticks."
                        : $"Plays {s}–{end} of {shownLength} ticks once.";
                    clipFollow.Load(c.Follow);
                }
                finally
                {
                    loading = false;
                }
            }

            /// <summary>The scene row of the selected slot (0.6).</summary>
            void LoadScene()
            {
                var doc = view.layout.Document;
                if (shownSlot is not { } at || doc == null || at.Scene < 0 || at.Scene >= doc.Scenes.Count)
                {
                    sceneBody.Visibility = Visibility.Collapsed;
                    return;
                }
                var info = doc.Scenes[at.Scene];
                sceneBody.Visibility = Visibility.Visible;
                sceneTitle.Text = (string.IsNullOrEmpty(info.Name) ? $"Scene {at.Scene + 1}" : $"Scene {at.Scene + 1}: {info.Name}").ToUpperInvariant();
                sceneTempo.Text = info.Tempo > 0 ? info.Tempo.ToString() : "";
                sceneBeats.Text = info.BeatsPerBar > 0 ? info.BeatsPerBar.ToString() : "";
                sceneFollow.Load(info.Follow);
            }

            void CommitScene()
            {
                if (loading || shownSlot is not { } at) return;
                var doc = view.layout.Document;
                if (doc == null || at.Scene >= doc.Scenes.Count) return;
                var info = doc.Scenes[at.Scene];
                if (!TryTicks(sceneTempo.Text, out int tempo) || (tempo != 0 && (tempo < 16 || tempo > 500)) ||
                    !TryTicks(sceneBeats.Text, out int beats) || beats > 64)
                {
                    Load();
                    return;
                }
                if (tempo != info.Tempo) view.layout.SetSceneTempo(at.Scene, tempo);
                if (beats != info.BeatsPerBar) view.layout.SetSceneBeatsPerBar(at.Scene, beats);
            }

            void CommitSceneFollow(Action<SessionDocument.FollowSettings> change)
            {
                if (loading || shownSlot is not { } at) return;
                var doc = view.layout.Document;
                if (doc == null || at.Scene >= doc.Scenes.Count) return;
                var f = doc.Scenes[at.Scene].Follow.Clone();
                change(f);
                view.layout.SetSceneFollow(at.Scene, f);
            }

            string Describe(int ticks)
            {
                int tpb = Math.Max(1, view.buzz.TPB);
                int bar = tpb * Math.Max(1, view.session.BeatsPerBar);
                string length = ticks % bar == 0 ? (ticks / bar == 1 ? "1 bar" : $"{ticks / bar} bars")
                              : ticks % tpb == 0 ? (ticks / tpb == 1 ? "1 beat" : $"{ticks / tpb} beats")
                              : $"{ticks} ticks";
                return ticks % bar == 0 || ticks % tpb == 0 ? $"{length} ({ticks} ticks)" : length;
            }

            /// <summary>Playing state and position (called on every refresh).</summary>
            public void UpdateStatus()
            {
                if (shownPattern == null || shownSequence == null)
                {
                    status.Text = "";
                    return;
                }
                var session = view.session;
                var state = session.GetState(shownSequence, shownPattern);
                string text = state switch
                {
                    ClipState.Playing => "▶ playing",
                    ClipState.Queued => "… queued",
                    ClipState.Stopping => "■ stopping",
                    _ => "stopped"
                };
                if (state == ClipState.Playing || state == ClipState.Stopping)
                {
                    int pos = session.GetClipPosition(shownSequence);
                    if (pos >= 0) text += $"  {pos + 1}/{shownLength}";
                    if (session is SessionCore core)
                    {
                        if (core.IsRepeating(shownSequence)) text += "  repeat";
                        long followAt = core.GetFollowAt(shownSequence);
                        if (followAt >= 0 && session.Clock >= 0) text += $"  follow in {followAt - session.Clock}";
                    }
                }
                if (status.Text != text) status.Text = text;
            }

            void Commit(Action<SessionDocument.ClipSettings> change)
            {
                if (loading || shownPattern == null) return;
                var c = view.layout.ClipOf(shownPattern);
                change(c);
                view.layout.SetClipSettings(shownPattern, c);
                // The layout's Changed event reloads the panel (and shows the result).
            }

            void CommitVelocity() => Commit(c => c.Velocity = (int)Math.Round(velocity.Value * 100));

            void CommitMarkers()
            {
                if (loading || shownPattern == null) return;
                if (!TryTicks(start.Text, out int s) || !TryTicks(loopStart.Text, out int ls) || !TryTicks(loopEnd.Text, out int le))
                {
                    Load();       // not a number: back to what it was
                    return;
                }
                Commit(c =>
                {
                    c.Start = Math.Min(s, shownLength - 1);
                    c.LoopStart = Math.Min(ls, shownLength - 1);
                    c.LoopEnd = le >= shownLength ? 0 : le;
                });
            }

            static bool TryTicks(string text, out int ticks)
            {
                text = text?.Trim() ?? "";
                if (text.Length == 0) { ticks = 0; return true; }
                return int.TryParse(text, out ticks) && ticks >= 0;
            }

            // ------------------------------------------------------------------ controls

            ComboBox Combo((string Value, string Label)[] items, Action changed, string tip)
            {
                var box = new ComboBox { Width = 110, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
                foreach (var item in items) box.Items.Add(item.Label);
                box.SelectionChanged += (s, e) => { if (!loading && box.SelectedIndex >= 0) changed(); };
                return box;
            }

            static CheckBox Check(string label, string tip, Action changed)
            {
                var box = new CheckBox { Content = label, Foreground = SessionPalette.Text, ToolTip = tip, Margin = new Thickness(0, 4, 0, 2) };
                box.Click += (s, e) => changed();
                return box;
            }

            TextBox NumberBox(string tip, Action commit = null)
            {
                commit ??= CommitMarkers;
                var box = new TextBox
                {
                    Width = 64,
                    Background = SessionPalette.Empty,
                    Foreground = SessionPalette.Text,
                    BorderBrush = SessionPalette.Header,
                    CaretBrush = SessionPalette.Text,
                    Padding = new Thickness(2, 1, 2, 1),
                    ToolTip = tip
                };
                box.KeyDown += (s, e) =>
                {
                    if (e.Key == Key.Enter) { commit(); e.Handled = true; }
                    else if (e.Key == Key.Escape) { Load(); e.Handled = true; }
                };
                box.LostKeyboardFocus += (s, e) => commit();
                return box;
            }

            static TextBlock Section(string text) => new TextBlock
            {
                Text = text.ToUpperInvariant(),
                Foreground = SessionPalette.DimText,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 10, 0, 4)
            };

            static TextBlock FieldLabel(string text) => new TextBlock
            {
                Text = text,
                Foreground = SessionPalette.DimText,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 80
            };

            static FrameworkElement Row(string label, FrameworkElement control) => Row(FieldLabel(label), control);

            static FrameworkElement Row(TextBlock label, FrameworkElement control)
            {
                var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };
                DockPanel.SetDock(label, Dock.Left);
                row.Children.Add(label);
                row.Children.Add(control);
                return row;
            }

            /// <summary>
            /// Follow action controls (0.6, Live's): on/off, actions A and B with their chance, the
            /// follow time (or, for clips, linked to the clip's end times a multiplier), and jump
            /// targets. Each change is committed through the given callback.
            /// </summary>
            internal sealed class FollowEditor : StackPanel
            {
                internal static readonly (string Value, string Label)[] Actions =
                {
                    ("None", "No action"), ("Stop", "Stop"), ("PlayAgain", "Play again"), ("Previous", "Previous"), ("Next", "Next"),
                    ("First", "First"), ("Last", "Last"), ("Any", "Any"), ("Other", "Other"), ("Jump", "Jump"),
                };

                readonly bool clip;
                readonly Action<Action<SessionDocument.FollowSettings>> commit;
                readonly CheckBox on;
                readonly ComboBox a;
                readonly ComboBox b;
                readonly MixerBar chance;
                readonly CheckBox linked;
                readonly TextBox multiplier;
                readonly TextBox time;
                readonly TextBox jumpA;
                readonly TextBox jumpB;
                readonly FrameworkElement multiplierRow, timeRow, jumpARow, jumpBRow, details;
                bool loading;

                public FollowEditor(bool clip, Action<Action<SessionDocument.FollowSettings>> commit)
                {
                    this.clip = clip;
                    this.commit = commit;
                    on = Check(clip ? "Follow action" : "Scene follow action",
                        clip ? "At the follow time, action A or B (by their chance) launches another clip of the group\n" +
                               "(the run of filled slots around this one), stops the track, or plays the clip again.\n" +
                               "Follow actions don't wait for the global quantization, only for a clip's own."
                             : "At the follow time after the scene launches, action A or B (by their chance) launches another scene\n" +
                               "(of the run of non-empty scenes around this one) or stops all clips. Scene follow actions win over clips'.",
                        () => Commit(f => f.On = on.IsChecked == true));
                    a = ActionBox(i => Commit(f => f.A = Actions[i].Value));
                    b = ActionBox(i => Commit(f => f.B = Actions[i].Value));
                    chance = new MixerBar(false, false, SessionPalette.Send)
                    {
                        Height = 18,
                        Caption = "Chance",
                        DefaultValue = 1,
                        Margin = new Thickness(0, 4, 0, 2),
                        ToolTip = "Chance of A; B gets the rest. Drag, wheel, double-click for 100% A."
                    };
                    chance.Changed += v =>
                    {
                        chance.ValueText = ChanceText((int)Math.Round(v * 100));
                        if (!chance.IsDragging) CommitChance();
                    };
                    chance.LostMouseCapture += (s, e) => CommitChance();
                    linked = Check("Linked to the clip's end", "On: at the end of the clip, after it has played its loop × times.\nOff: after the follow time.",
                        () => Commit(f => f.Linked = linked.IsChecked == true));
                    multiplier = Box("Passes of the clip (its loop region) before the follow action", CommitFields);
                    time = Box("Follow time in bars.beats.ticks from where it starts playing (a tick is a sixteenth at 4 ticks per beat)", CommitFields);
                    jumpA = Box("Scene row A jumps to (1 is the first)", CommitFields);
                    jumpB = Box("Scene row B jumps to (1 is the first)", CommitFields);
                    multiplierRow = Row("Loops ×", multiplier);
                    timeRow = Row("Time", time);
                    jumpARow = Row("A jumps to", jumpA);
                    jumpBRow = Row("B jumps to", jumpB);
                    var linkedRow = clip ? (FrameworkElement)linked : new Border();
                    details = new StackPanel { Children = { Row("A", a), jumpARow, Row("B", b), jumpBRow, chance, linkedRow, multiplierRow, timeRow } };
                    Children.Add(on);
                    Children.Add(details);
                }

                internal CheckBox OnBox => on;
                internal ComboBox ABox => a;
                internal ComboBox BBox => b;
                internal MixerBar ChanceBar => chance;
                internal TextBox TimeBox => time;
                internal CheckBox LinkedBox => linked;
                internal void CommitTypedFields() => CommitFields();

                public void Load(SessionDocument.FollowSettings f)
                {
                    f ??= new SessionDocument.FollowSettings();
                    loading = true;
                    try
                    {
                        on.IsChecked = f.On;
                        details.Visibility = f.On ? Visibility.Visible : Visibility.Collapsed;
                        a.SelectedIndex = Math.Max(0, Array.FindIndex(Actions, x => x.Value == f.A));
                        b.SelectedIndex = Math.Max(0, Array.FindIndex(Actions, x => x.Value == f.B));
                        chance.Value = f.ChanceA / 100.0;
                        chance.ValueText = ChanceText(f.ChanceA);
                        linked.IsChecked = f.Linked;
                        multiplier.Text = f.Multiplier.ToString();
                        time.Text = f.Time;
                        jumpA.Text = f.JumpA >= 0 ? (f.JumpA + 1).ToString() : "";
                        jumpB.Text = f.JumpB >= 0 ? (f.JumpB + 1).ToString() : "";
                        bool isLinked = clip && f.Linked;
                        multiplierRow.Visibility = isLinked ? Visibility.Visible : Visibility.Collapsed;
                        timeRow.Visibility = isLinked ? Visibility.Collapsed : Visibility.Visible;
                        jumpARow.Visibility = f.A == "Jump" ? Visibility.Visible : Visibility.Collapsed;
                        jumpBRow.Visibility = f.B == "Jump" ? Visibility.Visible : Visibility.Collapsed;
                    }
                    finally
                    {
                        loading = false;
                    }
                }

                static string ChanceText(int percentA) => $"A {percentA}% · B {100 - percentA}%";

                void Commit(Action<SessionDocument.FollowSettings> change)
                {
                    if (!loading) commit(change);
                }

                void CommitChance() => Commit(f => f.ChanceA = (int)Math.Round(chance.Value * 100));

                void CommitFields()
                {
                    if (loading) return;
                    string t = time.Text.Trim();
                    bool timeOk = SessionDocument.FollowSettings.TicksOf(t, 4, 4) != null;
                    bool multOk = int.TryParse(multiplier.Text.Trim(), out int m) && m >= 1 && m <= 999;
                    int ja = ParseJump(jumpA.Text), jb = ParseJump(jumpB.Text);
                    Commit(f =>
                    {
                        if (timeOk) f.Time = t;
                        if (multOk) f.Multiplier = m;
                        if (ja >= -1) f.JumpA = ja;
                        if (jb >= -1) f.JumpB = jb;
                    });
                }

                /// <summary>A 1-based scene number as a row; -1 for empty; -2 if it isn't one.</summary>
                static int ParseJump(string text)
                {
                    text = text?.Trim() ?? "";
                    if (text.Length == 0) return -1;
                    return int.TryParse(text, out int n) && n >= 1 ? n - 1 : -2;
                }

                ComboBox ActionBox(Action<int> changed)
                {
                    var box = new ComboBox { Width = 110, VerticalAlignment = VerticalAlignment.Center };
                    foreach (var x in Actions) box.Items.Add(x.Label);
                    box.SelectionChanged += (s, e) => { if (!loading && box.SelectedIndex >= 0) changed(box.SelectedIndex); };
                    return box;
                }

                TextBox Box(string tip, Action commitText)
                {
                    var box = new TextBox
                    {
                        Width = 64,
                        Background = SessionPalette.Empty,
                        Foreground = SessionPalette.Text,
                        BorderBrush = SessionPalette.Header,
                        CaretBrush = SessionPalette.Text,
                        Padding = new Thickness(2, 1, 2, 1),
                        ToolTip = tip
                    };
                    box.KeyDown += (s, e) => { if (e.Key == Key.Enter) { commitText(); e.Handled = true; } };
                    box.LostKeyboardFocus += (s, e) => commitText();
                    return box;
                }
            }
        }
    }
}
