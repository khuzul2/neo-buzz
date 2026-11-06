// Fork (docs/specs/mcp-server.md §12.11, U4): `ReBuzz.exe --agent --editor-shots <folder>`, the manifest editor of
// every machine with a curated manifest in Gear, as ReBuzz.exe opens it, for ReBuzzTests on the Windows package run
// (the natives load only in ReBuzz.exe's engine, which the tests run as a process, as AgentHeadlessTests do). The
// engine starts headless as for --agent; each machine is added, opened as a double click opens it (its manifest
// editor, in a window, through WpfMachineUiHost; or, for a machine with its own GUI, "Editor..." does it), drawn to
// <folder>/manifest-editor-<gear>.png, and read back: each control against the agent server's text for its
// parameter, a value typed into the value box against param_set's raw value, safe ranges on their knobs, the response
// plot's kind, the preset bar. editors.json holds what was found; then ReBuzz exits.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Interfaces;
using NeoBuzz.Editor;
using ReBuzz.Agent;
using ReBuzz.Agent.Model;
using ReBuzz.Core;
using ReBuzz.NativeMachine;

namespace ReBuzz.AppViews
{
    internal static class EditorShots
    {
        public const string Switch = "--editor-shots";

        /// <summary>The folder after --editor-shots, or null.</summary>
        public static string Folder(string[] args)
        {
            int i = Array.FindIndex(args, a => string.Equals(a, Switch, StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        public static string Slug(string gear) => gear.ToLowerInvariant().Replace(' ', '-');

        /// <summary>Opens, draws and checks each curated machine's editor; on the UI thread, the engine started.</summary>
        public static void Run(ReBuzzCore core, string folder, Action<string> log)
        {
            Directory.CreateDirectory(folder);
            var results = new JsonArray();
            var gears = core.MachineDLLs.Values
                .Where(d => d?.Info != null && SongModel.Manifest(d)?.IsCurated == true && ManifestEditorSettings.Available(d))
                .OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
            log($"{gears.Count} machines with curated manifests in Gear");
            foreach (var dll in gears)
            {
                var r = new JsonObject { ["gear"] = dll.Name, ["managed"] = dll.IsManaged };
                MachineCore m = null;
                try
                {
                    var before = SongModel.Machines(core).ToHashSet();
                    core.SongCore.CreateMachine(dll.Name, null, "Face", null, null, null, -1, 0.2f, 0.2f);
                    m = SongModel.Machines(core).FirstOrDefault(x => !before.Contains(x)) ?? throw new InvalidOperationException("not created");
                    Check(core, m, r, folder);
                }
                catch (Exception e)
                {
                    r["error"] = e.GetType().Name + ": " + e.Message;
                }
                finally
                {
                    if (m != null)
                    {
                        try
                        {
                            core.UiHost?.CloseWindows(m);
                            core.UiHost?.ReleaseMachine(m);
                            core.SongCore.DeleteMachines(new IMachine[] { m });
                        }
                        catch (Exception) { }
                    }
                }
                log($"{dll.Name}: " + (r["error"]?.GetValue<string>() ?? $"{r["controls"]} controls, opened by {r["opened_by"]}"));
                results.Add(r);
            }
            File.WriteAllText(Path.Combine(folder, "editors.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        static void Check(ReBuzzCore core, MachineCore m, JsonObject r, string folder)
        {
            // A double click, as people open it: the manifest editor, where the machine has no GUI of its own. A native
            // machine that handles double clicks itself (its own dialog) would wait for someone here: "Editor..." instead.
            r["opens_editor"] = ManifestEditorSettings.OpensEditor(m.DLL);
            bool ownDoubleClick = !m.DLL.IsManaged && m.CMachineEventType.Any(e => e.Type == BEventType.DoubleClickMachine && e.Event_Handler != IntPtr.Zero);
            r["own_double_click"] = ownDoubleClick;
            if (!ownDoubleClick) m.DoubleClick();
            Window window = WpfMachineUiHost.EditorWindowOf(m);
            r["opened_by"] = window != null ? "double click" : "Editor...";
            if (window == null)
            {
                r["double_click_opened"] = WpfMachineUiHost.GuiWindowOf(m) != null ? "its own GUI" : WpfMachineUiHost.ParameterWindowOf(m) != null ? "its parameter window" : "nothing here";
                core.UiHost.CloseWindows(m);
                if (!core.UiHost.OpenEditor(m)) throw new InvalidOperationException("OpenEditor gave no editor");
                window = WpfMachineUiHost.EditorWindowOf(m);
            }
            if (WpfMachineUiHost.ContentOf(window) is not ManifestEditor editor) throw new InvalidOperationException("its editor window holds no manifest editor");
            // The machine keyboard under a generator's editor (docs/specs/studio-mvp.md §3.12), in the picture too.
            r["keyboard"] = WpfMachineUiHost.KeyboardOf(window) is { } keyboard ? keyboard.Player.Route.Input.ToString() : "none";
            window.UpdateLayout();
            editor.UpdateLayout();
            var face = editor.Face;
            r["quality"] = face.Quality;
            r["sections"] = face.Sections.Count;
            r["controls"] = editor.Controls.Count;
            r["response"] = face.Response == null ? "none" : face.Response.Computed ? "computed" : "markers";
            r["envelopes"] = face.Sections.Sum(s => s.Envelopes.Count);
            r["preset_bar"] = Descendants(editor).Any(e => e.GetType().Name == "PresetBar");
            r["presets"] = m.GetPresetNames().Count(n => n != "<default>");
            r["editor_error"] = Descendants(editor).OfType<System.Windows.Controls.TextBlock>().Any(t => (t.Text ?? "").StartsWith("Editor error"));

            // Each control against the agent server's own text for its parameter.
            var server = SongModel.Params(m).Where(p => (p.Parameter.Flags & ParameterFlags.State) != 0).ToList();
            var mismatches = new JsonArray();
            int knobs = 0;
            foreach (var (ap, p) in server.Zip(face.Params))
            {
                if (ap.Name != p.Name) { mismatches.Add($"{ap.Name}: the editor's parameter is {p.Name}"); continue; }
                if (!editor.Controls.TryGetValue(p, out var control)) { mismatches.Add($"{p.Label}: no control"); continue; }
                if (control is Knob knob)
                {
                    knobs++;
                    string text = ap.Info.Describe(ap.Parameter.GetValue(0));
                    if (knob.ValueText != text) mismatches.Add($"{p.Label}: the knob shows \"{knob.ValueText}\", param_get \"{text}\"");
                    if ((p.SafeRange != null) != (knob.Safe != null)) mismatches.Add($"{p.Label}: its safe range {(p.SafeRange != null ? "isn't" : "is")} marked");
                }
            }
            if (server.Count != face.Params.Count) mismatches.Add($"{server.Count} parameters on the server, {face.Params.Count} in the editor");
            r["knobs"] = knobs;
            r["mismatches"] = mismatches;
            r["safe_ranges"] = new JsonArray(face.Params.Where(p => p.SafeRange != null)
                .Select(p => (JsonNode)new JsonObject { ["param"] = p.Label, ["range"] = p.SafeRange.Text, ["marked"] = editor.Controls[p] is Knob k && k.Safe != null }).ToArray());

            // A typed value, as param_set reads it: the middle of the first knob's range, in its own text.
            var typed = face.Params.FirstOrDefault(p => p.Control == FaceControl.Knob && p.Max > p.Min && (p.SafeRange == null || p.SafeRange.Contains((p.Min + p.Max) / 2)));
            if (typed != null)
            {
                var ap = server.First(x => x.Name == typed.Name && x.IsTrack == typed.IsTrack);
                int was = ap.Parameter.GetValue(0);
                string text = typed.Text((typed.Min + typed.Max) / 2);
                int expected = ValueResolver.Resolve(ap.Info, JsonValue.Create(text), was);
                editor.Select(typed, focus: false);
                bool ok = editor.Apply(text);
                int got = ap.Parameter.GetValue(0);
                r["typed"] = new JsonObject { ["param"] = typed.Label, ["text"] = text, ["applied"] = ok, ["raw"] = got, ["param_set_raw"] = expected, ["shows"] = ((Knob)editor.Controls[typed]).ValueText };
                ap.Parameter.SetValue(0, was);
            }

            // The editor drawn, at its size.
            r["png"] = Shoot((FrameworkElement)window.Content, Path.Combine(folder, "manifest-editor-" + Slug(m.DLL.Name) + ".png"), r);
        }

        static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            int n = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        static string Shoot(FrameworkElement element, string path, JsonObject r)
        {
            element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var size = element.DesiredSize;
            element.Arrange(new Rect(size));
            element.UpdateLayout();
            int w = Math.Max(1, (int)Math.Ceiling(size.Width)), h = Math.Max(1, (int)Math.Ceiling(size.Height));
            r["width"] = w;
            r["height"] = h;
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.DrawRectangle(Brushes.Black, null, new Rect(size));
                dc.DrawRectangle(new VisualBrush(element), null, new Rect(size));
            }
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
            return Path.GetFileName(path);
        }
    }
}
