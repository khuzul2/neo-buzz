// Fork (docs/specs/pitch.md section 9): "Convert to MIDI" in the studio. The right-click menu of an audio clip (Session slot, Arrangement
// clip, the Audio page) offers Melody, Harmony and Drums; each opens a small dialog (what to listen for, quantize) and then runs the
// conversion on a worker with a progress bar and Cancel; the result is a new MIDI track with an instrument and a clip where the source is,
// one undo step.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Core.AudioLibrary.AudioToMidi;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    /// <summary>What the dialog asks for.</summary>
    internal readonly record struct ConvertChoice(ConvertOptions Options, ConvertApply Apply);

    internal static class ConvertFlow
    {
        /// <summary>Tests answer here instead of the dialog: (the clip's name, the mode) → the choice, or null for cancel.</summary>
        public static Func<string, ConvertMode, ConvertChoice?> Responder { get; set; }

        /// <summary>What the last conversion did, or why it didn't (tests, and a line for the status).</summary>
        public static string LastSummary { get; private set; } = "";

        /// <summary>The outcome of the last conversion (tests).</summary>
        public static ConvertOutcome LastOutcome { get; private set; }

        static ReBuzzCore Core => StudioHost.Current?.Core;

        public static readonly (string Label, ConvertMode Mode)[] Modes =
        {
            ("Melody", ConvertMode.Melody), ("Harmony", ConvertMode.Harmony), ("Drums", ConvertMode.Drums),
        };

        /// <summary>"Convert to MIDI ▸ Melody / Harmony / Drums" for the audio clip <paramref name="clip"/> gives (menus of the Session, the Arrangement and the Audio page).</summary>
        public static MenuItem Menu(Func<IPattern> clip, bool enabled = true)
        {
            var root = new MenuItem { Header = "Convert to MIDI", Tag = "Convert to MIDI", IsEnabled = enabled };
            foreach (var (label, mode) in Modes)
            {
                var item = new MenuItem { Header = label + "…", Tag = "Convert to MIDI: " + label };
                var m = mode;
                item.Click += (_, _) => _ = ConvertClip(clip(), m);
                root.Items.Add(item);
            }
            return root;
        }

        /// <summary>The menu's items for a button's menu (the Audio page).</summary>
        public static ContextMenu ButtonMenu(Func<IPattern> clip)
        {
            var menu = new ContextMenu();
            foreach (var (label, mode) in Modes)
            {
                var item = new MenuItem { Header = label + "…", Tag = "Convert to MIDI: " + label };
                var m = mode;
                item.Click += (_, _) => _ = ConvertClip(clip(), m);
                menu.Items.Add(item);
            }
            return menu;
        }

        public static async Task ConvertClip(IPattern clip, ConvertMode mode)
        {
            var layout = Core?.SessionLayout;
            if (layout == null || clip == null) return;
            var source = layout.ConvertSourceOfClip(clip, out string error);
            if (source == null) { await Say(error); return; }
            string blocker = layout.ConvertBlocker(mode);
            if (blocker != null) { await Say(blocker); return; }
            if (Responder != null)
            {
                var answer = Responder(source.Name, mode);
                if (answer == null) return;
                try
                {
                    var options = answer.Value.Options;
                    options.Mode = mode;
                    var result = await layout.ConvertAsync(source, options, null, CancellationToken.None);
                    Apply(layout, source, result, answer.Value.Apply);
                }
                catch (Exception e)
                {
                    LastSummary = "Couldn't convert: " + e.Message;
                }
                return;
            }
            await StudioDialog.Run(new ConvertWindow(layout, source, mode));
        }

        static async Task Say(string text)
        {
            LastSummary = text;
            await Dialogs.Ask(text, Product.Name, UserButtons.Ok);
        }

        /// <summary>Puts a result in the song and sets <see cref="LastSummary"/>.</summary>
        internal static bool Apply(SessionLayout layout, StemSource source, ConvertResult result, ConvertApply apply)
        {
            var outcome = layout.ApplyConversion(source, result, apply, out string error);
            if (outcome == null)
            {
                LastSummary = "Couldn't put the MIDI in the song: " + error;
                LastOutcome = null;
                return false;
            }
            LastOutcome = outcome;
            LastSummary = $"{result.Summary} {outcome.Instrument} plays it on a new track, \"{outcome.Machine.Name}\".";
            return true;
        }

        // ------------------------------------------------------------------ the window

        /// <summary>The dialog, then the same window as a progress view while the job runs.</summary>
        internal sealed class ConvertWindow : Window
        {
            readonly SessionLayout layout;
            readonly StemSource source;
            readonly ConvertMode mode;
            readonly ComboBox range = Choice("Any melody instrument", "Voice", "Guitar", "Bass");
            readonly ComboBox sensitivity = Choice("Normal", "More notes", "Fewer notes");
            readonly CheckBox bends = new CheckBox { Content = "Pitch bends (a slide or vibrato stays one note)" };
            readonly ComboBox quantize = Choice("Off", "1/4", "1/8", "1/16", "1/32");
            readonly ComboBox strength = Choice("100%", "75%", "50%", "25%");
            readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = StudioTokens.TextSecondaryBrush };
            readonly Button go = StudioDialog.Primary("Convert");
            readonly Button cancel = StudioDialog.Secondary();
            readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 1, Height = 8, MinWidth = 380 };
            readonly CancellationTokenSource cts = new();
            bool running;

            internal ProgressBar Bar => bar;
            internal Button GoButton => go;
            internal Button CancelButton => cancel;
            internal ComboBox QuantizeBox => quantize;
            internal ConvertMode Mode => mode;

            static ComboBox Choice(params string[] items)
            {
                var box = new ComboBox { MinWidth = 220 };
                foreach (var i in items) box.Items.Add(i);
                box.SelectedIndex = 0;
                return box;
            }

            static Control Row(string label, Control c) =>
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center }, c } };

            public ConvertWindow(SessionLayout layout, StemSource source, ConvertMode mode)
            {
                this.layout = layout;
                this.source = source;
                this.mode = mode;
                string what = mode switch
                {
                    ConvertMode.Melody => "the melody (one note at a time: a voice, a lead, a bass line)",
                    ConvertMode.Harmony => "the notes (chords, piano, guitar: several at once)",
                    _ => "the drum hits (kick, snare, hats, toms, cymbals)",
                };
                Title = "Convert to MIDI: " + mode;
                SizeToContent = SizeToContent.WidthAndHeight;
                CanResize = false;
                ShowInTaskbar = false;
                strength.SelectedIndex = 0;
                var rows = new List<Control>
                {
                    new TextBlock
                    {
                        Text = $"Finds {what} in \"{source.Name}\" on this computer and puts it on a new MIDI track ({SessionLayout.InstrumentFor(mode)}), " +
                               "in a clip where this one is. Undo takes it all back.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                    },
                };
                if (mode == ConvertMode.Melody) { rows.Add(Row("Listen for", range)); rows.Add(bends); }
                else rows.Add(Row("Sensitivity", sensitivity));
                rows.Add(Row("Quantize", quantize));
                rows.Add(Row("Strength", strength));
                bar.IsVisible = false;
                rows.Add(status);
                rows.Add(bar);
                rows.Add(StudioDialog.Buttons(go, cancel));
                Content = StudioDialog.Body(rows.ToArray());
                go.Click += (_, _) => _ = Start();
                cancel.Click += (_, _) =>
                {
                    cts.Cancel();
                    if (!running) Close();
                };
            }

            internal (ConvertOptions, ConvertApply) Choices()
            {
                var o = new ConvertOptions { Mode = mode };
                switch (mode)
                {
                    case ConvertMode.Melody:
                        o.Range = new[] { "full", "voice", "guitar", "bass" }[Math.Max(0, range.SelectedIndex)];
                        o.PitchBends = bends.IsChecked == true;
                        break;
                    case ConvertMode.Harmony:
                        (o.OnsetThreshold, o.FrameThreshold) = sensitivity.SelectedIndex switch { 1 => (0.4, 0.25), 2 => (0.6, 0.35), _ => (0.5, 0.3) };
                        break;
                    default:
                        o.DrumThreshold = sensitivity.SelectedIndex switch { 1 => 3.0, 2 => 6.0, _ => 4.0 };
                        break;
                }
                var a = new ConvertApply
                {
                    QuantizeDivision = new[] { 0, 1, 2, 4, 8 }[Math.Max(0, quantize.SelectedIndex)],
                    QuantizeStrength = new[] { 1.0, 0.75, 0.5, 0.25 }[Math.Max(0, strength.SelectedIndex)],
                };
                return (o, a);
            }

            async Task Start()
            {
                var (options, apply) = Choices();
                running = true;
                foreach (var c in new Control[] { range, sensitivity, bends, quantize, strength }) c.IsEnabled = false;
                go.IsEnabled = false;
                bar.IsVisible = true;
                status.Text = "Converting… (Cancel stops it)";
                try
                {
                    var result = await layout.ConvertAsync(source, options, f => Dispatcher.UIThread.Post(() => bar.Value = f), cts.Token);
                    Apply(layout, source, result, apply);
                    status.Text = LastSummary;
                }
                catch (OperationCanceledException)
                {
                    LastSummary = "Conversion cancelled.";
                }
                catch (Exception e)
                {
                    LastSummary = "Couldn't convert: " + e.Message;
                    await Dialogs.Ask(LastSummary, Product.Name, UserButtons.Ok);
                }
                running = false;
                Close();
            }
        }
    }
}
