// Fork (docs/specs/history-snapshots.md HS2): the Snapshots section of the inspector's History tab. The song's full snapshots
// (kept in <song>.history): a list with name, time and manual or auto; save, rename, delete; a diff (machines, wires, parameters,
// patterns, arrangement) of two snapshots or of one against the song now; an A/B toggle between two snapshots; restore.
// Restore while the song plays is applied live at the next beat or bar (HS3: the "Apply at" choice, default a bar), or waits for the
// transport to stop when a machine can't be prepared safely while playing; the line under the buttons says which.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ReBuzz.Core;
using ReBuzz.Core.SongHistory;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Inspector
{
    internal sealed class SnapshotsSection : StackPanel, IDisposable
    {
        /// <summary>The diff lines shown (the agent tool lists them all).</summary>
        const int MaxDiffLines = 60;

        readonly ReBuzzCore core;
        readonly StackPanel list = new();
        readonly TextBox nameBox;
        readonly Button save, rename, delete, restore, diff, setA, setB, toggle;
        readonly CheckBox embed;
        readonly ComboBox applyAt;
        readonly TextBlock status, diffText;
        string selected, a, b;
        bool pending, disposed;

        public SnapshotsSection(ReBuzzCore core)
        {
            this.core = core;
            Margin = new Thickness(12, 0, 12, 8);
            Spacing = 4;
            AutomationProperties.SetName(this, "Snapshots");

            Children.Add(new TextBlock { Text = "Snapshots", FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextPrimaryBrush });
            nameBox = new TextBox { Watermark = "Name", FontSize = 12, MinHeight = 24 };
            AutomationProperties.SetName(nameBox, "Snapshot name");
            save = Small("Save", "Save a snapshot of the whole song under this name", () => Save());
            rename = Small("Rename", "Rename the selected snapshot to this name", () => { if (selected != null && core.Snapshots.Rename(selected, nameBox.Text)) Refresh(); });
            var namePanel = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(save, Dock.Right);
            namePanel.Children.Add(save);
            namePanel.Children.Add(nameBox);
            Children.Add(namePanel);

            list.Margin = new Thickness(0, 2, 0, 2);
            Children.Add(new Border { BorderBrush = StudioTokens.SurfaceBorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), MaxHeight = 160, Child = new ScrollViewer { Content = list } });

            delete = Small("Delete", "Delete the selected snapshot", () => { if (selected != null) { core.Snapshots.Delete(selected); selected = null; Refresh(); } });
            restore = Small("Restore", "Put the song back as the selected snapshot had it", () => Restore(selected));
            diff = Small("Diff", "What changed: A against B, or the selected snapshot against the song now", () => ShowDiff());
            setA = Small("Set A", "Make the selected snapshot A", () => MarkA(selected));
            setB = Small("Set B", "Make the selected snapshot B", () => MarkB(selected));
            toggle = Small("A/B", "Switch the song between A and B (live while playing, at the Apply choice)", () => ToggleAb());
            applyAt = new ComboBox { ItemsSource = new[] { "Bar", "Beat", "Now" }, SelectedIndex = 0, FontSize = 11, MinHeight = 22, Margin = new Thickness(0, 0, 4, 2) };
            ToolTip.SetTip(applyAt, "While the song plays, a restore applies at the next bar, beat or tick");
            AutomationProperties.SetName(applyAt, "Apply restore at");
            Children.Add(new WrapPanel { Children = { restore, applyAt, rename, delete, diff, setA, setB, toggle } });

            embed = new CheckBox { Content = "Embed history in song on save", FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, IsChecked = core.Snapshots.Embed };
            ToolTip.SetTip(embed, "Also write the snapshots into the song file (a section other Buzz versions ignore)");
            embed.IsCheckedChanged += (s, e) => core.Snapshots.Embed = embed.IsChecked == true;
            Children.Add(embed);

            status = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap };
            diffText = new TextBlock { FontSize = 11, Foreground = StudioTokens.TextPrimaryBrush, TextWrapping = TextWrapping.Wrap };
            Children.Add(status);
            Children.Add(diffText);

            core.Snapshots.Changed += OnChanged;
            core.Snapshots.RestoreFinished += OnRestoreFinished;
            core.PropertyChanged += OnCore;
            Refresh();
        }

        internal ComboBox ApplyAt => applyAt;

        RestoreQuantize Quantize => applyAt.SelectedIndex switch { 1 => RestoreQuantize.Beat, 2 => RestoreQuantize.Now, _ => RestoreQuantize.Bar };

        void OnRestoreFinished() =>
            Dispatcher.UIThread.Post(() => { if (!disposed) { status.Text = "Restored."; UpdateButtons(); } }, DispatcherPriority.Background);

        Button Small(string text, string tip, Action click)
        {
            var b = new Button { Content = text, FontSize = 11, Padding = new Thickness(8, 2, 8, 2), MinHeight = 22, Margin = new Thickness(0, 0, 4, 2), Focusable = true };
            ToolTip.SetTip(b, tip);
            AutomationProperties.SetName(b, text);
            b.Click += (s, e) => click();
            return b;
        }

        internal IReadOnlyList<Border> Rows => list.Children.OfType<Border>().ToList();
        internal string StatusText => status.Text;
        internal string DiffText => diffText.Text;
        internal Button RestoreButton => restore;
        internal Button ToggleButton => toggle;
        internal TextBox NameBox => nameBox;

        void OnChanged()
        {
            if (disposed || pending) return;
            pending = true;
            Dispatcher.UIThread.Post(() => { pending = false; if (!disposed) Refresh(); }, DispatcherPriority.Background);
        }

        void OnCore(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is "Playing" or "PlayRequested" or "Modified")
                Dispatcher.UIThread.Post(() => { if (!disposed) UpdateButtons(); }, DispatcherPriority.Background);
        }

        internal SnapshotManifest Save()
        {
            string name = string.IsNullOrWhiteSpace(nameBox.Text) ? null : nameBox.Text.Trim();
            var m = core.Snapshots.Capture(name ?? "Snapshot " + DateTime.Now.ToString("HH:mm:ss"));
            if (m != null) { nameBox.Text = ""; selected = m.Id; }
            Refresh();
            return m;
        }

        /// <summary>Restores a snapshot: stopped, at once; playing, live at the next beat or bar, or when the transport stops. True if it was restored or scheduled.</summary>
        internal bool Restore(string id)
        {
            var m = id == null ? null : core.Snapshots.Find(id);
            if (m == null) return false;
            try
            {
                var r = core.Snapshots.Restore(m, Quantize);
                diffText.Text = "";
                status.Text = r.Mode switch
                {
                    "applied" => $"Restored \"{m.Name}\". Undo returns the song to how it was.",
                    "deferred" => $"\"{m.Name}\" waits for the transport to stop: {r.Reason}",
                    _ => r.Waiting ? $"\"{m.Name}\" applies live at the next {r.Quantize}" + (r.WaitMs > 0 ? $" (in about {r.WaitMs / 1000:F1} s). Undo takes it back." : ".") : $"\"{m.Name}\": nothing differs from the song now.",
                };
                return true;
            }
            catch (InvalidOperationException e)
            {
                status.Text = e.Message;
                return false;
            }
        }

        internal void MarkA(string id) { a = id; Refresh(); }
        internal void MarkB(string id) { b = id; Refresh(); }

        internal void ToggleAb()
        {
            var ma = a == null ? null : core.Snapshots.Find(a);
            var mb = b == null ? null : core.Snapshots.Find(b);
            if (ma == null || mb == null) { status.Text = "Set A and B first: select a snapshot, then Set A or Set B."; return; }
            // The song is whichever of the two it was last switched to; anything else goes to A. The first switch keeps the song as it was in an auto snapshot (Restore does).
            string now = toggledTo == a ? b : a;
            toggledTo = now;
            if (Restore(now))
                status.Text = $"Now {(now == a ? "A" : "B")}: \"{core.Snapshots.Find(now)?.Name}\". Switch again for the other. " + status.Text;
        }

        string toggledTo;

        /// <summary>The diff text for A and B, else for the selected snapshot against the song now.</summary>
        internal string ShowDiff()
        {
            SnapshotManifest from, to = null;
            string what;
            var ma = a == null ? null : core.Snapshots.Find(a);
            var mb = b == null ? null : core.Snapshots.Find(b);
            if (ma != null && mb != null && (selected == null || selected == a || selected == b)) { from = ma; to = mb; what = $"\"{ma.Name}\" to \"{mb.Name}\""; }
            else
            {
                from = selected == null ? null : core.Snapshots.Find(selected);
                what = from == null ? "" : $"\"{from.Name}\" to the song now";
            }
            if (from == null) { diffText.Text = "Select a snapshot to compare."; return diffText.Text; }
            var changes = core.Snapshots.Diff(from, to);
            diffText.Text = changes.Count == 0 ? "No differences, " + what + "."
                : $"{changes.Count} changes, {what}:\n" + string.Join("\n", changes.Take(MaxDiffLines).Select(c => $"{c.Area}: {c.Subject}: {c.Text}")) + (changes.Count > MaxDiffLines ? "\n…" : "");
            return diffText.Text;
        }

        internal void Select(string id)
        {
            selected = id;
            var m = core.Snapshots.Find(id);
            if (m != null) nameBox.Text = m.Name;
            Refresh();
        }

        internal void Refresh()
        {
            list.Children.Clear();
            var items = core.Snapshots.List().OrderByDescending(m => m.Time).ToList();
            if (selected != null && items.All(m => m.Id != selected)) selected = null;
            if (a != null && items.All(m => m.Id != a)) a = null;
            if (b != null && items.All(m => m.Id != b)) b = null;
            foreach (var m in items)
                list.Children.Add(Row(m));
            if (items.Count == 0)
                list.Children.Add(new TextBlock { Text = "None yet. Save one, or they are taken on save and every few minutes of editing.", FontSize = 11, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(6) });
            embed.IsChecked = core.Snapshots.Embed;
            UpdateButtons();
        }

        Border Row(SnapshotManifest m)
        {
            bool sel = m.Id == selected;
            string tag = m.Id == a ? " [A]" : m.Id == b ? " [B]" : "";
            var name = new TextBlock { Text = m.Name + tag, FontSize = 12, Foreground = StudioTokens.TextPrimaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            IBrush small = sel ? StudioTokens.TextPrimaryBrush : StudioTokens.TextSecondaryBrush;
            var detail = new TextBlock { Text = $"{(m.IsAuto ? "auto" : "manual")}, {m.Time.ToLocalTime():HH:mm:ss}{(m.Pending ? ", saving…" : "")}", FontSize = 11, Foreground = small };
            var row = new Border
            {
                Child = new StackPanel { Children = { name, detail } },
                Padding = new Thickness(6, 3, 6, 3),
                Background = sel ? StudioTokens.SelectionFillBrush : Brushes.Transparent,
                Focusable = true,
                Tag = m.Id,
            };
            AutomationProperties.SetName(row, $"{m.Name}, {(m.IsAuto ? "auto" : "manual")}, {m.Time.ToLocalTime():HH:mm:ss}{tag}{(sel ? ", selected" : "")}");
            row.PointerPressed += (s, e) => { if (e.GetCurrentPoint(row).Properties.IsLeftButtonPressed) { Select(m.Id); e.Handled = true; } };
            row.KeyDown += (s, e) => { if (e.Key is Avalonia.Input.Key.Enter or Avalonia.Input.Key.Space) { Select(m.Id); e.Handled = true; } };
            return row;
        }

        void UpdateButtons()
        {
            bool any = selected != null;
            bool playing = core.PlayRequested;
            rename.IsEnabled = delete.IsEnabled = diff.IsEnabled = setA.IsEnabled = setB.IsEnabled = any || (a != null && b != null);
            restore.IsEnabled = any;
            toggle.IsEnabled = a != null && b != null;
            var d = core.Snapshots.Deferred;
            if (d != null) status.Text = $"\"{d.Value.Name}\" waits for the transport to stop: {d.Value.Reason}";
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            core.Snapshots.Changed -= OnChanged;
            core.Snapshots.RestoreFinished -= OnRestoreFinished;
            core.PropertyChanged -= OnCore;
        }
    }
}
