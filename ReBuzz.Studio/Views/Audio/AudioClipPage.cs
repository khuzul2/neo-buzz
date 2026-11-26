// Fork (docs/specs/neo-audio.md, NA3): the Audio page of the detail panel, the clip panel of an audio clip selected in the Session View or in the Arrangement
// (Live's Clip view, compact): the waveform with the clip's region and warp markers, the warp mode and its tempo, transpose, detune, gain, fades, reverse and
// loop. Each control is one edit and one undo step, made through the model calls the agent's audio_clip_set makes (SessionLayout.EditAudio). In the
// Session the loop and the region are the slot's (the clip's launch settings); in the Arrangement they are the arranged clip's (ClipEditor).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Commands;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Session;

namespace ReBuzz.Studio.Views.Audio
{
    using Control = Avalonia.Controls.Control;

    internal sealed class AudioClipPage : Border, IStudioEditTarget
    {
        /// <summary>Where the selected clip is edited from: a slot of the Session, a clip of the Arrangement, or just its clip.</summary>
        internal enum Where { None, Session, Arrangement, Clip }

        /// <summary>
        /// The warp modes the page offers, in the order it shows them: Live's four. A mode of <see cref="SessionDocument.AudioSettings.WarpModes"/> that is
        /// not named here is offered after them under its own name, so a new mode is one line in WarpModes.
        /// </summary>
        internal static readonly (string Value, string Label)[] KnownWarps =
        {
            ("Beats", "Beats"), ("Complex", "Complex"), ("Repitch", "Re-Pitch"), ("Off", "Off"),
        };

        internal static IReadOnlyList<(string Value, string Label)> Warps =>
            KnownWarps.Where(k => SessionDocument.AudioSettings.WarpModes.Contains(k.Value))
                .Concat(SessionDocument.AudioSettings.WarpModes.Where(w => KnownWarps.All(k => k.Value != w)).Select(w => (w, w))).ToList();

        internal static readonly (string Value, string Label)[] Preserves =
        {
            ("Transients", "Transients"), ("1/4", "1/4"), ("1/8", "1/8"), ("1/16", "1/16"),
        };

        /// <summary>The window's hook: shows the page for a clip (a double click on an audio clip); false when there is no page to show.</summary>
        public static Func<IClip, bool> Opener { get; set; }

        readonly ReBuzzCore core;
        readonly StudioSelection selection;
        readonly ClipEditor editor;
        readonly WarpEditor wave;
        readonly TextBlock title, info, status, empty, markerInfo, hintText;
        readonly Grid body;
        readonly ComboBox warp, preserve;
        readonly Control preserveRow;
        readonly TextBox tempo, transpose, detune, gain, fadeIn, fadeOut, start, loopStart, loopEnd;
        readonly Button halve, doubleIt, detect, detectMore, clear, fit, convert, slice;
        // Fork (AW5): the split button's mode, the chip of the clip's options, the live hint of the toolbar and the legend of the markers.
        readonly TextBlock chip = new TextBlock { FontSize = 12, FontFamily = new FontFamily("monospace"), Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
        readonly TextBlock hintLabel = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 0, 1) };
        bool lastToGrid;
        // Fork (WM1): segment tempo, quantize and follow.
        readonly TextBox segBefore, segAfter, strength;
        readonly Button segHalve, segDouble, quantize;
        readonly CheckBox follow;
        readonly CheckBox loop, reverse;
        readonly TextBlock regionText;
        readonly Views.Detail.EnvelopeStack envelopes;      // EV1: the clip's envelope lanes under the waveform
        readonly WaveAxis axis;
        int envelopeTick;
        readonly DispatcherTimer playTimer;
        bool loading;
        bool disposed;

        IClip shown;
        Where where;
        ClipItem item;
        (int Track, int Scene)? slot;
        string signature;

        public AudioClipPage(ReBuzzCore core)
        {
            this.core = core;
            selection = StudioSelection.For(core);
            editor = new ClipEditor(core);
            Background = StudioTokens.SurfacePanelBrush;
            AutomationProperties.SetName(this, "Audio clip");

            title = new TextBlock { FontSize = 13, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            info = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            status = new TextBlock { FontSize = 11, Foreground = StudioTokens.ErrorTextBrush, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
            empty = new TextBlock
            {
                Text = "Select an audio clip in the Session or the Arrangement to edit its warp, tempo, pitch and gain here.",
                Foreground = StudioTokens.TextSecondaryBrush,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(12, 10, 12, 0),
            };

            warp = Combo(Warps.Select(w => w.Label), i => Apply(new AudioClipEdit { Warp = Warps[i].Value }, "Warp mode"),
                "How the clip follows the song's tempo.\nBeats: slices on transients (drums, loops) · Complex: a phase vocoder (any material) · Re-Pitch: a turntable, its pitch follows · Off: its own speed.", "Warp mode");
            preserve = Combo(Preserves.Select(p => p.Label), i => Apply(new AudioClipEdit { Preserve = Preserves[i].Value }, "Beats preserve"),
                "What Beats mode keeps: the transients, or a grid of 1/4, 1/8 or 1/16 of a beat.", "Preserve");
            tempo = Box("The audio's own tempo. Without warp markers the clip's length follows it: 4 beats at 120 BPM are 8 at 60.", CommitTempo, "Tempo");
            halve = Btn("÷2", "Half the tempo (the clip twice as long)", () => ScaleTempo(0.5), "Halve tempo");
            doubleIt = Btn("×2", "Double the tempo (the clip half as long)", () => ScaleTempo(2), "Double tempo");
            detect = Btn("Auto-warp: Follow the beat", "Find the beats in the audio and follow its tempo where it moves: the warp markers and the tempo are replaced (one undo step). A steady tempo gives one tempo and the first downbeat; a tempo that drifts gives a marker on every bar. To grid (the ▾ menu) also puts a marker on every attack and snaps it to the grid. With a range selected on the waveform it changes that range only.", () => RunAutoWarp(lastToGrid), "Auto-warp");
            detectMore = Btn("▾", "Auto-warp: Follow the beat, To grid or Options…", OpenAutoWarpMenu, "Auto-warp mode and options");
            detectMore.MinWidth = 24;
            AutoWarpFlow.Ran += OnAutoWarpRan;
            // Fork (docs/specs/pitch.md section 9): the clip's notes as MIDI on a new track; the page's right-click menu offers the same.
            var convertMenu = ConvertFlow.ButtonMenu(() => shown);
            convert = Btn("Convert to MIDI ▾", "Find the melody, the harmony or the drums in the audio and put them on a new MIDI track, in a clip where this one is", () => convertMenu.Open(convert), "Convert to MIDI");
            // Fork (docs/specs/audio-to-midi.md, SL1): the clip cut into up to 16 slices played from a Neo Sampler on a new MIDI track.
            slice = Btn("Slice to new MIDI track…", "Cut the audio into up to 16 slices (at its transients, its warp markers or a grid) and play them from a Neo Sampler on a new MIDI track, in a clip where this one is", () => _ = SliceFlow.SliceClip(shown), "Slice to new MIDI track");
            transpose = Box("Transpose, semitones (-48 to 48)", CommitPitch, "Transpose");
            detune = Box("Detune, cents (-50 to 50)", CommitPitch, "Detune");
            gain = Box("Gain, dB (-70 to 24)", CommitGain, "Gain");
            fadeIn = Box("Fade in, beats from the clip's start (Arrangement)", CommitFades, "Fade in");
            fadeOut = Box("Fade out, beats to the clip's end (Arrangement)", CommitFades, "Fade out");
            reverse = Check("Reverse", "Play the audio backwards (the part the clip plays stays the same)", () => Apply(new AudioClipEdit { Reverse = reverse.IsChecked == true }, "Reverse"));
            loop = Check("Loop", "On: after the end the clip repeats its loop region. Off: it plays once.", CommitLoop);
            start = Box("Where the clip starts playing, as bar.beat.tick (1.1.1 is its start)", CommitRegion, "Start");
            loopStart = Box("Where the loop goes back to, as bar.beat.tick", CommitRegion, "Loop start");
            loopEnd = Box("Where the loop (or the clip) ends, as bar.beat.tick", CommitRegion, "Loop end");
            regionText = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            preserveRow = Row("Keep", preserve);

            wave = new WarpEditor { Margin = new Thickness(0, 4, 0, 0) };
            wave.MarkersCommitted += m => Apply(new AudioClipEdit { Markers = m.ToList() }, "Warp markers");
            wave.RegionCommitted += (s, ls, le) => CommitRegionTicks(s, ls, le);
            wave.ActionRequested += (request, label) => ApplyWarp(request, label);
            wave.SelectionChanged += UpdateSegments;
            wave.AutoWarpMenu = () => AutoWarpFlow.Menu(() => shown == null ? Array.Empty<IClip>() : new[] { shown }, RangeNow);
            fit = Btn("Fit", "Zoom to show the whole clip", () => wave.Fit(), "Fit");
            follow = Check("Follow", "Scroll the waveform to keep the playing position in view", () => wave.Follow = follow.IsChecked == true);
            follow.Margin = new Thickness(4, 0, 4, 0);
            clear = Btn("Clear markers", "Back to straight, at the clip's tempo", () => Apply(new AudioClipEdit { Markers = new() }, "Clear warp markers"), "Clear markers");
            quantize = Btn("Quantize", "Move the selected markers (or the markers, or transients, in the selected range) toward the nearest grid line by the strength beside it. " + (KeyText.IsMac ? "⌘U" : "Ctrl+U"), () => wave.Quantize(), "Quantize");
            strength = Box("How far Quantize moves a marker toward the grid, 0 to 100 % (kept with the song)", CommitStrength, "Quantize strength");
            strength.Width = 44;
            segBefore = Box("The tempo of the segment before the selected marker, BPM. Type one: the marker moves and only that segment is stretched.", () => CommitSegment(false), "Segment tempo before");
            segAfter = Box("The tempo of the segment after the selected marker, BPM. Type one: the next marker moves and only that segment is stretched.", () => CommitSegment(true), "Segment tempo after");
            segBefore.Width = segAfter.Width = 62;
            segHalve = Btn("÷2", "Halve the tempo of the segment after the selected marker (before it, for the last marker)", () => ScaleSegment(0.5), "Halve segment tempo");
            segDouble = Btn("×2", "Double the tempo of the segment after the selected marker (before it, for the last marker)", () => ScaleSegment(2), "Double segment tempo");
            markerInfo = new TextBlock { MinWidth = 120, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            hintText = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, MaxLines = 2, Width = 320, Height = 30, VerticalAlignment = VerticalAlignment.Top, Text = wave.Hint };
            hintLabel.Text = wave.HintLabel;
            wave.HintChanged += t => { hintText.Text = t; hintLabel.Text = wave.HintLabel; };

            var clipColumn = new StackPanel
            {
                Width = 196,
                Margin = new Thickness(0, 0, 14, 0),
                Children =
                {
                    Section("Clip"),
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { loop, reverse } },
                    Row("Start", start), Row("Loop start", loopStart), Row("Loop end", loopEnd),
                    Row("Fade in", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fadeIn, Unit("beats") } }),
                    Row("Fade out", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { fadeOut, Unit("beats") } }),
                    regionText,
                },
            };
            var audioColumn = new StackPanel
            {
                Width = 226,
                Margin = new Thickness(0, 0, 14, 0),
                Children =
                {
                    Section("Audio"),
                    Row("Warp", warp), preserveRow,
                    Row("Tempo", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Children = { tempo, halve, doubleIt } }),
                    Row("Transpose", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { transpose, Unit("semitones") } }),
                    Row("Detune", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { detune, Unit("cents") } }),
                    Row("Gain", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { gain, Unit("dB") } }),
                    Row("MIDI", new StackPanel { Orientation = Orientation.Vertical, Spacing = 4, Children = { convert, slice } }),
                },
            };
            var left = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Focusable = false,
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { clipColumn, audioColumn } },
            };
            var tools = new WrapPanel { Orientation = Orientation.Horizontal };
            var chipBox = new Border { Child = chip, Padding = new Thickness(8, 4), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), BorderBrush = StudioTokens.SurfaceBorderBrush, Margin = new Thickness(0, 0, 8, 0) };
            ToolTip.SetTip(chipBox, "The clip's Auto-warp to grid options: its grid, the strength (beside Quantize) and the reach. Change them in Options…");
            var split = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 1, Children = { detect, detectMore } };
            foreach (var c in new Control[]
            {
                split, chipBox, fit, follow, clear, quantize, strength, Unit("%"), Label("  Segment", 62), segBefore, segAfter, segHalve, segDouble, markerInfo,
            })
            {
                c.Margin = new Thickness(c.Margin.Left, c.Margin.Top, c.Margin.Right + 4, 2);
                c.VerticalAlignment = VerticalAlignment.Center;
                tools.Children.Add(c);
            }
            var right = new DockPanel { LastChildFill = true };
            var hintBox = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Top, Width = 320, Margin = new Thickness(8, 0, 0, 0), Children = { hintLabel, hintText } };
            var toolbar = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(hintBox, Dock.Right);
            toolbar.Children.Add(hintBox);
            toolbar.Children.Add(tools);
            DockPanel.SetDock(toolbar, Dock.Top);
            var legend = Legend();
            DockPanel.SetDock(legend, Dock.Bottom);
            // Fork (daw-modes.md EV1): the clip's envelopes under the waveform, on its beats.
            axis = new WaveAxis(this);
            envelopes = new Views.Detail.EnvelopeStack(core, axis);
            DockPanel.SetDock(envelopes, Dock.Bottom);
            right.Children.Add(toolbar);
            right.Children.Add(legend);
            right.Children.Add(envelopes);
            right.Children.Add(wave);
            right.SizeChanged += (s, e) => envelopes.MaxLanesHeight = e.NewSize.Height * 0.45;

            body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(12, 0, 12, 8) };
            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 1);
            body.Children.Add(left);
            body.Children.Add(right);

            var header = new DockPanel { Margin = new Thickness(12, 6, 12, 2), LastChildFill = true };
            var headerText = new StackPanel { Orientation = Orientation.Horizontal, Children = { title, info, status } };
            header.Children.Add(headerText);
            DockPanel.SetDock(header, Dock.Top);
            var pageMenu = ConvertFlow.ButtonMenu(() => shown);
            SliceFlow.AddTo(pageMenu, () => shown);
            var frame = new DockPanel { LastChildFill = true, ContextMenu = pageMenu };
            frame.Children.Add(header);
            DockPanel.SetDock(empty, Dock.Top);
            frame.Children.Add(empty);
            frame.Children.Add(body);
            Child = frame;

            playTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) => UpdatePlayhead());
            selection.Changed += OnSelectionChanged;
            if (core.SessionLayout != null) core.SessionLayout.Changed += OnLayoutChanged;
            Refresh(force: true);
        }

        // ------------------------------------------------------------------ for the window and the tests

        /// <summary>The clip shown, or null.</summary>
        public IClip Clip => shown;

        internal Where Context => where;
        internal ClipItem Arranged => item;
        internal WarpEditor Wave => wave;
        internal Views.Detail.EnvelopeStack Envelopes => envelopes;

        /// <summary>Shows the clip with the envelope lane of a parameter (a double click on a curve of an audio clip).</summary>
        internal void ShowEnvelope(ColumnKey key)
        {
            if (shown != null) envelopes.Show(shown, key);
            envelopes.LaneFor(key)?.Focus();
        }

        /// <summary>Whether the keyboard is in an envelope lane (the Edit commands are the lane's then).</summary>
        internal bool EnvelopeFocused => envelopes.OwnsFocus;
        internal ComboBox WarpBox => warp;
        internal ComboBox PreserveBox => preserve;
        internal bool PreserveShown => preserveRow.IsVisible;
        internal TextBox TempoBox => tempo;
        internal Button HalveButton => halve;
        internal Button DoubleButton => doubleIt;
        internal Button DetectButton => detect;
        internal TextBox TransposeBox => transpose;
        internal TextBox DetuneBox => detune;
        internal TextBox GainBox => gain;
        internal TextBox FadeInBox => fadeIn;
        internal TextBox FadeOutBox => fadeOut;
        internal CheckBox ReverseBox => reverse;
        internal CheckBox LoopBox => loop;
        internal TextBox StartBox => start;
        internal TextBox LoopStartBox => loopStart;
        internal TextBox LoopEndBox => loopEnd;
        internal Button ClearButton => clear;
        internal Button FitButton => fit;
        internal Button ConvertButton => convert;
        internal Button SliceButton => slice;
        internal TextBox SegmentBeforeBox => segBefore;
        internal TextBox SegmentAfterBox => segAfter;
        internal Button SegmentHalveButton => segHalve;
        internal Button SegmentDoubleButton => segDouble;
        internal Button DetectMoreButton => detectMore;
        internal string ChipText => chip.Text;
        internal string HintLabelText => hintLabel.Text;
        internal Button QuantizeButton => quantize;
        internal TextBox StrengthBox => strength;
        internal CheckBox FollowBox => follow;
        internal string HintText => hintText.Text;
        internal string TitleText => title.Text;
        internal string InfoText => info.Text;
        internal string StatusText => status.Text;
        internal string RegionText => regionText.Text;
        internal string MarkerText => markerInfo.Text;
        internal bool EmptyShown => empty.IsVisible;

        // Fork (WM1): the Edit commands of the window (Select All, Delete, Deselect All) are the waveform's markers' while it has the keyboard; undo and redo are the song's.
        /// <summary>Whether the keyboard is in the waveform.</summary>
        internal bool WaveFocused(object focused) => ReferenceEquals(focused, wave);
        bool IStudioEditTarget.Undo() => false;
        bool IStudioEditTarget.Redo() => false;
        bool IStudioEditTarget.Delete() { if (envelopes.OwnsFocus) return envelopes.Delete(); if (wave.SelectedIndices.Count == 0) return false; wave.DeleteSelected(); return true; }
        bool IStudioEditTarget.SelectAll() { if (envelopes.OwnsFocus) return envelopes.SelectAll(); if (!wave.HasClip) return false; wave.SelectAll(); return true; }
        bool IStudioEditTarget.DeselectAll() { if (envelopes.OwnsFocus) return envelopes.DeselectAll(); wave.ClearSelection(); return true; }
        bool IStudioEditTarget.Cut() => envelopes.OwnsFocus && envelopes.Cut();
        bool IStudioEditTarget.Copy() => envelopes.OwnsFocus && envelopes.Copy();
        bool IStudioEditTarget.Paste() => envelopes.OwnsFocus && envelopes.Paste();

        /// <summary>Which view the window shows now ("Session", "Arrangement"…): a clip in both is edited as the view that is shown has it.</summary>
        public Func<string> ViewShown { get; set; }

        /// <summary>Types in a text box and commits it (tests: the box's own Enter).</summary>
        internal void Type(TextBox box, string text)
        {
            box.Text = text;
            Commit(box);
        }

        void Commit(TextBox box)
        {
            if (ReferenceEquals(box, tempo)) CommitTempo();
            else if (ReferenceEquals(box, transpose) || ReferenceEquals(box, detune)) CommitPitch();
            else if (ReferenceEquals(box, gain)) CommitGain();
            else if (ReferenceEquals(box, fadeIn) || ReferenceEquals(box, fadeOut)) CommitFades();
            else if (ReferenceEquals(box, strength)) CommitStrength();
            else if (ReferenceEquals(box, segAfter)) CommitSegment(true);
            else if (ReferenceEquals(box, segBefore)) CommitSegment(false);
            else CommitRegion();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            playTimer.Stop();
            AutoWarpFlow.Ran -= OnAutoWarpRan;
            selection.Changed -= OnSelectionChanged;
            if (core.SessionLayout != null) core.SessionLayout.Changed -= OnLayoutChanged;
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            if (!disposed) playTimer.Start();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            playTimer.Stop();
            base.OnDetachedFromVisualTree(e);
        }

        // ------------------------------------------------------------------ following the selection

        void OnSelectionChanged(object source) => Refresh();

        void OnLayoutChanged() => Refresh(force: true);

        SessionLayout Layout => core.SessionLayout;

        /// <summary>The selected clip, where it is edited from, and the controls' values (the window's timer calls it; a signature keeps it cheap).</summary>
        public void Refresh(bool force = false)
        {
            if (disposed || Layout == null) return;
            var clip = selection.Clip;
            if (clip != null && !Layout.IsAudioClip(clip)) clip = null;
            Where now = Where.None;
            ClipItem use = null;
            (int, int)? at = null;
            if (clip != null)
            {
                var uses = editor.ClipsOf(clip);
                at = SlotOf(clip);
                bool sessionShown = ViewShown?.Invoke() == "Session";
                if (!sessionShown && uses.Count > 0) { now = Where.Arrangement; use = uses[0]; }
                else if (at != null) now = Where.Session;
                else if (uses.Count > 0) { now = Where.Arrangement; use = uses[0]; }
                else now = Where.Clip;
            }
            var audio = clip == null ? null : Layout.AudioOf(clip);
            var settings = clip == null ? null : Layout.ClipOf(clip);
            string sig = clip == null ? "-" :
                $"{clip.Machine?.Name}|{clip.Name}|{clip.Length}|{now}|{audio?.ToXml()}|{settings?.Loop}|{settings?.Start}|{settings?.LoopStart}|{settings?.LoopEnd}|" +
                (use == null ? "" : $"{use.Time}|{use.Spec.Start}|{use.Spec.LoopStart}|{use.Spec.LoopEnd}|{use.Spec.Loop}|{use.Spec.Length}") + $"|{core.TPB}";
            if (!force && sig == signature) return;
            signature = sig;
            shown = clip;
            where = now;
            item = use;
            slot = at;
            Load();
        }

        /// <summary>The slot of the Session that holds the clip, or null.</summary>
        (int Track, int Scene)? SlotOf(IClip clip)
        {
            var doc = Layout.Document;
            if (doc == null || clip?.Machine == null) return null;
            if (selection.Scene is int scene && scene < doc.Scenes.Count)
                for (int t = 0; t < Layout.TrackCount; t++)
                    if (Layout.SequenceOf(t)?.Machine == clip.Machine && doc.GetSlot(t, scene) == clip.Name) return (t, scene);
            for (int t = 0; t < Layout.TrackCount; t++)
                if (Layout.SequenceOf(t)?.Machine == clip.Machine)
                    for (int s = 0; s < doc.Scenes.Count; s++)
                        if (doc.GetSlot(t, s) == clip.Name) return (t, s);
            return null;
        }

        int TicksPerBeat => Math.Max(1, core.TPB);
        int BeatsPerBar => Math.Max(1, core.Session?.BeatsPerBar ?? 4);

        string Place(int ticks) => TimeText.Position(ticks, TicksPerBeat, BeatsPerBar);

        void Load()
        {
            loading = true;
            try
            {
                status.Text = "";
                var audio = shown == null ? null : Layout.AudioOf(shown);
                bool has = shown != null && audio != null;
                body.IsVisible = has;
                empty.IsVisible = !has;
                if (!has)
                {
                    title.Text = "Audio";
                    info.Text = "";
                    wave.Load(null, null, TicksPerBeat, BeatsPerBar, 1, default);
                    envelopes.Show(null);
                    return;
                }
                var settings = Layout.ClipOf(shown);
                var view = Layout.ViewOf(shown);
                var details = Layout.AudioInfo(shown);
                title.Text = shown.Name;
                string place = where switch
                {
                    Where.Session when slot is { } s => $"Session · scene {s.Scene + 1}",
                    Where.Arrangement when item != null => $"Arrangement · {Place(item.Time)}",
                    _ => "clip",
                };
                info.Text = $"{shown.Machine?.Name} · {place}" + (details is { } d ? $" · {d.Seconds:0.##} s · {d.Rate} Hz · {(d.Channels > 1 ? "stereo" : "mono")}" : " · the audio is missing");

                warp.SelectedIndex = Math.Max(0, Warps.ToList().FindIndex(w => w.Value == audio.Warp));
                preserve.SelectedIndex = Math.Max(0, Array.FindIndex(Preserves, p => p.Value == audio.Preserve));
                preserveRow.IsVisible = audio.Warp == "Beats";
                tempo.Text = audio.Bpm.ToString("0.###", CultureInfo.InvariantCulture);
                transpose.Text = audio.Transpose.ToString(CultureInfo.InvariantCulture);
                detune.Text = audio.Detune.ToString(CultureInfo.InvariantCulture);
                gain.Text = audio.GainDb.ToString("0.0#", CultureInfo.InvariantCulture);
                fadeIn.Text = audio.FadeIn.ToString("0.###", CultureInfo.InvariantCulture);
                fadeOut.Text = audio.FadeOut.ToString("0.###", CultureInfo.InvariantCulture);
                bool arranged = where == Where.Arrangement;
                fadeIn.IsEnabled = fadeOut.IsEnabled = arranged;
                ToolTip.SetTip(fadeIn, arranged ? "Fade in, beats from the clip's start" : "Fades play on the Arrangement: they don't apply to a slot of the Session");
                ToolTip.SetTip(fadeOut, arranged ? "Fade out, beats to the clip's end" : "Fades play on the Arrangement: they don't apply to a slot of the Session");
                halve.IsEnabled = doubleIt.IsEnabled = detect.IsEnabled = true;
                reverse.IsChecked = audio.Reverse;

                var r = RegionOf(settings);
                loop.IsChecked = r.Loop;
                int length = Math.Max(1, shown.Length);
                int end = r.LoopEnd > 0 && r.LoopEnd < length ? r.LoopEnd : length;
                start.Text = Place(r.Start);
                loopStart.Text = Place(r.LoopStart);
                loopEnd.Text = Place(end);
                loopStart.IsEnabled = r.Loop;
                regionText.Text = r.Loop
                    ? $"Plays from {Place(r.Start)}, loops {Place(r.LoopStart)}–{Place(end)} of {TimeText.Length(length, TicksPerBeat, BeatsPerBar)}."
                    : $"Plays {Place(r.Start)}–{Place(end)} of {TimeText.Length(length, TicksPerBeat, BeatsPerBar)} once.";
                if (where == Where.Arrangement) regionText.Text += " On the Arrangement.";

                int markers = audio.Markers?.Count ?? 0;
                markerInfo.Text = markers >= 2 ? $"{markers} warp markers" : $"straight at {audio.Bpm:0.##} BPM";
                wave.GridName = audio.AutoGrid;
                UpdateAutoWarpLabels();
                clear.IsEnabled = markers >= 2;
                strength.Text = Layout.QuantizeStrength.ToString(CultureInfo.InvariantCulture);
                wave.Load(view, Layout.TransientsOf(shown), TicksPerBeat, BeatsPerBar, length, (r.Start, r.LoopStart, r.LoopEnd, r.Loop));
                wave.SetAttacks(markers >= 2 ? Layout.GridOnsetsOf(shown) : Array.Empty<double>(), audio.AutoGridBeats, audio.AutoReach / 100.0);
                UpdateSegments();
                envelopes.Show(shown);
            }
            finally
            {
                loading = false;
            }
        }

        /// <summary>The clip's loop and region where it is edited from: the slot's settings, or the arranged clip's.</summary>
        (int Start, int LoopStart, int LoopEnd, bool Loop) RegionOf(SessionDocument.ClipSettings settings) =>
            where == Where.Arrangement && item != null
                ? (item.Spec.Start, item.Spec.LoopStart, item.Spec.LoopEnd, item.Spec.Loop)
                : (settings.Start, settings.LoopStart, settings.LoopEnd, settings.Loop);

        // ------------------------------------------------------------------ the playing position

        void UpdatePlayhead()
        {
            if (shown == null || !IsEffectivelyVisible) { wave.ShowPlayhead(double.NaN); return; }
            double beat = double.NaN;
            try
            {
                var session = core.Session;
                if (where == Where.Arrangement && item != null)
                {
                    if (core.PlayRequested && item.Spec.ClipPositionAt(core.Song.PlayPosition) is int pos) beat = pos / (double)TicksPerBeat;
                }
                else if (session != null && slot is { } s && Layout.SequenceOf(s.Track) is { } seq)
                {
                    if (Layout.SlotState(seq, shown, s.Track, s.Scene) == ClipState.Playing && session.GetClipPosition(seq) is int p && p >= 0)
                        beat = p / (double)TicksPerBeat;
                }
            }
            catch (Exception)
            {
                beat = double.NaN;
            }
            wave.ShowPlayhead(beat);
            axis.Playhead = double.IsNaN(beat) ? -1 : (int)Math.Round(beat * TicksPerBeat * ClipEvent.TimeBase);
            axis.Poll();
            if (++envelopeTick % 3 == 0) envelopes.Refresh();
        }

        /// <summary>The waveform's beats as the time axis of the envelope lanes (EV1): its zoom, its scroll and its beat grid.</summary>
        sealed class WaveAxis : Views.Detail.IEnvelopeAxis
        {
            readonly AudioClipPage page;
            (double, double) seen;

            public WaveAxis(AudioClipPage page) => this.page = page;

            public event Action Changed;

            public int Playhead { get; set; } = -1;

            double Unit => page.TicksPerBeat * (double)ClipEvent.TimeBase;

            public double Gutter => 0;

            public double X(int time) => page.wave.X(time / Unit);

            public int TimeAt(double x) => (int)Math.Round(Math.Max(0, page.wave.BeatAt(x)) * Unit);

            public int GridUnits => Math.Max(1, (int)Math.Round(page.wave.SnapStep * Unit));

            public int BeatUnits => (int)Unit;

            public int BarUnits => (int)Unit * page.BeatsPerBar;

            public (int From, int To)? Loop => null;

            public void Zoom(double factor) => page.wave.Zoom(Math.Max(1, page.wave.Bounds.Width / 2), factor);

            public void ScrollBy(double pixels) => page.wave.Scroll(pixels);

            /// <summary>The waveform was zoomed or scrolled (it has no event for it): the lanes draw again.</summary>
            public void Poll()
            {
                var now = (page.wave.X(0), page.wave.X(1));
                if (now == seen) return;
                seen = now;
                Changed?.Invoke();
            }
        }

        // ------------------------------------------------------------------ Auto-warp (AW5)

        /// <summary>The range selected on the waveform, as seconds of the audio.</summary>
        AutoWarpFlow.Range? RangeNow() => wave.RangeSeconds is { } r ? new AutoWarpFlow.Range(r.From, r.To) : null;

        void RunAutoWarp(bool toGrid)
        {
            if (loading || shown == null) return;
            status.Text = "";
            AutoWarpFlow.Run(new[] { shown }, toGrid, RangeNow());
            if (AutoWarpFlow.LastWasError) status.Text = AutoWarpFlow.LastSummary;
            Refresh(force: true);
        }

        void OpenAutoWarpMenu()
        {
            if (shown == null) return;
            var root = AutoWarpFlow.Menu(() => new[] { shown }, RangeNow);
            var menu = new ContextMenu();
            foreach (var item in root.Items.OfType<object>().ToList()) { root.Items.Remove(item); menu.Items.Add(item); }
            autoWarpMenu = menu;
            menu.Open(detectMore);
        }

        ContextMenu autoWarpMenu;
        internal ContextMenu AutoWarpMenuOpen => autoWarpMenu;

        void OnAutoWarpRan(bool toGrid)
        {
            lastToGrid = toGrid;
            UpdateAutoWarpLabels();
        }

        void UpdateAutoWarpLabels()
        {
            bool range = wave.RangeSeconds != null;
            detect.Content = (range ? "Auto-warp selection: " : "Auto-warp: ") + (lastToGrid ? "To grid" : "Follow the beat");
            if (shown != null && Layout.AudioOf(shown) is { } a)
                chip.Text = $"{a.AutoGrid} · {Layout.QuantizeStrength} % · reach {a.AutoReach} %";
        }

        Control Legend()
        {
            Control Item(Control mark, string text) => new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = { mark, new TextBlock { Text = text, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center } } };
            var amber = new Avalonia.Controls.Shapes.Rectangle { Width = 8, Height = 8, Fill = StudioTokens.WarningBrush, RenderTransform = new RotateTransform(45), VerticalAlignment = VerticalAlignment.Center };
            var blue = new Avalonia.Controls.Shapes.Ellipse { Width = 10, Height = 10, Fill = StudioTokens.AccentBrush, VerticalAlignment = VerticalAlignment.Center };
            var hollow = new Avalonia.Controls.Shapes.Ellipse { Width = 9, Height = 9, Stroke = StudioTokens.TextSecondaryBrush, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
            var dash = new Avalonia.Controls.Shapes.Rectangle { Width = 18, Height = 2, Fill = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };
            return new WrapPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0), ItemSpacing = 16, LineSpacing = 2,
                Children =
                {
                    Item(amber, "Beat or bar (Follow the beat)"), Item(blue, "Attack snapped to the grid"), Item(hollow, "Attack left alone (beyond reach)"), Item(dash, "Long note: no markers"),
                },
            };
        }

        // ------------------------------------------------------------------ edits

        void Apply(AudioClipEdit edit, string label)
        {
            if (loading || shown == null) return;
            status.Text = "";
            string why = Layout.EditAudio(shown, edit, editor, label);
            if (why != null) status.Text = why;
            Refresh(force: true);                  // the controls show what the model now holds (the layout's Changed event does it too)
        }

        /// <summary>A warp marker edit of the waveform (its menu, keys or buttons): one step, through the call the agent's audio_warp_markers makes. What it made stays selected.</summary>
        void ApplyWarp(WarpRequest request, string label)
        {
            if (loading || shown == null) return;
            status.Text = "";
            string why = Layout.WarpEdit(shown, request, editor, out var result, label);
            if (why != null) status.Text = why;
            Refresh(force: true);
            if (why != null) status.Text = why;                  // the refresh clears the line: say it after
            bool keepSelection = request.Action is WarpRequest.SegmentTempo or WarpRequest.HalveSegment or WarpRequest.DoubleSegment;   // the marker whose segment it is stays the one selected
            if (why == null && !keepSelection && result != null && result.Touched.Count > 0 && request.Action != WarpRequest.Delete && request.Action != WarpRequest.RemoveAll)
                wave.SelectIndices(result.Touched);
            else if (why == null && (request.Action == WarpRequest.Delete || request.Action == WarpRequest.RemoveAll))
                wave.ClearSelection();
            UpdateSegments();
        }

        /// <summary>The tempo boxes of the segments either side of the selected marker.</summary>
        void UpdateSegments()
        {
            if (shown == null || Layout.AudioOf(shown) == null) return;
            int p = wave.Selected;
            var tempos = p >= 0 && wave.Markers.Count >= 2 ? Layout.SegmentTempos(shown) : new List<double>();
            string Show(int i) => i >= 0 && i < tempos.Count && !double.IsNaN(tempos[i]) ? tempos[i].ToString("0.##", CultureInfo.InvariantCulture) : "";
            bool reversed = Layout.AudioOf(shown).Reverse;
            // The boxes read left to right as the waveform does: a reversed clip shows its later segment on the left.
            segBefore.Text = Show(reversed ? p : p - 1);
            segAfter.Text = Show(reversed ? p - 1 : p);
            segBefore.IsEnabled = segBefore.Text.Length > 0;
            segAfter.IsEnabled = segAfter.Text.Length > 0;
            segHalve.IsEnabled = segDouble.IsEnabled = segBefore.Text.Length > 0 || segAfter.Text.Length > 0;
        }

        /// <summary>The marker a segment box is about: the segment from it to the next. "After" is the one starting at the selected marker (before it, in a reversed clip).</summary>
        int SegmentMarker(bool after)
        {
            int p = wave.Selected;
            bool reversed = Layout.AudioOf(shown)?.Reverse == true;
            return after != reversed ? p : p - 1;
        }

        void CommitSegment(bool after)
        {
            if (loading || shown == null) return;
            var box = after ? segAfter : segBefore;
            int m = SegmentMarker(after);
            if (m < 0 || !double.TryParse(box.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) || !double.IsFinite(bpm) || bpm < 1 || bpm > 999) { UpdateSegments(); return; }
            var now = Layout.SegmentTempos(shown);
            if (m < now.Count && Math.Abs(now[m] - bpm) < 5e-3) { UpdateSegments(); return; }
            ApplyWarp(new WarpRequest { Action = WarpRequest.SegmentTempo, Markers = new[] { m }, Bpm = bpm }, "Segment tempo");
        }

        void ScaleSegment(double factor)
        {
            if (loading || shown == null || wave.Selected < 0) return;
            int m = segAfter.IsEnabled ? SegmentMarker(true) : SegmentMarker(false);
            ApplyWarp(new WarpRequest { Action = factor < 1 ? WarpRequest.HalveSegment : WarpRequest.DoubleSegment, Markers = new[] { m } }, factor < 1 ? "Halve segment tempo" : "Double segment tempo");
        }

        void CommitStrength()
        {
            if (loading || shown == null) return;
            if (!int.TryParse(strength.Text?.Trim().TrimEnd('%'), NumberStyles.Integer, CultureInfo.InvariantCulture, out int pct)) { strength.Text = Layout.QuantizeStrength.ToString(CultureInfo.InvariantCulture); return; }
            pct = Math.Clamp(pct, 0, 100);
            if (pct != Layout.QuantizeStrength) Layout.SetQuantizeStrength(pct);
            strength.Text = pct.ToString(CultureInfo.InvariantCulture);
        }

        void ScaleTempo(double factor)
        {
            if (loading || shown == null || Layout.AudioOf(shown) is not { } a) return;
            Apply(new AudioClipEdit { TempoBpm = Math.Clamp(a.Bpm * factor, 1, 999) }, factor < 1 ? "Halve tempo" : "Double tempo");
        }

        void CommitTempo()
        {
            if (loading || shown == null || Layout.AudioOf(shown) is not { } a) return;
            if (!double.TryParse(tempo.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double bpm) || !double.IsFinite(bpm) || bpm < 1 || bpm > 999) { Refresh(force: true); return; }
            if (Math.Abs(bpm - a.Bpm) < 1e-9) return;
            Apply(new AudioClipEdit { TempoBpm = bpm }, "Tempo");
        }

        void CommitPitch()
        {
            if (loading || shown == null || Layout.AudioOf(shown) is not { } a) return;
            if (!int.TryParse(transpose.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int st) || !int.TryParse(detune.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ct)) { Refresh(force: true); return; }
            st = Math.Clamp(st, -48, 48);
            ct = Math.Clamp(ct, -50, 50);
            if (st == a.Transpose && ct == a.Detune) { Refresh(force: true); return; }
            Apply(new AudioClipEdit { Transpose = st != a.Transpose ? st : null, Detune = ct != a.Detune ? ct : null }, st != a.Transpose ? "Transpose" : "Detune");
        }

        void CommitGain()
        {
            if (loading || shown == null || Layout.AudioOf(shown) is not { } a) return;
            if (!double.TryParse(gain.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double db) || !double.IsFinite(db)) { Refresh(force: true); return; }
            db = Math.Clamp(db, -70, 24);
            if (Math.Abs(db - a.GainDb) < 1e-9) { Refresh(force: true); return; }
            Apply(new AudioClipEdit { GainDb = db }, "Gain");
        }

        void CommitFades()
        {
            if (loading || shown == null || Layout.AudioOf(shown) is not { } a) return;
            if (!double.TryParse(fadeIn.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fi) || !double.TryParse(fadeOut.Text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double fo)
                || !double.IsFinite(fi) || !double.IsFinite(fo)) { Refresh(force: true); return; }
            double max = Math.Max(0, shown.Length / (double)TicksPerBeat);
            fi = Math.Clamp(fi, 0, max);
            fo = Math.Clamp(fo, 0, max);
            bool inChanged = Math.Abs(fi - a.FadeIn) > 1e-9, outChanged = Math.Abs(fo - a.FadeOut) > 1e-9;
            if (!inChanged && !outChanged) { Refresh(force: true); return; }
            Apply(new AudioClipEdit { FadeIn = inChanged ? fi : null, FadeOut = outChanged ? fo : null }, inChanged ? "Fade in" : "Fade out");
        }

        void CommitLoop()
        {
            if (loading || shown == null) return;
            bool on = loop.IsChecked == true;
            if (where == Where.Arrangement && item != null)
            {
                string why = editor.SetLoop(item, on);
                if (why != null) status.Text = why;
                return;
            }
            Apply(new AudioClipEdit { Loop = on }, "Loop");
        }

        void CommitRegion()
        {
            if (loading || shown == null) return;
            if (!TryPlace(start.Text, out int s) || !TryPlace(loopStart.Text, out int ls) || !TryPlace(loopEnd.Text, out int le)) { Refresh(force: true); return; }
            int length = Math.Max(1, shown.Length);
            CommitRegionTicks(Math.Min(s, length - 1), Math.Min(ls, length - 1), le >= length ? 0 : le);
        }

        bool TryPlace(string text, out int ticks)
        {
            ticks = 0;
            return string.IsNullOrWhiteSpace(text) || TimeText.TryParsePosition(text, TicksPerBeat, BeatsPerBar, out ticks);
        }

        /// <summary>The region's start, loop start and loop end in ticks of the clip (null leaves one): the slot's settings, or the arranged clip's.</summary>
        void CommitRegionTicks(int? s, int? ls, int? le)
        {
            if (loading || shown == null) return;
            if (where == Where.Arrangement && item != null)
            {
                string why = editor.SetRegion(item, s, ls, le);
                if (why != null) status.Text = why;
                Refresh(force: true);
                return;
            }
            var settings = Layout.ClipOf(shown);
            if ((s ?? settings.Start) == settings.Start && (ls ?? settings.LoopStart) == settings.LoopStart && (le ?? settings.LoopEnd) == settings.LoopEnd)
            {
                Refresh(force: true);
                return;
            }
            Apply(new AudioClipEdit { Start = s, LoopStart = ls, LoopEnd = le }, "Clip region");
        }

        // ------------------------------------------------------------------ parts

        ComboBox Combo(IEnumerable<string> labels, Action<int> chosen, string tip, string name)
        {
            var box = new ComboBox { Width = 120, MinHeight = 24, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
            foreach (var l in labels) box.Items.Add(l);
            ToolTip.SetTip(box, tip);
            AutomationProperties.SetName(box, name);
            box.SelectionChanged += (s, e) => { if (!loading && box.SelectedIndex >= 0) chosen(box.SelectedIndex); };
            return box;
        }

        TextBox Box(string tip, Action commit, string name)
        {
            var box = new TextBox { Width = 58, MinHeight = 24, FontSize = 12, Padding = new Thickness(4, 2, 4, 2), VerticalContentAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(box, tip);
            AutomationProperties.SetName(box, name);
            box.AddHandler(KeyDownEvent, (s, e) =>
            {
                if (e.Key == Key.Enter) { commit(); e.Handled = true; }
                else if (e.Key == Key.Escape) { Refresh(force: true); e.Handled = true; }
            }, RoutingStrategies.Tunnel);
            box.LostFocus += (s, e) => commit();
            return box;
        }

        static Button Btn(string text, string tip, Action run, string name)
        {
            var b = StudioButtons.KeepFocus(new Button { Content = text, Padding = new Thickness(5, 1, 5, 1), MinHeight = 24, FontSize = 11 });
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, name);
            b.Click += (s, e) => run();
            return b;
        }

        static CheckBox Check(string label, string tip, Action changed)
        {
            var box = new CheckBox { Content = label, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, Margin = new Thickness(0, 2, 0, 0) };
            ToolTip.SetTip(box, tip);
            AutomationProperties.SetName(box, label);
            box.IsCheckedChanged += (s, e) => changed();
            return box;
        }

        static TextBlock Section(string text) => new TextBlock
        {
            Text = text.ToUpperInvariant(),
            Foreground = StudioTokens.TextSecondaryBrush,
            FontSize = 11,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 4, 0, 4),
        };

        static TextBlock Label(string text, double width) => new TextBlock { Text = text, FontSize = 12, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center, Width = width };

        static TextBlock Unit(string text) => new TextBlock { Text = text, FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center };

        static Control Row(string label, Control control)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2), LastChildFill = false };
            var l = Label(label, 68);
            DockPanel.SetDock(l, Dock.Left);
            row.Children.Add(l);
            DockPanel.SetDock(control, Dock.Left);
            row.Children.Add(control);
            return row;
        }
    }
}
