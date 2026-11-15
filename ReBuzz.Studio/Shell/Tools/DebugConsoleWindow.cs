// Fork (docs/specs/ux-overhaul.md P6b, §9): View ▸ Debug Console, after ReBuzz.exe's DebugWindow (a rich text box on Serilog): the
// engine's log (what machines and the engine report through DCWriteLine), newest at the bottom, with a level filter, a text filter,
// Clear, Copy and "Follow" (stays at the end). It reads DebugLog's memory, so it shows what was logged before it was opened.

using System;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Serilog.Events;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    internal sealed class DebugConsoleWindow : ToolWindow
    {
        public const string PlacementName = "DebugConsole";
        static DebugConsoleWindow current;

        static readonly LogEventLevel[] Levels = { LogEventLevel.Information, LogEventLevel.Warning, LogEventLevel.Error };

        readonly DebugLog log;
        internal readonly TextBox Text = new TextBox { IsReadOnly = true, AcceptsReturn = true, FontFamily = new FontFamily("Cascadia Mono, Menlo, Consolas, monospace"), FontSize = StudioTokens.FontBody };
        internal readonly ComboBox Level = new ComboBox { ItemsSource = new[] { "Information", "Warnings", "Errors" }, SelectedIndex = 0, MinWidth = 120 };
        internal readonly TextBox Filter = new TextBox { Watermark = "Filter", MinWidth = 160 };
        internal readonly CheckBox Follow = new CheckBox { Content = "Follow", IsChecked = true };
        internal readonly Button Clear = new Button { Content = "Clear" };
        internal readonly Button Copy = new Button { Content = "Copy all" };
        long shown = -1;

        DebugConsoleWindow(StudioHost host, DebugLog log) : base(host, "Debug Console", PlacementName, 760, 420, refreshMs: 250)
        {
            this.log = log;
            AutomationProperties.SetName(this, "Debug Console");
            AutomationProperties.SetName(Text, "Log");
            AutomationProperties.SetName(Level, "Lowest level shown");
            AutomationProperties.SetName(Filter, "Filter the log");
            ToolTip.SetTip(Follow, "Keep the newest line in view");
            Clear.Click += (s, e) => { log.Clear(); Tick(); };
            Copy.Click += async (s, e) =>
            {
                var clipboard = GetTopLevel(this)?.Clipboard;
                if (clipboard != null) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, Text.Text ?? "");
            };
            Level.SelectionChanged += (s, e) => Redraw();
            Filter.PropertyChanged += (s, e) => { if (e.Property == TextBox.TextProperty) Redraw(); };
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(12, 8), Children = { Label("Show"), Level, Filter, Follow, Clear, Copy } };
            var dock = new DockPanel();
            DockPanel.SetDock(bar, Dock.Top);
            dock.Children.Add(bar);
            dock.Children.Add(Text);
            Content = dock;
            Closed += (s, e) => { if (current == this) current = null; };
        }

        /// <summary>Opens the console (or brings it to the front).</summary>
        public static DebugConsoleWindow Open(StudioHost host, Window owner = null, DebugLog log = null)
        {
            if (current != null)
            {
                current.Activate();
                return current;
            }
            current = new DebugConsoleWindow(host, log ?? DebugLog.Install());
            ShowOver(current, owner);
            return current;
        }

        internal static DebugConsoleWindow Current => current;

        void Redraw()
        {
            shown = -1;
            Tick();
        }

        internal override void Tick()
        {
            long v = log.Version;
            if (v == shown) return;
            shown = v;
            var min = Levels[Math.Clamp(Level.SelectedIndex, 0, Levels.Length - 1)];
            string filter = Filter.Text?.Trim();
            var lines = log.Snapshot(min).Where(l => string.IsNullOrEmpty(filter) || l.Text.Contains(filter, StringComparison.OrdinalIgnoreCase)).Select(DebugLog.Format);
            Text.Text = string.Join("\n", lines);
            if (Follow.IsChecked == true) Text.CaretIndex = Text.Text.Length;
        }
    }
}
