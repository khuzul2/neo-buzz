// Agent server (docs/specs/agent-knowledge.md §5, K3): the studio window, for agents that work beside a person.
// view_show (a view, or a pattern in the Tracker or piano roll), machine_open_editor, select, panel_set (browser,
// inspector, detail) and window_layout. Studio mode only: elsewhere they say so. Every call is visible to the
// person watching, and while the transport records nothing opens a window or takes the keyboard.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ReBuzz.Agent.Mcp;
using static ReBuzz.Agent.Mcp.Schema;

namespace ReBuzz.Agent.App
{
    internal static class ViewTools
    {
        public static readonly string[] Views = { "Arrangement", "Session", "Machines" };
        public static readonly string[] Panels = { "browser", "inspector", "detail" };

        static IViewControl View(IAppServices app)
        {
            if (app.View != null) return app.View;
            string mode = app.Facts().Mode;
            throw new ToolError(mode == "studio"
                ? "Studio only: this window has no view control. The view tools work in the NeoBuzz Live studio window."
                : $"Studio only: {mode} mode has no window to control. The view tools work in the studio, with a window open.");
        }

        static void NotWhileRecording(IViewControl v, string what)
        {
            if (v.Recording)
                throw new ToolError($"The transport is recording, so {what} would take the keyboard or open a window over the person playing. Stop recording first (transport(action=\"stop\")).");
        }

        public static void Register(ToolRegistry tools, IAppServices app)
        {
            tools.Add(new ToolDef
            {
                Name = "view_show",
                Title = "Show a view",
                Description =
                    "Brings a view to the front in the studio window: Arrangement, Session or Machines. With machine and pattern it shows that pattern in the detail panel's Tracker (editor=\"tracker\", default) " +
                    "or piano roll (editor=\"piano_roll\"). With no arguments it just reports where the window is. Studio mode only; refused while recording. The person sees it happen. " +
                    "Example: view_show(view=\"Session\") or view_show(machine=\"Bass\", pattern=\"Verse\", editor=\"piano_roll\").",
                Input = Object(null,
                    Opt("view", String("The main view: Arrangement, Session or Machines")),
                    Opt("machine", String("With pattern: the pattern's machine")),
                    Opt("pattern", String("A pattern to show in the detail panel")),
                    Opt("editor", String("For a pattern: tracker (default) or piano_roll", "tracker", "piano_roll"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var v = View(app);
                    string? view = c.String("view"), machine = c.String("machine"), pattern = c.String("pattern");
                    if ((machine == null) != (pattern == null)) throw new ToolError("Give machine and pattern together.");
                    if (view != null && machine != null) throw new ToolError("Give view, or machine and pattern, not both.");
                    if (view != null) view = Views.FirstOrDefault(x => x.Equals(view, StringComparison.OrdinalIgnoreCase)) ?? (view.Equals("Sequence", StringComparison.OrdinalIgnoreCase) ? "Arrangement" : throw new ToolError($"view: {string.Join(", ", Views)}."));
                    if (machine != null) NotWhileRecording(v, "showing a pattern editor");
                    var s = await app.Engine("view_show", false, () =>
                    {
                        if (view != null) v.ShowView(view);
                        else if (machine != null) v.ShowPattern(machine, pattern!, c.String("editor") == "piano_roll");
                        return v.State();
                    }, c.Cancellation);
                    return ToolResult.Ok(machine != null ? $"Showing {machine}'s {pattern} in the {(c.String("editor") == "piano_roll" ? "piano roll" : "Tracker")}. " + Line(s) : (view != null ? $"Showing {view}. " : "") + Line(s), State(s));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "machine_open_editor",
                Title = "Open a machine's window",
                Description =
                    "Opens a machine's window in the studio: window=\"gui\" (its own editor, default), \"parameters\" (the parameter window) or \"editor\" (the manifest editor, for machines that have one). " +
                    "Studio mode only; refused while recording. The window opens over the studio, so the person sees it. Example: machine_open_editor(machine=\"Bass\", window=\"parameters\").",
                Input = Object(null,
                    Req("machine", String("The machine")),
                    Opt("window", String("gui (default), parameters or editor", "gui", "parameters", "editor"))),
                Output = Object(null, Req("machine", String("The machine")), Req("window", String("The window opened"))),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var v = View(app);
                    NotWhileRecording(v, "opening a machine window");
                    string machine = c.RequiredString("machine"), window = c.String("window") ?? "gui";
                    bool ok = await app.Engine("machine_open_editor", false, () => v.OpenMachineWindow(machine, window), c.Cancellation);
                    if (!ok) throw new ToolError($"{machine} has no {window} window here" + (window == "editor" ? " (it has no manifest editor)" : "") + ". Try window=\"parameters\".");
                    return ToolResult.Ok($"Opened {machine}'s {window} window.", new JsonObject { ["machine"] = machine, ["window"] = window });
                },
            });

            tools.Add(new ToolDef
            {
                Name = "select",
                Title = "Select in the studio",
                Description =
                    "Selects a machine (its track in the Arrangement and Session, its node in Machines), and optionally one of its patterns or a Session scene, so the inspector and the other views follow, as when a person clicks. " +
                    "clear=true selects nothing. Studio mode only. Example: select(machine=\"Bass\", pattern=\"Verse\").",
                Input = Object(null,
                    Opt("machine", String("The machine")),
                    Opt("pattern", String("One of its patterns")),
                    Opt("scene", Integer("A Session scene (0-based)", 0, 4095)),
                    Opt("clear", Boolean("Select nothing"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var v = View(app);
                    bool clear = c.Bool("clear") == true;
                    string? machine = c.String("machine"), pattern = c.String("pattern");
                    if (clear == (machine != null || pattern != null || c.Int("scene") != null)) throw new ToolError("Give a machine (with a pattern or scene), or clear=true.");
                    if (pattern != null && machine == null) throw new ToolError("pattern goes with machine.");
                    var s = await app.Engine("select", false, () =>
                    {
                        v.Select(clear ? null : machine, clear ? null : pattern, c.Int("scene"));
                        return v.State();
                    }, c.Cancellation);
                    return ToolResult.Ok(clear ? "Selection cleared. " + Line(s) : "Selected " + machine + (pattern != null ? $" / {pattern}" : "") + ". " + Line(s), State(s));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "panel_set",
                Title = "Open or size a panel",
                Description =
                    "The studio's side panels: browser (left: machines and patterns), inspector (right: the selection and Song notes) and detail (under the view: the Tracker and piano roll). " +
                    "open shows or hides it; size is the width in pixels (the detail's height); tab picks the inspector's tab (Selection or Song); maximised stretches the detail over the window. " +
                    "Studio mode only. Example: panel_set(panel=\"inspector\", open=true, tab=\"Song\").",
                Input = Object(null,
                    Req("panel", String("Which panel", Panels)),
                    Opt("open", Boolean("Show (true) or hide (false)")),
                    Opt("size", Number("Width in pixels (the detail panel's height); it keeps to the panel's limits", 28, 1200)),
                    Opt("tab", String("Inspector only: Selection or Song", "Selection", "Song")),
                    Opt("maximised", Boolean("Detail only: over the whole window, or docked"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var v = View(app);
                    string panel = Panels.FirstOrDefault(p => p.Equals(c.RequiredString("panel"), StringComparison.OrdinalIgnoreCase)) ?? throw new ToolError($"panel: {string.Join(", ", Panels)}.");
                    if (c.String("tab") != null && panel != "inspector") throw new ToolError("tab is for the inspector.");
                    if (c.Bool("maximised") != null && panel != "detail") throw new ToolError("maximised is for the detail panel.");
                    if (c.Bool("open") == null && c.Number("size") == null && c.String("tab") == null && c.Bool("maximised") == null) throw new ToolError("Give open, size, tab or maximised.");
                    var s = await app.Engine("panel_set", false, () =>
                    {
                        v.Panel(panel, c.Bool("open"), c.Number("size"), c.String("tab"), c.Bool("maximised"));
                        return v.State();
                    }, c.Cancellation);
                    return ToolResult.Ok($"{panel} set. " + Line(s), State(s));
                },
            });

            tools.Add(new ToolDef
            {
                Name = "window_layout",
                Title = "Window layout",
                Description =
                    "Reads the studio window's layout (view, panels, selection, size), and with width, height or maximised changes the window itself. Studio mode only. " +
                    "Example: window_layout(width=1400, height=900).",
                Input = Object(null,
                    Opt("width", Number("Window width in pixels", 360, 10000)),
                    Opt("height", Number("Window height in pixels", 240, 10000)),
                    Opt("maximised", Boolean("Maximise or restore the window"))),
                Output = StateSchema(),
                Annotations = ToolAnnotations.WriteIdempotent,
                Handler = async c =>
                {
                    var v = View(app);
                    var s = await app.Engine("window_layout", false, () =>
                    {
                        if (c.Number("width") != null || c.Number("height") != null || c.Bool("maximised") != null) v.Window(c.Number("width"), c.Number("height"), c.Bool("maximised"));
                        return v.State();
                    }, c.Cancellation);
                    return ToolResult.Ok(Line(s), State(s));
                },
            });
        }

        static string Line(ViewState s) =>
            $"{s.View} view; browser {(s.BrowserOpen ? "open" : "closed")}, inspector {(s.InspectorOpen ? "open (" + s.InspectorTab + ")" : "closed")}, detail {(s.DetailOpen ? (s.DetailMaximised ? "maximised " : "open ") + s.DetailPage : "closed")}" +
            (s.Machine != null ? $"; selected {s.Machine}" + (s.Pattern != null ? $" / {s.Pattern}" : "") : "; nothing selected") + $"; window {s.WindowWidth:0}x{s.WindowHeight:0}{(s.Maximised ? " (maximised)" : "")}.";

        static JsonObject StateSchema() => Object("Where the studio window is",
            Req("view", String("The view shown: Arrangement, Session, Machines (or Song notes)")),
            Req("browser", Object(null, Req("open", Boolean("Shown")), Req("width", Number("Pixels")))),
            Req("inspector", Object(null, Req("open", Boolean("Shown")), Req("width", Number("Pixels")), Req("tab", String("Selection or Song")))),
            Req("detail", Object(null, Req("open", Boolean("Shown")), Req("height", Number("Pixels")), Req("maximised", Boolean("Over the whole window")), Req("page", String("tracker or piano_roll")))),
            Req("selection", Object(null, Opt("machine", String("The selected machine")), Opt("pattern", String("Its selected pattern")), Opt("scene", Integer("The Session scene")))),
            Req("window", Object(null, Req("width", Number("Pixels")), Req("height", Number("Pixels")), Req("maximised", Boolean("Maximised")))),
            Req("recording", Boolean("The transport records: no windows open and no keyboard is taken")));

        static JsonObject State(ViewState s)
        {
            var sel = new JsonObject();
            if (s.Machine != null) sel["machine"] = s.Machine;
            if (s.Pattern != null) sel["pattern"] = s.Pattern;
            if (s.Scene != null) sel["scene"] = s.Scene;
            return new JsonObject
            {
                ["view"] = s.View,
                ["browser"] = new JsonObject { ["open"] = s.BrowserOpen, ["width"] = Math.Round(s.BrowserWidth) },
                ["inspector"] = new JsonObject { ["open"] = s.InspectorOpen, ["width"] = Math.Round(s.InspectorWidth), ["tab"] = s.InspectorTab },
                ["detail"] = new JsonObject { ["open"] = s.DetailOpen, ["height"] = Math.Round(s.DetailHeight), ["maximised"] = s.DetailMaximised, ["page"] = s.DetailPage },
                ["selection"] = sel,
                ["window"] = new JsonObject { ["width"] = Math.Round(s.WindowWidth), ["height"] = Math.Round(s.WindowHeight), ["maximised"] = s.Maximised },
                ["recording"] = s.Recording,
            };
        }
    }
}
