// Fork (docs/specs/ux-overhaul.md P4a): the export renders off the UI thread; renders of the Neo instruments repeat within one process;
// one button, menu and dialog style; transitions of 150 ms or less; Tab reaches every control; every icon-only control has a name.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Avalonia.Headless.NUnit;
using Avalonia.Threading;
using NUnit.Framework;
using ReBuzz.Agent.Mcp;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Design;
using ReBuzz.Studio.Shell.Preferences;
using static ReBuzz.Studio.Tests.MachineViewTests;

namespace ReBuzz.Studio.Tests
{
    public class UxP4aTests
    {
        static ReBuzzCore Core => Studio.Host.Core;

        static (bool Error, string Text, JsonObject Data) Try(string tool, JsonObject args = null)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args ?? new JsonObject() },
            };
            var task = Studio.Host.Agent.Host.Server.HandleAsync(message, new McpSession(), _ => Task.CompletedTask, CancellationToken.None);
            Wait(task);
            var r = task.GetAwaiter().GetResult()!;
            if (r["error"] is JsonObject e) return (true, e["message"]!.GetValue<string>(), new JsonObject());
            var result = r["result"]!.AsObject();
            return (result["isError"]?.GetValue<bool>() == true, result["content"]![0]!["text"]!.GetValue<string>(), result["structuredContent"] as JsonObject ?? new JsonObject());
        }

        static void Wait(Task task)
        {
            var until = DateTime.UtcNow.AddSeconds(180);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail("didn't finish in time");
                Thread.Sleep(2);
            }
        }

        internal static JsonObject Call(string tool, JsonObject args = null)
        {
            var r = Try(tool, args);
            Assert.That(r.Error, Is.False, $"{tool}: {r.Text}");
            return r.Data;
        }

        internal static JsonObject Args(params (string Name, JsonNode Value)[] pairs)
        {
            var o = new JsonObject();
            foreach (var (n, v) in pairs) o[n] = v;
            return o;
        }

        /// <summary>A fresh song with one instrument playing a few notes over a few bars.</summary>
        internal static MainWindow SongWith(string gear, int beats = 16)
        {
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            SongFiles.New().Wait();
            var window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
            window.Show();
            Settle();
            Call("machine_add", Args(("gear", gear), ("name", "Voice")));
            Call("pattern_create", Args(("machine", "Voice"), ("name", "P"), ("length_beats", beats)));
            var notes = new JsonArray();
            for (int b = 0; b < beats; b += 2)
            {
                notes.Add(new JsonObject { ["pitch"] = 48 + (b / 2) % 5 * 2, ["start"] = (double)b, ["length"] = 1.5, ["velocity"] = 100 });
                notes.Add(new JsonObject { ["pitch"] = 55 + (b / 2) % 3 * 3, ["start"] = (double)b + 0.5, ["length"] = 1.0, ["velocity"] = 80 });
            }
            Call("notes_write", Args(("machine", "Voice"), ("pattern", "P"), ("notes", notes)));
            Call("arrangement_place", Args(("machine", "Voice"), ("pattern", "P"), ("at", 0)));
            Settle();
            return window;
        }

        internal static void Restore(MainWindow window, string folder)
        {
            ExportSheet.FolderOpener = null;
            ExportSheet.Chooser = null;
            ExportSheet.OnProgress = null;
            Core.Looping = false;
            Dialogs.Responder = (m, c, b) => UserAnswer.No;
            window.Close();
            Dialogs.Responder = null;
            Dispatcher.UIThread.RunJobs();
            if (folder != null && Directory.Exists(folder)) Directory.Delete(folder, true);
        }

        // ------------------------------------------------------------------ renders repeat

        [AvaloniaTest]
        [TestCase("Neo Poly")]
        [TestCase("Neo FM6")]
        [TestCase("Neo Wave")]
        [TestCase("Neo Drums")]
        public void TwoRendersOfTheSameSongInOneProcessAreTheSameBytes(string gear)
        {
            var window = SongWith(gear);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            string first = Call("render", Args(("name", "a")))["file"]!.GetValue<string>();
            string second = Call("render", Args(("name", "b")))["file"]!.GetValue<string>();
            string third = Call("render", Args(("name", "c"), ("bits", 16)))["file"]!.GetValue<string>();
            var a = File.ReadAllBytes(first);
            var b = File.ReadAllBytes(second);
            Assert.That(a.Length, Is.GreaterThan(10000));
            Assert.That(ReBuzz.Agent.Audio.Wav.Read(first).Left.Max(Math.Abs), Is.GreaterThan(0.001f), gear + " made sound");
            Assert.That(b, Is.EqualTo(a), gear + ": the second render is the first's bytes");
            var c = File.ReadAllBytes(Call("render", Args(("name", "d")))["file"]!.GetValue<string>());
            Assert.That(c, Is.EqualTo(a), gear + ": and the fourth");
        }

        // ------------------------------------------------------------------ the export renders off the UI thread

        [AvaloniaTest]
        public void TheExportRendersOnAWorkerThreadWhileTheUiKeepsRunningAndEditsAreBlocked()
        {
            var window = SongWith("Neo Poly", 32);
            string folder = Path.Combine(Path.GetTempPath(), "ux-p4a-export-" + Environment.ProcessId);
            using var restore = Studio.AfterTest(() => Restore(window, folder));
            Directory.CreateDirectory(folder);
            ExportSheet.FolderOpener = _ => { };
            int ui = Environment.CurrentManagedThreadId;
            var threads = new System.Collections.Concurrent.ConcurrentBag<int>();
            var voice = Core.SongCore.Machines.First(m => m.Name == "Voice");
            Action<float[], bool, BuzzGUI.Interfaces.SongTime> tap = (s, st, t) => threads.Add(Environment.CurrentManagedThreadId);
            voice.Outputs[0].Tap += tap;
            var sheet = new ExportSheet(Core);
            sheet.Show(window);
            Settle();
            sheet.PathBox.Text = Path.Combine(folder, "bg.wav");
            int reports = 0, pumps = 0;
            bool blockedDuring = false, flagDuring = false;
            ExportSheet.OnProgress = _ =>
            {
                reports++;
                blockedDuring = !window.IsEnabled;
                flagDuring = ExportSheet.Rendering;
            };
            var run = sheet.Run();
            while (!run.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                pumps++;
                Thread.Sleep(2);
                Assert.That(pumps, Is.LessThan(90000), "the export finished");
            }
            voice.Outputs[0].Tap -= tap;
            Assert.That(run.Result, Is.True, sheet.Status.Text);
            Assert.That(threads, Is.Not.Empty, "the tap heard the render");
            Assert.That(threads, Does.Not.Contain(ui), "the render ran on no UI thread");
            Assert.That(pumps, Is.GreaterThan(10), "the UI thread kept pumping while it rendered");
            Assert.That(reports, Is.GreaterThanOrEqualTo(1), "progress came to the sheet, on the UI thread");
            Assert.That(blockedDuring, Is.True, "the main window took no input while it rendered");
            Assert.That(flagDuring, Is.True, "the autosave and snapshot timers know");
            Assert.That(window.IsEnabled, Is.True, "and takes input again");
            Assert.That(ExportSheet.Rendering, Is.False);
            Assert.That(Core.Playing, Is.False);
            // The bytes are those of the tool's own export (the same render, off the UI thread or on it).
            var args = sheet.Arguments();
            args["path"] = Path.Combine(folder, "tool.wav");
            Call("export_audio", args);
            Assert.That(File.ReadAllBytes(Path.Combine(folder, "bg.wav")), Is.EqualTo(File.ReadAllBytes(Path.Combine(folder, "tool.wav"))));
        }

        // ------------------------------------------------------------------ the audits: every surface

        /// <summary>One screen of the studio: its name and the root whose controls are audited.</summary>
        internal sealed record Surface(string Name, Avalonia.Controls.Control Root);

        internal static IEnumerable<Surface> Surfaces(MainWindow window, List<Avalonia.Controls.Window> opened)
        {
            window.Width = 1500; window.Height = 950;
            foreach (var view in new[] { "Machines", "Arrangement", "Session", "Pattern", "Song Notes" })
            {
                window.ShowView(view);
                Settle();
                yield return new Surface("main/" + view, window);
            }
            window.ShowView("Machines");
            Settle();
            window.Detail.ShowDevice();
            Settle();
            yield return new Surface("main/device", window);
            var host = Studio.Host;
            var prefs = PreferencesWindow.Open(host);
            opened.Add(prefs);
            foreach (var page in prefs.Pages.Select(p => p.Title).ToList())
            {
                prefs.ShowPage(page);
                Settle();
                Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
                Settle();
                yield return new Surface("prefs/" + page, prefs);
            }
            var about = AboutWindow.Open();
            opened.Add(about);
            Settle();
            yield return new Surface("about", about);
            var sheet = new ExportSheet(Core);
            opened.Add(sheet);
            sheet.Show(window);
            Settle();
            yield return new Surface("export", sheet);
            foreach (var (name, buttons, choices) in new (string, UserButtons, DialogChoices)[]
            {
                ("ok", UserButtons.Ok, null), ("ok-cancel", UserButtons.OkCancel, null), ("yes-no", UserButtons.YesNo, null),
                ("yes-no-cancel", UserButtons.YesNoCancel, DialogChoices.SaveChanges), ("turn-off", UserButtons.YesNo, DialogChoices.TurnOff),
            })
            {
                var ask = Dialogs.Build("Is this a question?", "Question", buttons, choices, OperatingSystem.IsMacOS(), _ => { });
                opened.Add(ask);
                ask.Show(window);
                Settle();
                yield return new Surface("dialog/" + name, ask);
            }
            Call("machine_add", Args(("gear", "Pedal Gain"), ("name", "Plain")));
            Core.UiHost.OpenParameterWindow(Core.SongCore.MachinesList.First(m => m.Name == "Plain"), null);
            var parameters = ReBuzz.Studio.Platform.StudioMachineUiHost.ParameterWindowOf(Core.SongCore.MachinesList.First(m => m.Name == "Plain"));
            if (parameters != null)
            {
                opened.Add(parameters);
                Settle();
                yield return new Surface("parameters", parameters);
            }
        }

        static IEnumerable<Avalonia.Controls.Control> Visible(Avalonia.Controls.Control root) =>
            Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(root).OfType<Avalonia.Controls.Control>()
                .Where(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0);

        static string Describe(Avalonia.Controls.Control c)
        {
            string content = c is Avalonia.Controls.ContentControl cc ? (cc.Content is string str ? "'" + str + "'" : cc.Content?.GetType().Name ?? "-") : "";
            return $"{c.GetType().Name} {content} {c.Bounds.Width:0}x{c.Bounds.Height:0} [{string.Join(",", c.Classes)}]";
        }

        /// <summary>Runs <paramref name="check"/> on every surface once and fails with the list of what it said.</summary>
        static void EverySurface(Func<Surface, IEnumerable<string>> check, string rule)
        {
            var window = SongWith("Neo Poly", 8);
            var opened = new List<Avalonia.Controls.Window>();
            using var restore = Studio.AfterTest(() =>
            {
                foreach (var w in opened) { try { w.Close(); } catch (Exception) { } }
                Restore(window, null);
            });
            Call("machine_add", Args(("gear", "Neo Drums"), ("name", "Kit")));
            Call("machine_add", Args(("gear", "Neo Delay"), ("name", "Echo")));
            Settle();
            var problems = new Dictionary<string, List<string>>();       // a problem once, with the surfaces it is on
            var names = new List<string>();
            foreach (var surface in Surfaces(window, opened))
                foreach (var p in WithName(surface, names, check))
                {
                    if (!problems.TryGetValue(p, out var where)) problems[p] = where = new List<string>();
                    where.Add(surface.Name);
                }
            Assert.That(names.Count, Is.GreaterThanOrEqualTo(18), "the audit saw the main views, Preferences' pages, About, the export sheet, the dialogs and a parameter window: " + string.Join(", ", names));
            var lines = problems.Select(kv => kv.Key + "   @ " + string.Join(", ", kv.Value.Take(3)) + (kv.Value.Count > 3 ? " +" + (kv.Value.Count - 3) : "")).ToList();
            Assert.That(lines, Is.Empty, rule + "\n  " + string.Join("\n  ", lines.Take(80)));
        }

        static IEnumerable<string> WithName(Surface surface, List<string> names, Func<Surface, IEnumerable<string>> check)
        {
            names.Add(surface.Name);
            return check(surface).Distinct();
        }

        // ------------------------------------------------------------------ one button style

        [AvaloniaTest]
        public void EveryButtonOnEverySurfaceIsOneOfTheStylesFourSizesAndNoneSetsItsOwn()
        {
            EverySurface(surface => Visible(surface.Root).Where(c => c.GetType() == typeof(Avalonia.Controls.Button) || c.GetType() == typeof(Avalonia.Controls.Primitives.ToggleButton)).Cast<Avalonia.Controls.Primitives.TemplatedControl>().SelectMany(b =>
            {
                var problems = new List<string>();
                bool icon = b.Classes.Contains("icon"), large = b.Classes.Contains("large");
                var padding = icon ? StudioStyles.IconPadding : large ? StudioStyles.LargePadding : StudioStyles.ButtonPadding;
                double font = icon ? StudioStyles.IconFontSize : StudioTokens.FontBody;
                if (b.Padding != padding) problems.Add($"padding {b.Padding} (the style's {padding}): {Describe(b)}");
                if (b.FontSize != font) problems.Add($"font {b.FontSize} (the style's {font}): {Describe(b)}");
                if (b.CornerRadius != new Avalonia.CornerRadius(StudioStyles.CornerRadius)) problems.Add($"radius {b.CornerRadius}: {Describe(b)}");
                double minHeight = large ? StudioStyles.LargeHeight : StudioStyles.ControlHeight;
                if (b.Bounds.Height < minHeight - 0.5) problems.Add($"{b.Bounds.Height:0} pt high, under {minHeight}: {Describe(b)}");
                if (b.Bounds.Height > StudioStyles.LargeHeight + 8) problems.Add($"{b.Bounds.Height:0} pt high, a button is never that tall: {Describe(b)}");
                if (b.Classes.Contains("dialog") && b.Bounds.Width < StudioStyles.DialogButtonWidth - 0.5) problems.Add($"a dialog button {b.Bounds.Width:0} wide: {Describe(b)}");
                return problems;
            }), "one button style (spec 3.6): 24 pt, 10x3 padding, 13 pt; icon, large, ghost and dialog change one thing each");
        }

        // ------------------------------------------------------------------ one dialog style

        [AvaloniaTest]
        public void EveryDialogIsMadeTheSameWay()
        {
            EverySurface(surface =>
            {
                var problems = new List<string>();
                if (surface.Root is not Avalonia.Controls.Window w || !(surface.Name.StartsWith("dialog/") || surface.Name == "export")) return problems;
                if (w.CanResize) problems.Add("it can be resized");
                if (w.ShowInTaskbar) problems.Add("it is in the taskbar");
                if (w.SizeToContent != Avalonia.Controls.SizeToContent.WidthAndHeight) problems.Add("it is not sized by its content");
                if (w.Content is not Avalonia.Controls.StackPanel body || body.Margin != new Avalonia.Thickness(StudioDialog.Margin) || body.Spacing != StudioDialog.Spacing)
                    problems.Add("its content is not StudioDialog.Body (20 pt margin, 12 pt spacing)");
                var buttons = Visible(w).OfType<Avalonia.Controls.Button>().Where(b => b.Classes.Contains("dialog")).ToList();
                if (buttons.Count == 0) problems.Add("no dialog buttons");
                if (buttons.Count(b => b.IsDefault) != 1) problems.Add("not exactly one default button");
                var defaults = buttons.Where(b => b.IsDefault).ToList();
                if (defaults.Any(b => !b.Classes.Contains("accent"))) problems.Add("the default button is not accent");
                if (buttons.Any(b => !b.IsDefault && b.Classes.Contains("accent"))) problems.Add("a button that is not the default is accent");
                if (!buttons.Any(b => b.IsCancel)) problems.Add("nothing answers Escape");
                var row = buttons.Select(b => b.Parent).OfType<Avalonia.Controls.StackPanel>().Distinct().ToList();
                if (row.Count != 1) problems.Add("the buttons are not in one row");
                else if (row[0].HorizontalAlignment != Avalonia.Layout.HorizontalAlignment.Right || row[0].Spacing != StudioDialog.ButtonSpacing) problems.Add("the row is not at the right with 8 pt between");
                else if (buttons.Count > 1)
                {
                    var order = row[0].Children.OfType<Avalonia.Controls.Button>().ToList();
                    bool mac = OperatingSystem.IsMacOS();
                    if (mac && !order.Last().IsDefault) problems.Add("on a Mac the default is the rightmost");
                    if (!mac && !order.First().IsDefault) problems.Add("the default is the first button");
                }
                return problems;
            }, "one dialog style");
        }

        // ------------------------------------------------------------------ one menu style

        [AvaloniaTest]
        public void EveryMenuRowAndEveryPopupIsOneStyle()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Settle();
            window.ShowView("Machines");
            Settle();
            var problems = new List<string>();
            double row = OperatingSystem.IsMacOS() ? 22 : StudioStyles.MenuRow;
            void Item(Avalonia.Controls.MenuItem mi, string where)
            {
                if (mi.FontSize != StudioTokens.FontBody) problems.Add($"{where}: '{mi.Header}' font {mi.FontSize}");
                if (mi.MinHeight != row) problems.Add($"{where}: '{mi.Header}' row {mi.MinHeight}");
                if (mi.IsEffectivelyVisible && mi.Bounds.Height > 0 && mi.Bounds.Height < row - 0.5) problems.Add($"{where}: '{mi.Header}' is {mi.Bounds.Height:0} pt high");
            }
            // The menu bar (not on a Mac, where the menus are the system's).
            var bar = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Menu>().FirstOrDefault();
            if (bar != null)
                foreach (var top in bar.Items.OfType<Avalonia.Controls.MenuItem>())
                {
                    top.IsSubMenuOpen = true;
                    Settle();
                    Item(top, "menu bar");
                    foreach (var mi in top.Items.OfType<Avalonia.Controls.MenuItem>()) Item(mi, "menu bar/" + top.Header);
                    top.IsSubMenuOpen = false;
                }
            // The Machine View's own menus.
            var view = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<ReBuzz.Studio.Views.Machines.MachineView>().First();
            var machine = Core.SongCore.Machines.First(m => m.Name == "Voice");
            foreach (var (name, menu) in new (string, Avalonia.Controls.ContextMenu)[]
            {
                ("canvas menu", view.Menus.ForCanvas(new Avalonia.Point(100, 100))),
                ("machine menu", view.Menus.ForMachine(machine, new[] { machine })),
            })
            {
                menu.Open(view);
                Settle();
                if (menu.FontSize != StudioTokens.FontBody) problems.Add($"{name}: font {menu.FontSize}");
                if (menu.Padding != new Avalonia.Thickness(4)) problems.Add($"{name}: padding {menu.Padding}");
                if (menu.CornerRadius != new Avalonia.CornerRadius(6)) problems.Add($"{name}: radius {menu.CornerRadius}");
                foreach (var mi in menu.Items.OfType<Avalonia.Controls.MenuItem>()) Item(mi, name);
                menu.Close();
            }
            Assert.That(problems, Is.Empty, string.Join("\n  ", problems.Distinct().Take(50)));
        }

        // ------------------------------------------------------------------ transitions

        [AvaloniaTest]
        public void NoTransitionIsLongerThan150MsAndNoneRunsOnTheAudioPath()
        {
            int seen = 0;
            EverySurface(surface =>
            {
                var problems = new List<string>();
                foreach (var c in Visible(surface.Root))
                {
                    if (c.Transitions == null) continue;
                    foreach (var t in c.Transitions)
                    {
                        seen++;
                        var d = (TimeSpan)t.GetType().GetProperty("Duration")!.GetValue(t)!;
                        if (d > Motion.Max) problems.Add($"{(int)d.TotalMilliseconds} ms {t.Property?.Name} on {c.GetType().Name} [{string.Join(",", c.Classes)}]");
                        if (c.Classes.Contains("instant")) problems.Add($"a transition on an instant control: {Describe(c)}");
                    }
                }
                // The audio path's controls answer at once.
                if (surface.Name.StartsWith("main/"))
                    foreach (var b in Visible(surface.Root).OfType<Avalonia.Controls.Primitives.ToggleButton>().Concat<Avalonia.Controls.Button>(Visible(surface.Root).OfType<Avalonia.Controls.Button>()).Distinct())
                    {
                        string name = Avalonia.Automation.AutomationProperties.GetName(b) ?? "";
                        if (name is "Play" or "Stop" or "Record" or "Go to start" or "Loop" or "Metronome" or "Tap tempo" && !b.Classes.Contains("instant"))
                            problems.Add($"the transport's {name} is not instant");
                    }
                return problems;
            }, "transitions are 150 ms or less, and none on the audio-critical paths");
            Assert.That(seen, Is.GreaterThan(20), "the check looked at the transitions of the buttons and the other controls");
        }

        // ------------------------------------------------------------------ names for icons

        static bool IconOnly(Avalonia.Controls.ContentControl b)
        {
            static bool Words(string text) => text != null && text.Count(char.IsLetterOrDigit) >= 2;
            if (b.Content is string text) return !Words(text);
            if (b.Content is Avalonia.Controls.TextBlock tb) return !Words(tb.Text);
            if (b.Content is Avalonia.Controls.Panel panel)
                return !Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(panel).OfType<Avalonia.Controls.TextBlock>().Any(t => Words(t.Text));
            return true;       // a shape, an icon, an image
        }

        [AvaloniaTest]
        public void EveryIconOnlyControlHasAName()
        {
            EverySurface(surface => Visible(surface.Root)
                .OfType<Avalonia.Controls.Button>()
                .Where(b => b.GetType() != typeof(Avalonia.Controls.RepeatButton) && b.Name != "PART_PopupButton")
                .Where(b => b.GetType() == typeof(Avalonia.Controls.Button) || b is Avalonia.Controls.Primitives.ToggleButton)
                .Where(b => IconOnly(b) && string.IsNullOrWhiteSpace(Avalonia.Automation.AutomationProperties.GetName(b)))
                .Select(b => "no AutomationProperties.Name: " + Describe(b)),
                "every icon-only control has an accessibility name");
        }

        // ------------------------------------------------------------------ focus order and keyboard reach

        /// <summary>The controls that keep Tab for themselves (Buzz's tracker: Tab is the next column; a text box that takes tabs), each with the way out.</summary>
        static readonly string[] TabHolders = { "PatternBody", "the window", "SongHistoryTextBox" };

        static bool IsInteractive(Avalonia.Controls.Control c) =>
            c.IsEnabled && c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0
            && (c.GetType() == typeof(Avalonia.Controls.Button) || c is Avalonia.Controls.Primitives.ToggleButton || c is Avalonia.Controls.ComboBox
                || c is Avalonia.Controls.TextBox || c is Avalonia.Controls.CheckBox || c is Avalonia.Controls.RadioButton || c is Avalonia.Controls.Slider
                || c is Avalonia.Controls.NumericUpDown || c is Avalonia.Controls.Expander || c.GetType().Name == "Segment")
            && !(c is Avalonia.Controls.Button && c.Name != null && c.Name.StartsWith("PART_"))
            && !Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(c).Any(a => a is Avalonia.Controls.Primitives.ScrollBar || a is Avalonia.Controls.Slider && c is not Avalonia.Controls.Slider
                || a is Avalonia.Controls.NumericUpDown && c is not Avalonia.Controls.NumericUpDown || a is Avalonia.Controls.ComboBox);

        /// <summary>
        /// The stops of a window in the order the Tab key visits them (real key presses, from the first stop on, until the order comes
        /// round). A control that keeps Tab for itself (the tracker's cells move the cursor with it) holds the focus: the walk notes it
        /// in <paramref name="own"/> and goes on from the next stop in the tree, as a person leaves it by the mouse or the view keys.
        /// </summary>
        static List<Avalonia.Input.IInputElement> TabOrder(Avalonia.Controls.Window window, List<string> own = null)
        {
            var order = new List<Avalonia.Input.IInputElement>();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            var focus = window.FocusManager!;
            window.Focus();
            var stops = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Control>()
                .Where(c => c.Focusable && c.IsEnabled && c.IsEffectivelyVisible && Avalonia.Input.KeyboardNavigation.GetIsTabStop(c) && c.Bounds.Width > 0).ToList();
            Avalonia.Input.IInputElement before = window;
            for (int i = 0; i < 400; i++)
            {
                Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
                Avalonia.Headless.HeadlessWindowExtensions.KeyReleaseQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
                Dispatcher.UIThread.RunJobs();
                var at = focus.GetFocusedElement() ?? window;
                if (ReferenceEquals(at, before))
                {
                    // Held: the control (or the window's editor) took Tab. On to the next stop after it in the tree that is not inside it.
                    var held = before as Avalonia.Controls.Control;
                    own?.Add(held == null || held == window ? "the window" : Describe(held));
                    int k = held == null || held == window ? -1 : stops.IndexOf(held);
                    var next = stops.Skip(k + 1).FirstOrDefault(c => (held == null || held == window || !Avalonia.VisualTree.VisualExtensions.GetVisualAncestors(c).Contains(held)) && !order.Contains(c));
                    if (next == null) break;
                    next.Focus(Avalonia.Input.NavigationMethod.Tab);
                    Dispatcher.UIThread.RunJobs();
                    at = focus.GetFocusedElement() ?? window;
                    if (ReferenceEquals(at, window)) break;
                }
                if (order.Contains(at)) break;
                order.Add(at);
                before = at;
            }
            window.Focus();
            // The walk is a ring that starts wherever the focus was: begin it at the first control on screen, as the window is read.
            if (order.Count > 1)
            {
                (double, double) Rank(Avalonia.Input.IInputElement e) =>
                    e is Avalonia.Visual v && Avalonia.VisualExtensions.TranslatePoint(v, new Avalonia.Point(0, 0), window) is { } p && p.Y >= 0 && p.Y < window.Bounds.Height
                        ? (Math.Round(p.Y / 8), p.X) : (double.MaxValue, 0);
                int start = 0;
                int tab = order.FindIndex(e => e is Avalonia.Controls.TabItem);
                if (tab >= 0) start = tab; // a window with a tab list reads it first
                else for (int i = 1; i < order.Count; i++) if (Rank(order[i]).CompareTo(Rank(order[start])) < 0) start = i;
                order = order.Skip(start).Concat(order.Take(start)).ToList();
            }
            return order;
        }

        [AvaloniaTest]
        public void TabReachesEveryControlOfEveryViewAndVisitsEachPanelInReadingOrder()
        {
            EverySurface(surface =>
            {
                var problems = new List<string>();
                if (surface.Root is not Avalonia.Controls.Window window) return problems;
                var own = new List<string>();
                var order = TabOrder(window, own);
                bool alone = Visible(window).Count(IsInteractive) <= 1;
                foreach (var held in own) if (!alone && !TabHolders.Any(h => held.StartsWith(h))) problems.Add("a control keeps Tab for itself and is not on the list: " + held);
                var index = new Dictionary<Avalonia.Visual, int>();
                for (int i = 0; i < order.Count; i++) if (order[i] is Avalonia.Visual v) index[v] = i;
                // Reach: every control a hand can use is a tab stop, or has one inside it (a number box's text).
                static bool Stop(Avalonia.Controls.Control c) => c.Focusable && c.IsEnabled && Avalonia.Input.KeyboardNavigation.GetIsTabStop(c);
                foreach (var c in Visible(window).Where(IsInteractive))
                    if (!Stop(c) && !Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(c).OfType<Avalonia.Controls.Control>().Any(Stop))
                        problems.Add("not a tab stop: " + Describe(c) + (c.Focusable ? "" : " (not Focusable)"));
                // Order: within one panel, the children's first stops come in the order the children are seen (rows top to bottom, each row left to right).
                foreach (var panel in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(window).OfType<Avalonia.Controls.Panel>().Where(p => p.IsEffectivelyVisible))
                {
                    var kids = new List<(Avalonia.Controls.Control Child, int First, Avalonia.Rect Box)>();
                    foreach (var child in panel.Children.OfType<Avalonia.Controls.Control>().Where(ch => ch.IsEffectivelyVisible && ch.Bounds.Width > 0))
                    {
                        int first = int.MaxValue;
                        if (index.TryGetValue(child, out int at)) first = at;
                        foreach (var d in Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(child)) if (index.TryGetValue(d, out int k) && k < first) first = k;
                        if (first != int.MaxValue) kids.Add((child, first, child.Bounds));
                    }
                    if (kids.Count < 2 || panel is Avalonia.Controls.Canvas) continue;
                    var visited = kids.OrderBy(k => k.First).ToList();
                    for (int i = 0; i < kids.Count - 1; i++)
                    {
                        var a = visited[i]; var b = visited[i + 1];
                        bool overlap = a.Box.Intersects(b.Box);
                        static double Line(Avalonia.Rect r) => Math.Round((r.Y + r.Height / 2) / 20);
                        bool bBeforeA = Line(b.Box) < Line(a.Box) || Line(b.Box) == Line(a.Box) && b.Box.X < a.Box.X - 1;
                        // A dock panel reads its side panels (top, left, right) first, then the fill: a side panel is a stop before the main area.
                        if (panel is Avalonia.Controls.DockPanel dock && dock.LastChildFill && ReferenceEquals(b.Child, dock.Children.LastOrDefault())
                            && Avalonia.Controls.DockPanel.GetDock(a.Child) != Avalonia.Controls.Dock.Bottom) bBeforeA = false;
                        if (!overlap && bBeforeA)
                        {
                            problems.Add($"Tab goes to {Describe(a.Child)} before {Describe(b.Child)}, which is above or left of it, in {panel.GetType().Name}");
                            break;
                        }
                    }
                }
                return problems;
            }, "Tab reaches every control, and each panel's controls in reading order");
        }

        [AvaloniaTest]
        public void TabAndShiftTabMoveTheFocusOneStopAlongTheTransportAndTheSwitcher()
        {
            var window = SongWith("Neo Poly", 8);
            using var restore = Studio.AfterTest(() => Restore(window, null));
            Settle();
            var order = TabOrder(window);
            Assert.That(order.Count, Is.GreaterThan(10), "a main window has dozens of stops");
            var first = order[0];
            ((Avalonia.Controls.Control)first).Focus(Avalonia.Input.NavigationMethod.Tab);
            Settle();
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(first));
            var next = order[1];
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
            Avalonia.Headless.HeadlessWindowExtensions.KeyReleaseQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.None);
            Settle();
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(next), "Tab goes to the next stop");
            Avalonia.Headless.HeadlessWindowExtensions.KeyPressQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.Shift);
            Avalonia.Headless.HeadlessWindowExtensions.KeyReleaseQwerty(window, Avalonia.Input.PhysicalKey.Tab, Avalonia.Input.RawInputModifiers.Shift);
            Settle();
            Assert.That(window.FocusManager!.GetFocusedElement(), Is.SameAs(first), "Shift+Tab comes back");
        }
    }
}
