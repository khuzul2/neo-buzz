// Fork (docs/specs/midi-mapping.md, MM-C): the Inspector's MIDI page of the selected track: the slots laid out as the knobs of the controllers
// (K1..KN, N the largest knob group), each with the machine and parameter it holds, its value, and a grey ring when it is only the generator's
// pin of the same number (the fallback). A parameter dragged onto a slot (Alt+drag from a control, or from the Device panel) fills it; a page
// chooser, the controllers' knob groups ("Teach knobs": turn knob 1 to N) and their keys role are here too.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Midi.Mapping;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Views.Inspector
{
    /// <summary>What a drag of a parameter carries: its mapping target, in one application data format.</summary>
    internal static class MidiTargetDrag
    {
        public static readonly DataFormat<string> Format = DataFormat.CreateStringApplicationFormat("neobuzz.midi-target");

        public static DataTransfer Package(MapTarget target)
        {
            var transfer = new DataTransfer();
            transfer.Add(DataTransferItem.Create(Format, string.Join("\t", target.Machine, target.Param, target.Track.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            return transfer;
        }

        public static MapTarget Read(IDataTransfer transfer)
        {
            var text = transfer?.TryGetValue(Format);
            if (text == null) return null;
            var parts = text.Split('\t');
            return parts.Length == 3 && int.TryParse(parts[2], out int track) ? MapTarget.Parameter(parts[0], parts[1], track) : null;
        }
    }

    internal sealed class MidiPageSection : StackPanel
    {
        /// <summary>One knob's slot of the page.</summary>
        internal sealed class SlotView : Border
        {
            readonly MidiPageSection owner;
            readonly TextBlock knob = new TextBlock { FontSize = StudioTokens.FontCaption, FontWeight = FontWeight.SemiBold };
            readonly TextBlock what = new TextBlock { FontSize = StudioTokens.FontCaption, TextTrimming = TextTrimming.CharacterEllipsis };
            readonly TextBlock value = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextTrimming = TextTrimming.CharacterEllipsis };
            public int Index { get; }
            public MapInfo Info { get; private set; }

            public SlotView(MidiPageSection owner, int index)
            {
                this.owner = owner;
                Index = index;
                Width = 66;
                Height = 50;
                Margin = new Thickness(0, 0, 4, 4);
                Padding = new Thickness(4, 2);
                CornerRadius = new CornerRadius(6);
                BorderThickness = new Thickness(1.5);
                var stack = new StackPanel();
                stack.Children.Add(knob);
                stack.Children.Add(what);
                stack.Children.Add(value);
                Child = stack;
                AutomationProperties.SetName(this, "Knob " + (index + 1));
                DragDrop.SetAllowDrop(this, true);
                AddHandler(DragDrop.DragOverEvent, (s, e) => e.DragEffects = MidiTargetDrag.Read(e.DataTransfer) != null ? DragDropEffects.Copy : DragDropEffects.None);
                AddHandler(DragDrop.DropEvent, (s, e) =>
                {
                    if (MidiTargetDrag.Read(e.DataTransfer) is { } t) Assign(t);
                });
                PointerPressed += (s, e) =>
                {
                    if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) Menu();
                };
            }

            /// <summary>Puts a parameter on this slot (a drop; the menu's Free is Assign(null)).</summary>
            public string Assign(MapTarget target) => owner.AssignSlot(Index, target);

            void Menu()
            {
                var free = new MenuItem { Header = "Free this knob", IsEnabled = Info?.Target != null && !Info.Fallback };
                free.Click += (s, e) => Assign(null);
                new ContextMenu { ItemsSource = new object[] { free } }.Open(this);
            }

            public void Show(MapInfo info, string valueText)
            {
                Info = info;
                knob.Text = "K" + (Index + 1);
                bool free = info.Target == null;
                what.Text = free ? "free" : info.Target.Machine + " · " + info.Target.ParamName;
                value.Text = valueText ?? (free ? "" : info.Range == "full" ? "" : info.Range);
                BorderBrush = free ? StudioTokens.SurfaceBorderBrush : info.Fallback ? StudioTokens.TextPlaceholderBrush : StudioTokens.AccentBrush;
                BorderThickness = new Thickness(info.Fallback ? 1.5 : 1.5);
                Background = free ? null : StudioTokens.SurfaceControlBrush;
                knob.Foreground = free ? StudioTokens.TextPlaceholderBrush : StudioTokens.TextPrimaryBrush;
                what.Foreground = free || info.Fallback ? StudioTokens.TextSecondaryBrush : StudioTokens.TextPrimaryBrush;
                Opacity = free ? 0.8 : 1;
                string tip = free ? "Free: drop a parameter here, or map a knob in map mode" : info.Fallback ? $"The generator's pinned parameter of the same number ({info.Target.Machine} · {info.Target.ParamName}); drop another to take the slot" : $"{info.Target.Machine} · {info.Target.ParamName}";
                ToolTip.SetTip(this, tip);
                AutomationProperties.SetName(this, "Knob " + (Index + 1) + (free ? ", free" : info.Fallback ? ", pin " + info.Target.ParamName : ", " + info.Target.ParamName));
            }
        }

        readonly ReBuzzCore core;
        readonly TextBlock title = new TextBlock { Text = "MIDI PAGE", FontSize = StudioTokens.FontCaption, FontWeight = FontWeight.SemiBold, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 0, 0, 4) };
        readonly TextBlock track = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, Margin = new Thickness(0, 0, 0, 4) };
        readonly Button previous = new Button { Content = "◀", Padding = new Thickness(6, 1) };
        readonly Button next = new Button { Content = "▶", Padding = new Thickness(6, 1) };
        readonly Button add = new Button { Content = "+", Padding = new Thickness(8, 1) };
        readonly Button remove = new Button { Content = "−", Padding = new Thickness(8, 1) };
        readonly TextBlock pageName = new TextBlock { VerticalAlignment = VerticalAlignment.Center, MinWidth = 60, FontSize = StudioTokens.FontCaption };
        readonly WrapPanel slots = new WrapPanel { Orientation = Orientation.Horizontal };
        readonly TextBlock status = new TextBlock { FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) };
        readonly Button teach = new Button { Content = "Teach knobs", Padding = new Thickness(8, 2), Margin = new Thickness(0, 4, 8, 0) };
        readonly CheckBox autoArm = new CheckBox { Content = "Selecting a track arms it", FontSize = StudioTokens.FontCaption };
        readonly StackPanel controllers = new StackPanel { Spacing = 2, Margin = new Thickness(0, 4, 0, 0) };
        readonly List<SlotView> views = new List<SlotView>();
        string signature;
        bool updating;

        public MidiPageSection(ReBuzzCore core)
        {
            this.core = core;
            Margin = new Thickness(0, 10, 0, 0);
            IsVisible = false;
            AutomationProperties.SetName(this, "MIDI page");
            AutomationProperties.SetName(previous, "Previous page");
            AutomationProperties.SetName(next, "Next page");
            AutomationProperties.SetName(add, "Add page");
            AutomationProperties.SetName(remove, "Remove page");
            AutomationProperties.SetName(teach, "Teach knobs");
            AutomationProperties.SetName(autoArm, "Selecting a track arms it");
            ToolTip.SetTip(teach, "Turn knob 1, then 2, up to the last, on the controller to teach: its knobs then drive the slots below");
            ToolTip.SetTip(autoArm, "Selecting a track arms it for MIDI, and lets go of the one the selection armed before (tracks you armed by hand stay armed)");
            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(0, 0, 0, 6) };
            pager.Children.Add(previous);
            pager.Children.Add(pageName);
            pager.Children.Add(next);
            pager.Children.Add(add);
            pager.Children.Add(remove);
            var tools = new StackPanel { Orientation = Orientation.Horizontal };
            tools.Children.Add(teach);
            Children.Add(title);
            Children.Add(track);
            Children.Add(pager);
            Children.Add(slots);
            Children.Add(status);
            Children.Add(tools);
            Children.Add(controllers);
            Children.Add(autoArm);
            previous.Click += (s, e) => Page(-1);
            next.Click += (s, e) => Page(1);
            add.Click += (s, e) => { if (Mapping?.SelectedTrackId is { } id) Mapping.AddPage(id); };
            remove.Click += (s, e) =>
            {
                if (Mapping?.SelectedTrackId is { } id && Mapping.PagesOf(id) is { } p) Mapping.RemovePage(id, p.Current);
            };
            teach.Click += (s, e) =>
            {
                var mm = Mapping;
                if (mm == null) return;
                if (mm.Listening is { Teach: true }) mm.FinishTeach();
                else mm.BeginTeach(null, mm.KnobCount);
            };
            autoArm.IsCheckedChanged += (s, e) => { if (!updating && Mapping != null) Mapping.AutoArm = autoArm.IsChecked == true; };
            if (Mapping is { } m)
            {
                m.Changed += () => signature = null;
                m.TeachProgress += (d, n) => signature = null;
            }
        }

        MidiMapping Mapping => core.MidiMapping;

        /// <summary>The slots, in knob order (tests).</summary>
        public IReadOnlyList<SlotView> Slots => views;

        public string StatusText => status.Text;

        public string PageText => pageName.Text;

        internal Button TeachButton => teach;
        internal Button AddPageButton => add;
        internal Button RemovePageButton => remove;
        internal Button PreviousPageButton => previous;
        internal Button NextPageButton => next;
        internal CheckBox AutoArmBox => autoArm;
        internal StackPanel Controllers => controllers;

        void Page(int delta)
        {
            var mm = Mapping;
            if (mm?.SelectedTrackId is not { } id || mm.PagesOf(id) is not { } p) return;
            int to = Math.Clamp(p.Current + delta, 0, p.Pages.Count - 1);
            if (to != p.Current) mm.SelectPage(id, to);
        }

        /// <summary>Puts a parameter on a slot of the current page of the selected track, or frees it (null). Returns the reason it did not, or null.</summary>
        public string AssignSlot(int index, MapTarget target)
        {
            var mm = Mapping;
            if (mm?.SelectedTrackId is not { } id) return "No track is selected.";
            var pages = mm.PagesOf(id);
            var result = mm.SetSlot(id, pages?.Current ?? 0, index, target);
            if (!result.Ok) status.Text = result.Error;
            return result.Error;
        }

        public void Refresh()
        {
            var mm = Mapping;
            var entry = mm?.SelectedTrack;
            if (mm == null || entry == null) { IsVisible = false; return; }
            IsVisible = true;
            int count = mm.KnobCount;
            var infos = mm.PageSlots(count);
            var pages = mm.PagesOf(entry.Id);
            string sig = entry.Id + "|" + (pages?.Current ?? 0) + "/" + (pages?.Pages.Count ?? 1) + "|" + string.Join(",", infos.Select(i => i.Target?.Text + (i.Fallback ? "*" : "") + i.Range + i.Takeover + i.Invert)) + "|" + mm.IsListening + "|" + string.Join(",", mm.Controllers().Select(c => c.Id + c.Knobs + c.Keys + c.Connected)) + "|" + mm.AutoArm;
            bool structure = sig != signature;
            signature = sig;
            if (structure)
            {
                track.Text = entry.Name;
                int current = pages?.Current ?? 0, total = pages?.Pages.Count ?? 1;
                pageName.Text = (pages?.Pages[Math.Clamp(current, 0, total - 1)].Name ?? "Page 1") + (total > 1 ? $" ({current + 1}/{total})" : "");
                previous.IsEnabled = current > 0;
                next.IsEnabled = current < total - 1;
                add.IsEnabled = total < TrackPages.MaxPages;
                remove.IsEnabled = total > 1;
                while (views.Count < count)
                {
                    var v = new SlotView(this, views.Count);
                    views.Add(v);
                    slots.Children.Add(v);
                }
                while (views.Count > count) { slots.Children.Remove(views[^1]); views.RemoveAt(views.Count - 1); }
                updating = true;
                autoArm.IsChecked = mm.AutoArm;
                updating = false;
                var listening = mm.Listening;
                teach.Content = listening is { Teach: true } ? "Done teaching" : "Teach knobs";
                status.Text = listening is { Teach: true } t ? $"Turn knob {t.Taught.Count + 1} of {t.TeachCount} on the controller to teach…" : StatusLine(mm);
                FillControllers(mm);
            }
            for (int i = 0; i < count && i < views.Count; i++) views[i].Show(infos[i], ValueOf(mm, infos[i]));
        }

        static string StatusLine(MidiMapping mm)
        {
            var cs = mm.Controllers();
            if (cs.Count == 0) return "No controller is open. Connect one, then teach its knobs.";
            if (cs.All(c => c.Knobs == 0)) return "Teach a controller's knobs to drive these slots.";
            return "Its knobs drive the slots above. A grey ring is the generator's pin of that number.";
        }

        static string ValueOf(MidiMapping mm, MapInfo info)
        {
            if (info.Target == null || info.Target.IsCommand) return null;
            try
            {
                var p = mm.ParameterOf(info.Target);
                return p == null ? "missing" : p.DescribeValue(p.GetValue(info.Target.Track));
            }
            catch (Exception) { return null; }
        }

        void FillControllers(MidiMapping mm)
        {
            controllers.Children.Clear();
            foreach (var (id, connected, knobs, keys) in mm.Controllers())
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                var keysBox = new CheckBox { Content = "Keys", IsChecked = keys, FontSize = StudioTokens.FontCaption, MinWidth = 0 };
                AutomationProperties.SetName(keysBox, "Keys follow the selected track: " + id);
                ToolTip.SetTip(keysBox, "This controller's notes play the selected track's generator");
                string device = id;
                keysBox.IsCheckedChanged += (s, e) => { if (!updating && Mapping != null) Mapping.SetKeysRole(device, keysBox.IsChecked == true); };
                row.Children.Add(new TextBlock { Text = id + (connected ? "" : " (not connected)"), FontSize = StudioTokens.FontCaption, VerticalAlignment = VerticalAlignment.Center, MinWidth = 70, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120 });
                row.Children.Add(new TextBlock { Text = knobs == 0 ? "no knobs taught" : knobs + " knobs", FontSize = StudioTokens.FontCaption, Foreground = StudioTokens.TextSecondaryBrush, VerticalAlignment = VerticalAlignment.Center });
                row.Children.Add(keysBox);
                controllers.Children.Add(row);
            }
        }
    }
}
