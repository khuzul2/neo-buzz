// Fork (docs/specs/ux-overhaul.md P6b, §9): View ▸ CPU Monitor, after BuzzGUI.MachineView's CPUMonitorWindow: every machine's share of the
// audio engine's time (a bar and a percentage), its CPU cycles per sample and the longest it held the engine's lock; below, the engine's
// total and what the machines don't account for. Refreshed four times a second from the engine's performance snapshot (the same
// numbers as the status line's CPU); the snapshot is taken on the UI thread, the audio thread only counts.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Shell.Design;

namespace ReBuzz.Studio.Shell.Tools
{
    /// <summary>The arithmetic of the monitor, apart from the window and the engine (tests feed it numbers).</summary>
    internal sealed class CpuMonitorModel
    {
        internal sealed record Entry(object Key, string Name, MachinePerformanceData Data);

        internal sealed class Row
        {
            public object Key;
            public string Name;
            public double Cpu;
            public long CyclesPerSample;
            public long MaxLockTime;
        }

        BuzzPerformanceData last;
        readonly Dictionary<object, MachinePerformanceData> before = new Dictionary<object, MachinePerformanceData>();
        readonly Dictionary<object, Row> rows = new Dictionary<object, Row>();

        public double Total { get; private set; }
        public double Overhead { get; private set; }
        public bool HasReading { get; private set; }

        /// <summary>The machines, by name.</summary>
        public IReadOnlyList<Row> Rows { get; private set; } = Array.Empty<Row>();

        /// <summary>One reading: the engine's counters and each machine's, as the engine's last snapshot has them.</summary>
        public void Update(BuzzPerformanceData now, IReadOnlyList<Entry> machines)
        {
            long dt = last == null ? 0 : now.PerformanceCount - last.PerformanceCount;
            var live = new HashSet<object>();
            foreach (var e in machines)
            {
                live.Add(e.Key);
                if (!rows.TryGetValue(e.Key, out var row)) rows[e.Key] = row = new Row { Key = e.Key };
                row.Name = e.Name;
                if (before.TryGetValue(e.Key, out var prev) && e.Data != null)
                {
                    if (dt > 0) row.Cpu = 100.0 * (e.Data.PerformanceCount - prev.PerformanceCount) / dt;
                    long ds = e.Data.SampleCount - prev.SampleCount;
                    if (ds > 0) row.CyclesPerSample = (e.Data.CycleCount - prev.CycleCount) / ds;
                    if (prev.MaxEngineLockTime > row.MaxLockTime) row.MaxLockTime = prev.MaxEngineLockTime;
                }
                if (e.Data != null) before[e.Key] = e.Data;
            }
            foreach (var gone in rows.Keys.Where(k => !live.Contains(k)).ToList())
            {
                rows.Remove(gone);
                before.Remove(gone);
            }
            Rows = machines.Select(e => rows[e.Key]).OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList();
            if (dt > 0)
            {
                Total = 100.0 * (now.EnginePerformanceCount - last.EnginePerformanceCount) / dt;
                Overhead = Total - Rows.Sum(r => r.Cpu);
                HasReading = true;
            }
            last = now;
        }

        /// <summary>Clears the longest lock times (the window's "Reset" button).</summary>
        public void ResetLocks()
        {
            foreach (var r in rows.Values) r.MaxLockTime = 0;
        }

        /// <summary>One reading from the engine: takes its snapshot first.</summary>
        public void Update(ReBuzzCore core)
        {
            core.TakePerformanceDataSnapshot();
            var machines = core.SongCore.MachinesList.Select(m => new Entry(m, m.Name, m.PerformanceData)).ToList();
            Update(core.PerformanceData, machines);
        }
    }

    internal sealed class CpuMonitorWindow : ToolWindow
    {
        public const string PlacementName = "CpuMonitor";
        static CpuMonitorWindow current;

        readonly CpuMonitorModel model = new CpuMonitorModel();
        readonly Grid grid = new Grid();
        readonly TextBlock total = Label("Total: -", size: StudioTokens.FontHeading);
        readonly TextBlock overhead = Label("Overhead: -", secondary: true);
        readonly Dictionary<object, RowControls> controls = new Dictionary<object, RowControls>();
        string shown = "";

        sealed class RowControls
        {
            public TextBlock Name, Cpu, Cycles, Lock;
            public Border Fill;
        }

        CpuMonitorWindow(StudioHost host) : base(host, "CPU Monitor", PlacementName, 520, 420, refreshMs: 250)
        {
            AutomationProperties.SetName(this, "CPU Monitor");
            grid.ColumnDefinitions.Add(new ColumnDefinition(150, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(60, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(90, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(90, GridUnitType.Pixel));
            var reset = new Button { Content = "Reset lock times" };
            reset.Click += (s, e) => { model.ResetLocks(); Tick(); };
            AutomationProperties.SetName(reset, "Reset lock times");
            ToolTip.SetTip(reset, "Start counting the longest time each machine held the engine's lock from zero");
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(12, 8), Children = { total, overhead, reset } };
            var header = Header();
            var dock = new DockPanel();
            DockPanel.SetDock(footer, Dock.Bottom);
            DockPanel.SetDock(header, Dock.Top);
            dock.Children.Add(footer);
            dock.Children.Add(header);
            dock.Children.Add(new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
            Content = dock;
            Closed += (s, e) => { if (current == this) current = null; };
        }

        Grid Header()
        {
            var h = new Grid { Margin = new Thickness(12, 8, 12, 4) };
            foreach (var d in grid.ColumnDefinitions) h.ColumnDefinitions.Add(new ColumnDefinition(d.Width));
            string[] names = { "Machine", "Load", "CPU", "Cycles/sample", "Max lock" };
            for (int i = 0; i < names.Length; i++)
            {
                var t = Label(names[i], secondary: true);
                Grid.SetColumn(t, i);
                h.Children.Add(t);
            }
            return h;
        }

        /// <summary>Opens the monitor (or brings the open one to the front).</summary>
        public static CpuMonitorWindow Open(StudioHost host, Window owner = null)
        {
            if (current != null)
            {
                current.Activate();
                return current;
            }
            current = new CpuMonitorWindow(host);
            ShowOver(current, owner);
            return current;
        }

        internal static CpuMonitorWindow Current => current;

        /// <summary>The rows as text, "name: 12.3%" each, for tests.</summary>
        internal string RowsText => string.Join("\n", model.Rows.Select(r => $"{r.Name}: {r.Cpu.ToString("0.0", CultureInfo.InvariantCulture)}%"));

        internal string TotalText => total.Text;
        internal CpuMonitorModel Model => model;

        internal override void Tick()
        {
            var core = Core;
            if (core?.SongCore == null) return;
            model.Update(core);
            Show(model);
        }

        /// <summary>Draws a reading (after <see cref="CpuMonitorModel.Update(ReBuzzCore)"/>).</summary>
        internal void Show(CpuMonitorModel m)
        {
            string key = string.Join("\n", m.Rows.Select(r => r.Name));
            if (key != shown || controls.Count != m.Rows.Count)
            {
                shown = key;
                Rebuild(m);
            }
            foreach (var r in m.Rows)
            {
                var c = controls[r.Key];
                c.Cpu.Text = r.Cpu.ToString("0.0", CultureInfo.InvariantCulture) + "%";
                c.Cycles.Text = r.CyclesPerSample.ToString(CultureInfo.InvariantCulture);
                c.Lock.Text = r.MaxLockTime.ToString(CultureInfo.InvariantCulture);
                c.Fill.Width = Math.Clamp(r.Cpu, 0, 100) / 100.0 * Math.Max(0, c.Fill.Parent is Border b && b.Bounds.Width > 0 ? b.Bounds.Width : 100);
                c.Fill.Background = r.Cpu >= 50 ? StudioTokens.MeterHotBrush : StudioTokens.MeterNormalBrush;
            }
            total.Text = m.HasReading ? "Total: " + m.Total.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "Total: -";
            overhead.Text = m.HasReading ? "Overhead: " + m.Overhead.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "Overhead: -";
        }

        void Rebuild(CpuMonitorModel m)
        {
            grid.Children.Clear();
            grid.RowDefinitions.Clear();
            controls.Clear();
            int i = 0;
            foreach (var r in m.Rows)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var c = new RowControls { Name = Label(r.Name), Cpu = Label(""), Cycles = Label(""), Lock = Label("") };
                c.Fill = new Border { HorizontalAlignment = HorizontalAlignment.Left, Height = 10, CornerRadius = new CornerRadius(2) };
                var track = new Border { Background = StudioTokens.ControlTrackBrush, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 0, 8, 0), Child = c.Fill };
                track.SizeChanged += (s, e) => Show(model);
                AutomationProperties.SetName(c.Name, r.Name);
                var cells = new Avalonia.Controls.Control[] { c.Name, track, c.Cpu, c.Cycles, c.Lock };
                for (int col = 0; col < cells.Length; col++)
                {
                    Grid.SetRow(cells[col], i);
                    Grid.SetColumn(cells[col], col);
                    cells[col].Margin = col == 1 ? cells[col].Margin : new Thickness(12, 2, 0, 2);
                    grid.Children.Add(cells[col]);
                }
                controls[r.Key] = c;
                i++;
            }
        }
    }
}
