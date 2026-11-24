// Fork (docs/specs/audio-to-midi.md, "Slice to new MIDI track (SL1 as built)"): "Slice to new MIDI track…" in the studio. The right-click menu of an audio clip
// (Session slot, Arrangement clip) and the Audio page offer it next to Convert to MIDI; it opens a small dialog (what to slice by, and how many slices that
// makes: at most 16, the number of Neo Sampler's zones) and then puts the slices, a new MIDI track with a Neo Sampler and a clip of the notes in the song,
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
using ReBuzz.Core.Clips;
using ReBuzz.Core.Session;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell
{
    using Control = Avalonia.Controls.Control;

    internal static class SliceFlow
    {
        /// <summary>Tests answer here instead of the dialog: the clip's name → what to slice by, or null for cancel.</summary>
        public static Func<string, SliceBy?> Responder { get; set; }

        /// <summary>What the last slicing did, or why it didn't (tests, and a line for the status).</summary>
        public static string LastSummary { get; private set; } = "";

        /// <summary>The outcome of the last slicing (tests).</summary>
        public static SliceOutcome LastOutcome { get; private set; }

        public const string MenuLabel = "Slice to new MIDI track…";

        static ReBuzzCore Core => StudioHost.Current?.Core;

        /// <summary>"Slice to new MIDI track…" for the audio clip <paramref name="clip"/> gives (menus of the Session and the Arrangement).</summary>
        public static MenuItem Menu(Func<IPattern> clip, bool enabled = true)
        {
            var item = new MenuItem { Header = MenuLabel, Tag = "Slice to new MIDI track", IsEnabled = enabled };
            item.Click += (_, _) => _ = SliceClip(clip());
            return item;
        }

        /// <summary>The same entry for a context menu (the Audio page).</summary>
        public static void AddTo(ContextMenu menu, Func<IPattern> clip)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Menu(clip));
        }

        public static async Task SliceClip(IPattern clip)
        {
            var layout = Core?.SessionLayout;
            if (layout == null || clip == null) return;
            var source = layout.SliceSourceOfClip(clip, out string error);
            if (source == null) { await Say(error); return; }
            if (Responder != null)
            {
                var by = Responder(source.Name);
                if (by == null) return;
                try
                {
                    var plan = await layout.PlanSlicesAsync(source, by.Value, CancellationToken.None);
                    Apply(layout, source, plan);
                }
                catch (Exception e)
                {
                    LastSummary = "Couldn't slice: " + e.Message;
                }
                return;
            }
            await StudioDialog.Run(new SliceWindow(layout, source));
        }

        static async Task Say(string text)
        {
            LastSummary = text;
            await Dialogs.Ask(text, Product.Name, UserButtons.Ok);
        }

        /// <summary>Puts a cut in the song and sets <see cref="LastSummary"/>.</summary>
        internal static bool Apply(SessionLayout layout, SliceSource source, SlicePlan plan)
        {
            var outcome = layout.ApplySlices(source, plan, out string error);
            if (outcome == null)
            {
                LastSummary = "Couldn't slice: " + error;
                LastOutcome = null;
                return false;
            }
            LastOutcome = outcome;
            LastSummary = $"Cut \"{source.Name}\" into {outcome.Slices.Count} slices: {outcome.Instrument} plays them on a new track, \"{outcome.Machine.Name}\".";
            return true;
        }

        // ------------------------------------------------------------------ the window

        internal sealed class SliceWindow : Window
        {
            readonly SessionLayout layout;
            readonly SliceSource source;
            readonly ComboBox by = new ComboBox { MinWidth = 220 };
            readonly TextBlock count = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, FontWeight = Avalonia.Media.FontWeight.SemiBold };
            readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Foreground = StudioTokens.TextSecondaryBrush };
            readonly Button go = StudioDialog.Primary("Slice");
            readonly Button cancel = StudioDialog.Secondary();
            readonly CancellationTokenSource cts = new();
            SlicePlan[] plans;

            internal ComboBox ByBox => by;
            internal Button GoButton => go;
            internal Button CancelButton => cancel;
            internal TextBlock CountText => count;
            /// <summary>The plans of the six choices, once they are made (tests).</summary>
            internal IReadOnlyList<SlicePlan> Plans => plans;
            /// <summary>Completes when the plans are in (tests).</summary>
            internal Task Ready { get; }

            public SliceWindow(SessionLayout layout, SliceSource source)
            {
                this.layout = layout;
                this.source = source;
                Title = "Slice to new MIDI track";
                SizeToContent = SizeToContent.WidthAndHeight;
                CanResize = false;
                ShowInTaskbar = false;
                foreach (var b in SliceMath.All)
                    by.Items.Add(new ComboBoxItem { Content = SliceMath.Label(b), Tag = b, IsEnabled = false });
                by.SelectedIndex = 0;
                count.Text = "Finding the slices…";
                go.IsEnabled = false;
                var rows = new List<Control>
                {
                    new TextBlock
                    {
                        Text = $"Cuts \"{source.Name}\" ({source.Region}) into slices, each one its own wave, and plays them from a Neo Sampler on a new MIDI track, " +
                               "with a note for each slice in a clip where this one is. The slices are not warped: they play at their own speed. Undo takes it all back.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { new TextBlock { Text = "Slice by", Width = 90, VerticalAlignment = VerticalAlignment.Center }, by } },
                    count,
                    status,
                    StudioDialog.Buttons(go, cancel),
                };
                Content = StudioDialog.Body(rows.ToArray());
                by.SelectionChanged += (_, _) => Refresh();
                go.Click += (_, _) => Start();
                cancel.Click += (_, _) => { cts.Cancel(); Close(); };
                Ready = Load();
            }

            async Task Load()
            {
                try
                {
                    plans = await Task.Run(() => SliceMath.All.Select(b => source.Plan(b)).ToArray(), cts.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception e)
                {
                    count.Text = "Couldn't read the audio: " + e.Message;
                    return;
                }
                int first = -1;
                for (int i = 0; i < plans.Length; i++)
                {
                    var item = (ComboBoxItem)by.Items[i]!;
                    var p = plans[i];
                    item.IsEnabled = p.Ok;
                    ToolTip.SetShowOnDisabled(item, true);
                    item.Content = p.Ok ? SliceMath.Label(p.By) : SliceMath.Label(p.By) + (p.Found > 0 ? $" ({p.Found})" : "");
                    ToolTip.SetTip(item, p.Ok ? $"{p.Slices.Count} slice{(p.Slices.Count == 1 ? "" : "s")}" : p.Why);
                    if (p.Ok && first < 0) first = i;
                }
                by.SelectedIndex = plans[0].Ok ? 0 : Math.Max(0, first);
                Refresh();
            }

            SlicePlan Chosen => plans != null && by.SelectedIndex >= 0 ? plans[by.SelectedIndex] : null;

            void Refresh()
            {
                var p = Chosen;
                if (p == null) return;
                if (!p.Ok)
                {
                    count.Text = p.Why ?? "Nothing to slice.";
                    status.Text = "";
                    go.IsEnabled = false;
                    return;
                }
                count.Text = $"{p.Slices.Count} slice{(p.Slices.Count == 1 ? "" : "s")}" + (p.Capped ? $" (the {p.Found} transients are merged to the {p.Slices.Count} strongest)" : "") +
                             $" · limit {SliceMath.MaxSlices}";
                string blocker = layout.SliceBlocker(p.Slices.Count);
                status.Text = blocker ?? $"Waves {FirstSlots(p.Slices.Count)}; keys {SessionLayout.SliceKeyBase} and up.";
                go.IsEnabled = blocker == null;
            }

            string FirstSlots(int n)
            {
                var waves = Core?.SongCore.Wavetable.Waves;
                var free = new List<int>();
                for (int i = 0; waves != null && i < waves.Count && free.Count < n; i++)
                    if (waves[i] == null || waves[i].Layers.Count == 0) free.Add(i + 1);
                return free.Count == 0 ? "" : free.Count == 1 ? $"{free[0]}" : $"{free[0]}–{free[^1]}";
            }

            void Start()
            {
                var p = Chosen;
                if (p == null || !p.Ok) return;
                go.IsEnabled = false;
                bool ok = Apply(layout, source, p);
                if (!ok) _ = Dialogs.Ask(LastSummary, Product.Name, UserButtons.Ok);
                Close();
            }
        }
    }
}
