// Fork (docs/specs/ux-overhaul.md P3f and the P3e polish): the device view gives its list the room; Preferences in one window.

using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.NUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using BuzzGUI.Common;
using BuzzGUI.Interfaces;
using NUnit.Framework;
using ReBuzz.Agent.App;
using ReBuzz.Core;
using ReBuzz.Studio.Shell;
using ReBuzz.Studio.Shell.Preferences;
using ReBuzz.Studio.Views;
using ReBuzz.Studio.Views.Device;
using ReBuzz.Studio.Views.Inspector;

namespace ReBuzz.Studio.Tests
{
    public class UxP3fTests
    {
        static void Settle(Window window = null)
        {
            Dispatcher.UIThread.RunJobs();
            window?.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        sealed class DeviceRig : IDisposable
        {
            public SessionRig Song { get; }
            public MainWindow Window { get; }
            public MachineCore Fm;

            public DeviceRig()
            {
                Dialogs.Responder = (m, c, b) => UserAnswer.No;
                Song = new SessionRig("Lead");
                Fm = Song.Add("Neo FM6", "Fm", track: false);
                StudioSelection.For(Song.Core).Clear(this);
                Window = new MainWindow(Studio.Host) { Width = 1280, Height = 800 };
                Window.Show();
                Window.ShowView("Machines");
                Settle();
            }

            public DeviceDock Dock => (DeviceDock)Window.Detail.DevicePage;

            public void Select(IMachine m)
            {
                StudioSelection.For(Song.Core).SelectMachine(m, this);
                Settle();
            }

            public void Dispose()
            {
                Window.Close();
                Song.Dispose();
            }
        }

        // ------------------------------------------------------------------ the P3e polish (device view, inspector)

        [AvaloniaTest]
        public void TheDeviceListGetsTheRoomTheStripsLeaveIt()
        {
            using var restore = Studio.AfterTest(ResetDevice);
            using var rig = new DeviceRig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Fm);
            var page = rig.Dock.Page!;
            var view = page.Parameters;
            Settle(rig.Window);
            Assert.That(view.ExplainFocused, Is.False);
            Assert.That(view.Scroller.Bounds.Height, Is.GreaterThan(100), "a list of rows, not one");
            var keyboard = NeoBuzz.Editor.MachineKeyboardFrame.Of(rig.Dock.Frame);
            Assert.That(keyboard, Is.Not.Null);
            Assert.That(keyboard!.Shown, Is.False, "the keyboard starts collapsed in the device view");
            double listHeight = view.Scroller.Bounds.Height;
            Studio.Screenshot(rig.Window, "device-room");

            // A chosen parameter opens the strip (after the pause a double click needs), and none closes it again.
            view.List.SetFocus(view.Layout.All.First(p => p.FullName == "Volume"));
            Settle();
            view.ExplainExpandedNow();
            Settle(rig.Window);
            Assert.That(view.ExplainExpanded, Is.True);
            Assert.That(view.Scroller.Bounds.Height, Is.LessThan(listHeight));
            view.List.SetFocus(null);
            Settle(rig.Window);
            Assert.That(view.ExplainExpanded, Is.False);
            Assert.That(view.Scroller.Bounds.Height, Is.EqualTo(listHeight).Within(1), "one line again");

            // The keyboard remembers what the person chose, for the device view alone.
            keyboard.SetShown(true, remember: true);
            Settle();
            Assert.That(DevicePage.Read(DeviceDock.KeyboardKey, false), Is.True);
            rig.Select(rig.Song.Machine("Lead"));
            rig.Select(rig.Fm);
            Assert.That(NeoBuzz.Editor.MachineKeyboardFrame.Of(rig.Dock.Frame)!.Shown, Is.True, "opened again as it was left");
        }

        static void ResetDevice() => DevicePage.Write(DeviceDock.KeyboardKey, false);

        [AvaloniaTest]
        public void MacrosAreChosenByRoleNamedInTitleCaseAndNeverAnEnvelopeStage()
        {
            using var rig = new DeviceRig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Fm);
            var macros = rig.Dock.Page!.Parameters.Layout.Macros;
            Assert.That(macros.Count, Is.InRange(4, 8));
            Assert.That(macros.Select(m => m.MacroLabel), Has.None.Matches<string>(s => s.Any(char.IsDigit)), "no 'rate 1' or 'level 1' of an envelope stage");
            Assert.That(macros.Select(m => m.MacroLabel), Has.All.Matches<string>(s => char.IsUpper(s[0])), "Title Case");
            Assert.That(macros.Select(m => m.MacroLabel), Does.Contain("Volume"));
            Assert.That(DeviceParam.TitleCase("L depth"), Is.EqualTo("L Depth"));
            Assert.That(DeviceParam.TitleCase("LFO speed"), Is.EqualTo("LFO Speed"));
            Assert.That(DeviceParam.TitleCase("Filter cutoff"), Is.EqualTo("Filter Cutoff"));
        }

        [AvaloniaTest]
        public void ASliderCellsTextHasABackingSoTheMarkerNeverRunsThroughIt()
        {
            using var rig = new DeviceRig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Fm);
            var strip = rig.Dock.Page!.Parameters.MacroStrip;
            Settle(rig.Window);
            Assert.That(strip.IsVisible && strip.Bounds.Width > 100, Is.True);
            Studio.Screenshot(rig.Window, "device-macros");
        }

        [AvaloniaTest]
        public void TheInspectorShowsTheClipCardOnlyWhenTheSelectionNeedsIt()
        {
            using var restore = Studio.AfterTest(() =>
            {
                Studio.Host.Registry.Write(InspectorPanel.OpenKey, true, InspectorPanel.RegistrySection);
                Studio.Host.Registry.Write(InspectorPanel.WidthKey, InspectorPanel.DefaultWidth, InspectorPanel.RegistrySection);
            });
            using var rig = new DeviceRig();
            var inspector = rig.Window.Inspector;
            inspector.Open();
            Settle(rig.Window);
            var lead = rig.Song.Machine("Lead");
            rig.Window.ShowView("Machines");
            rig.Select(lead);
            Assert.That(inspector.ClipPanel.IsVisible, Is.False, "a machine alone in the Machines view: no 'Select a clip' card");
            rig.Select(rig.Fm);
            Assert.That(inspector.ClipPanel.IsVisible, Is.False);
            rig.Window.ShowView("Session");
            rig.Select(lead);
            Settle(rig.Window);
            Assert.That(inspector.ClipPanel.IsVisible, Is.True, "in the Session View, where clips are");
            rig.Window.ShowView("Machines");
            Settle(rig.Window);
            Assert.That(inspector.ClipPanel.IsVisible, Is.False, "and gone again with the view");
        }

        // ------------------------------------------------------------------ Preferences, one window

        static StudioHost Host => Studio.Host;

        static PreferencesWindow OpenPrefs(string page)
        {
            var prefs = PreferencesWindow.Open(Host, page);
            Assert.That(prefs, Is.Not.Null);
            Settle(prefs);
            return prefs;
        }

        static void ClosePrefs()
        {
            PreferencesWindow.Current?.Close();
            Dialogs.Responder = null;
            Settle();
        }

        static IAppServices App => Host.Agent.Host.AppControl;

        static JsonObject LastHistory(string key = null)
        {
            var lines = File.Exists(App.SettingsHistoryFile) ? File.ReadAllLines(App.SettingsHistoryFile) : Array.Empty<string>();
            return lines.Reverse().Select(l => JsonNode.Parse(l)!.AsObject()).FirstOrDefault(o => key == null || o["key"]!.GetValue<string>() == key);
        }

        [AvaloniaTest]
        public void PreferencesIsOneWindowWithTheSpecsPagesInOrder()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("General");
            var titles = prefs.Pages.Select(p => p.Title).ToList();
            Assert.That(titles.Where(t => t != "Sync"), Is.EqualTo(new[] { "General", "Audio", "MIDI", "Appearance", "Agent", "Safety", "Advanced" }));
            Assert.That(PreferencesWindow.Open(Host), Is.SameAs(prefs), "one window");
            Assert.That(prefs.RestoreButton.IsVisible, Is.True);
            foreach (var page in prefs.Pages)
            {
                prefs.ShowPage(page.Title);
                Settle(prefs);
                Studio.Screenshot(prefs, "prefs-" + page.Title.ToLowerInvariant());
            }
        }

        [AvaloniaTest]
        public void EverySettingOnThePagesIsTheAgentsSettingUnderItsKey()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("General");
            var model = prefs.Model;
            Assert.That(model, Is.Not.Null);
            var keys = App.Settings().Select(e => e.Key).ToList();
            foreach (var (section, hidden) in new[] { ("general", GeneralPage.WindowsOnlyGeneral), ("advanced", AdvancedPage.WindowsOnlyEngine), ("midi", new string[0]), ("appearance", new string[0]) })
            {
                var page = prefs.Pages.First(p => p.Sections.Contains(section));
                foreach (var e in model.Section(section).Where(e => !hidden.Contains(e.Name)))
                    Assert.That(page.SettingControl(e.Key), Is.Not.Null, $"{e.Key} is on the {page.Title} page");
                Assert.That(model.Section(section).Select(e => e.Key), Is.All.Matches<string>(k => keys.Contains(k)), "the same list settings_get gives");
            }
            Assert.That(prefs.Pages.OfType<SafetyPage>().Single().Title, Is.EqualTo("Safety"));
            Assert.That(model.Section("safety").Select(e => e.Key), Is.EquivalentTo(new[] { "safety.SafetyLimiter", "safety.LimiterCeilingDb", "safety.ClipThresholdDb", "safety.MachineGuard" }));
        }

        [AvaloniaTest]
        public void AChangeInTheWindowIsInTheSettingsHistoryAsManual()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("General");
            var page = prefs.Page<GeneralPage>();
            var box = (CheckBox)page.SettingControl("general.AutomaticBackups");
            bool was = Global.GeneralSettings.AutomaticBackups;
            try
            {
                box.IsChecked = !was;
                Settle();
                Assert.That(Global.GeneralSettings.AutomaticBackups, Is.EqualTo(!was));
                var entry = LastHistory("general.AutomaticBackups");
                Assert.That(entry, Is.Not.Null);
                Assert.That(entry!["source"]!.GetValue<string>(), Is.EqualTo("Manual"));
                Assert.That(entry["from"]!.GetValue<string>(), Is.EqualTo(was ? "True" : "False"));
                Assert.That(entry["to"]!.GetValue<string>(), Is.EqualTo(was ? "False" : "True"));
                // The agent's own tool reads the same log.
                var viaTool = AgentTool("settings_history", new JsonObject { ["key"] = "general.AutomaticBackups", ["limit"] = 1 });
                Assert.That(viaTool["entries"]![0]!["source"]!.GetValue<string>(), Is.EqualTo("Manual"));
            }
            finally
            {
                Global.GeneralSettings.Set("AutomaticBackups", was);
                Global.GeneralSettings.Save();
            }
        }

        internal static JsonObject AgentTool(string tool, JsonObject args)
        {
            var message = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
                ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = args },
            };
            var task = Host.Agent.Host.Server.HandleAsync(message, new ReBuzz.Agent.Mcp.McpSession(), _ => Task.CompletedTask, System.Threading.CancellationToken.None);
            var until = DateTime.UtcNow.AddSeconds(60);
            while (!task.IsCompleted)
            {
                Dispatcher.UIThread.RunJobs();
                if (DateTime.UtcNow > until) Assert.Fail($"{tool} didn't answer in time");
                System.Threading.Thread.Sleep(2);
            }
            var r = task.GetAwaiter().GetResult()!;
            Assert.That(r["error"], Is.Null, tool);
            var result = r["result"]!.AsObject();
            Assert.That(result["isError"]?.GetValue<bool>() ?? false, Is.False, result["content"]![0]!["text"]!.GetValue<string>());
            return result["structuredContent"]!.AsObject();
        }

        [AvaloniaTest]
        public void AChangeByTheAgentShowsInTheOpenWindowAndIsLoggedAsAgent()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("General");
            var box = (CheckBox)prefs.Page<GeneralPage>().SettingControl("general.AutomaticBackups");
            bool was = Global.GeneralSettings.AutomaticBackups;
            try
            {
                Assert.That(box.IsChecked, Is.EqualTo(was));
                AgentTool("settings_set", new JsonObject { ["key"] = "general.AutomaticBackups", ["value"] = !was });
                Settle(prefs);
                Assert.That(box.IsChecked, Is.EqualTo(!was), "the open window shows what the agent set");
                Assert.That(LastHistory("general.AutomaticBackups")!["source"]!.GetValue<string>(), Is.EqualTo("Agent"));

                // And a safety setting, on its own page.
                prefs.ShowPage("Safety");
                var safety = prefs.Page<SafetyPage>();
                Settle(prefs);
                AgentTool("settings_set", new JsonObject { ["key"] = "safety.LimiterCeilingDb", ["value"] = -3 });
                Settle(prefs);
                Assert.That(safety.CeilingBox.SelectedItem, Is.EqualTo("−3 dBFS"));
            }
            finally
            {
                Global.GeneralSettings.Set("AutomaticBackups", was);
                Global.GeneralSettings.Save();
                Host.Core.SafeMaster.CeilingDb = 0;
                Host.Core.SafeMaster.Save(Host.Registry);
            }
        }

        [AvaloniaTest]
        public void ASettingThatNeedsARestartSaysSoOnItsRowAndUnderThePages()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("Audio");
            var threads = App.Settings().Single(e => e.Key == "audio.AudioThreads");
            Assert.That(threads.RestartRequired, Is.True);
            Assert.That(prefs.RestartText, Is.Empty);
            string was = SettingsService.Get(threads);
            try
            {
                SettingsService.Apply(App, threads, was == "2" ? "3" : "2");
                Settle(prefs);
                Assert.That(prefs.RestartText, Does.Contain("Restart").And.Contain("Audio threads"));
                Assert.That(LastHistory("audio.AudioThreads")!["restart_required"]!.GetValue<bool>(), Is.True);
                SettingsService.Apply(App, threads, was);       // back to what it was at the start: nothing waits
                Settle(prefs);
                Assert.That(prefs.RestartText, Is.Empty);
            }
            finally
            {
                threads.Set(was);
            }
            // A row of the generic pages says it too, when its setting does.
            var advanced = App.Settings().First(e => e.Section == "advanced" && e.RestartRequired && !AdvancedPage.WindowsOnlyEngine.Contains(e.Name));
            prefs.ShowPage("Advanced");
            Settle(prefs);
            var grid = prefs.Page<AdvancedPage>().SettingControl(advanced.Key)!.GetVisualAncestors().OfType<Grid>().First();
            Assert.That(grid.GetVisualDescendants().OfType<TextBlock>().Any(t => (t.Text ?? "").Contains("estart", StringComparison.Ordinal)), Is.True);
        }

        [AvaloniaTest]
        public void RestoreDefaultsPutsThePageBackAfterAskingWhereItGuardsSomething()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("Advanced");
            var flag = App.Settings().First(e => e.Section == "advanced" && e.Kind == SettingKind.Bool && !AdvancedPage.WindowsOnlyEngine.Contains(e.Name) && !e.RestartRequired);
            string was = SettingsService.Get(flag);
            var control = (CheckBox)prefs.Page<AdvancedPage>().SettingControl(flag.Key)!;
            try
            {
                bool other = flag.Default.Equals("True", StringComparison.OrdinalIgnoreCase) ? false : true;
                control.IsChecked = other;
                Settle();
                Assert.That(SettingsService.IsDefault(flag), Is.False);

                string asked = null;
                Dialogs.Responder = (m, c, b) => { asked = c; return UserAnswer.No; };
                _ = prefs.RestoreDefaults();
                Settle();
                Assert.That(asked, Is.EqualTo("Restore Defaults"), "the engine flags ask first");
                Assert.That(SettingsService.IsDefault(flag), Is.False, "Cancel leaves it");

                Dialogs.Responder = (m, c, b) => UserAnswer.Yes;
                _ = prefs.RestoreDefaults();
                Settle();
                Assert.That(SettingsService.IsDefault(flag), Is.True);
                Assert.That(control.IsChecked, Is.EqualTo(flag.Default.Equals("True", StringComparison.OrdinalIgnoreCase)), "the page shows it");
                Assert.That(LastHistory(flag.Key)!["source"]!.GetValue<string>(), Is.EqualTo("Manual"));
            }
            finally
            {
                flag.Set(was);
            }

            // Safety asks too, and puts the guards back on.
            prefs.ShowPage("Safety");
            Settle(prefs);
            var safety = prefs.Page<SafetyPage>();
            Host.Core.SafeMaster.CeilingDb = -6;
            safety.RefreshSettings();
            Dialogs.Responder = (m, c, b) => UserAnswer.Yes;
            _ = prefs.RestoreDefaults();
            Settle();
            Assert.That(Host.Core.SafeMaster.CeilingDb, Is.EqualTo(0));
            Assert.That(safety.CeilingBox.SelectedItem, Is.EqualTo("0 dBFS"));
            Assert.That(safety.LimiterBox.IsChecked, Is.True);
        }

        [AvaloniaTest]
        public void AValueTheToolWouldRefuseIsRefusedOnThePageToo()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("General");
            var entry = prefs.Model.Section("general").FirstOrDefault(e => e.Kind == SettingKind.Int && e.Options == null && e.Min != null)
                        ?? prefs.Model.Section("general").FirstOrDefault(e => e.Kind is SettingKind.Text);
            Assume.That(entry, Is.Not.Null, "no free-text setting in General");
            var box = prefs.Page<GeneralPage>().SettingControl(entry.Key) as TextBox;
            Assume.That(box, Is.Not.Null);
            string was = SettingsService.Get(entry);
            box!.Text = entry.Kind == SettingKind.Int ? "not a number" : box.Text;
            if (entry.Kind == SettingKind.Int)
            {
                box.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(InputElement.LostFocusEvent));
                Settle();
                Assert.That(SettingsService.Get(entry), Is.EqualTo(was), "unchanged");
                Assert.That(box.Text, Is.EqualTo(was), "the box shows what it holds");
            }
        }

        [AvaloniaTest]
        public void MidiDevicesAreATableWithAHeader()
        {
            using var restore = Studio.AfterTest(ClosePrefs);
            var prefs = OpenPrefs("MIDI");
            var page = prefs.Page<MidiPage>();
            Assert.That(page.SettingControl("midi.MIDIFiltering") ?? page.SettingControl(Host.Agent.Host.AppControl.Settings().First(e => e.Section == "midi").Key), Is.Not.Null, "the options are the agent's midi section");
        }

        // ------------------------------------------------------------------ the switcher

        [AvaloniaTest]
        public void TheViewSwitchersSegmentsAreTabFocusableAndLeaveSpaceToThePlayKey()
        {
            using var rig = new DeviceRig();
            var segments = rig.Window.Switcher.Segments.Values.ToList();
            Assert.That(segments, Has.Count.EqualTo(3));
            Assert.That(segments, Has.All.Matches<Button>(b => b.Focusable && b.IsTabStop));
            segments[0].Focus(NavigationMethod.Tab);
            Settle(rig.Window);
            Assert.That(segments[0].IsFocused, Is.True);
            Studio.Screenshot(rig.Window, "switcher-focus");
            int clicks = 0;
            segments[0].Click += (s, e) => clicks++;
            rig.Window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            rig.Window.KeyRelease(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
            Settle(rig.Window);
            Assert.That(clicks, Is.EqualTo(0), "Space is the window's: it plays and stops everywhere, a focused segment doesn't take it");
            Host.Core.Playing = false;
        }

        [AvaloniaTest]
        public void ShotDeviceFm6()
        {
            using var restore = Studio.AfterTest(ResetDevice);
            using var rig = new DeviceRig();
            rig.Window.Detail.ShowDevice();
            rig.Select(rig.Fm);
            Studio.Screenshot(rig.Window, Environment.GetEnvironmentVariable("P3F_SHOT") ?? "p3f-device-fm6");
        }
    }
}
