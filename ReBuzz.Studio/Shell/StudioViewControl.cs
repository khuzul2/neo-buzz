// Fork (docs/specs/agent-knowledge.md §5, K3): the studio window for the agent's view tools (IViewControl). It does what a
// person does with the menus and panel buttons, on the UI thread (the agent's calls run there), so the person watching
// sees it. The tools refuse the calls that would take the keyboard or open a window while the transport records.

using System;
using System.Linq;
using Avalonia.Controls;
using BuzzGUI.Interfaces;
using ReBuzz.Agent;
using ReBuzz.Agent.App;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Views;

namespace ReBuzz.Studio.Shell
{
    internal sealed class StudioViewControl : IViewControl
    {
        readonly StudioHost studio;

        public StudioViewControl(StudioHost studio) => this.studio = studio;

        ReBuzzCore Core => studio.Core ?? throw new ToolError("The engine isn't running yet.");

        MainWindow Main => MainWindow.OpenWindows.LastOrDefault() ?? throw new ToolError("The studio window isn't open.");

        public bool Recording => studio.Core is { } c && (c.Recording || c.SessionLayout?.IsSessionRecording == true);

        public ViewState State()
        {
            var w = Main;
            var sel = StudioSelection.For(Core);
            var browser = w.Browser;
            var inspector = w.Inspector;
            var detail = w.Detail;
            return new ViewState(
                View: (w.Tabs.SelectedItem as TabItem)?.Header as string ?? "",
                BrowserOpen: browser?.IsOpen == true,
                BrowserWidth: browser?.PanelWidth ?? 0,
                InspectorOpen: inspector?.IsOpen == true,
                InspectorWidth: inspector?.PanelWidth ?? 0,
                InspectorTab: inspector?.CurrentTab ?? "Selection",
                DetailOpen: detail?.IsOpen == true,
                DetailHeight: detail?.PanelHeight ?? 0,
                DetailMaximised: detail?.IsMaximised == true,
                DetailPage: detail?.IsPianoRoll == true ? "piano_roll" : "tracker",
                Machine: sel.Machine?.Name,
                Pattern: sel.Pattern?.Name,
                Scene: sel.Scene,
                WindowWidth: w.Bounds.Width,
                WindowHeight: w.Bounds.Height,
                Maximised: w.WindowState == WindowState.Maximized,
                Recording: Recording);
        }

        public void ShowView(string view) => Main.ShowView(view);

        IMachine Machine(string name)
        {
            var machines = Core.Song.Machines;
            var m = machines.FirstOrDefault(x => x.Name == name) ?? machines.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (m != null) return m;
            var near = Text.Closest(name, machines.Select(x => x.Name));
            throw new ToolError($"No machine \"{name}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") + " machine_list lists them.");
        }

        IPattern Pattern(IMachine machine, string name)
        {
            var p = machine.Patterns.FirstOrDefault(x => x.Name == name) ?? machine.Patterns.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            if (p != null) return p;
            var near = Text.Closest(name, machine.Patterns.Select(x => x.Name));
            throw new ToolError($"{machine.Name} has no pattern \"{name}\"." + (near.Count > 0 ? $" Did you mean \"{near[0]}\"?" : "") + $" pattern_list(machine=\"{machine.Name}\") lists them.");
        }

        public void ShowPattern(string machine, string pattern, bool pianoRoll)
        {
            var p = Pattern(Machine(machine), pattern);
            var w = Main;
            PatternView.Edit(p);
            if (pianoRoll) w.ShowPianoRoll();
            else w.ShowView("Pattern");
        }

        public bool OpenMachineWindow(string machine, string window)
        {
            var m = Machine(machine);
            var host = Core.UiHost ?? throw new ToolError("This studio has no machine windows.");
            switch (window)
            {
                case "parameters":
                    host.OpenParameterWindow(m, null);
                    return true;
                case "editor":
                    return host.OpenEditor(m);
                default:
                    host.OpenGui(m, null);
                    return true;
            }
        }

        public void Select(string machine, string pattern, int? scene)
        {
            var sel = StudioSelection.For(Core);
            if (machine == null && pattern == null && scene == null)
            {
                sel.Clear(this);
                return;
            }
            var m = Machine(machine ?? throw new ToolError("Give a machine to select."));
            sel.Set(m, pattern != null ? Pattern(m, pattern) : null, this, scene);
        }

        public void Panel(string panel, bool? open, double? size, string tab, bool? maximised)
        {
            var w = Main;
            switch (panel)
            {
                case "browser":
                    if (w.Browser is not { } b) throw new ToolError("The browser isn't ready yet.");
                    if (open == true) b.Open();
                    else if (open == false) b.Close();
                    if (size != null) b.PanelWidth = size.Value;
                    break;
                case "inspector":
                    if (w.Inspector is not { } i) throw new ToolError("The inspector isn't ready yet.");
                    if (tab != null) i.ShowTab(tab);
                    else if (open == true) i.Open();
                    if (open == false) i.Close();
                    if (size != null) i.PanelWidth = size.Value;
                    break;
                default:
                    if (w.Detail is not { } d) throw new ToolError("The detail panel isn't ready yet.");
                    if (open == false) d.Close();
                    else if (maximised == true) d.Show(true);
                    else if (maximised == false) d.Restore();
                    if (open == true && !d.IsOpen) d.Open();
                    if (size != null) d.PanelHeight = size.Value;
                    break;
            }
        }

        public void Window(double? width, double? height, bool? maximised)
        {
            var w = Main;
            if (maximised == true) w.WindowState = WindowState.Maximized;
            else
            {
                if (maximised == false || w.WindowState == WindowState.Maximized) w.WindowState = WindowState.Normal;
                if (width != null) w.Width = width.Value;
                if (height != null) w.Height = height.Value;
            }
        }
    }
}
