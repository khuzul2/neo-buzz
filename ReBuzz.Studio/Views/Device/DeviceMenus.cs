// Fork (docs/specs/ux-overhaul.md, the parameter panel): the menus of the Device panel. A control's right-click menu (reset, type a value,
// pin to the Inspector, MIDI binding, attributes, tracks) and the page's Presets menu: what the retired parameter window kept in its
// context menu and its top row.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using BuzzGUI.Common.Actions.MachineActions;
using BuzzGUI.Common.InterfaceExtensions;
using BuzzGUI.Common.Presets;
using BuzzGUI.Interfaces;
using ReBuzz.Core;
using ReBuzz.Studio.Parameters;

namespace ReBuzz.Studio.Views.Device
{
    using Control = Avalonia.Controls.Control;

    internal static class DeviceMenus
    {
        public const string DefaultPreset = "<default>";

        static MenuItem Item(string header, Action run, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            item.Click += (s, e) => run();
            return item;
        }

        /// <summary>The parameter a pin names: its name in the machine (a global parameter).</summary>
        public static string PinKey(DeviceParam p) => p.Parameter.Name;

        /// <summary>A control's menu. <paramref name="type"/> opens its value editor.</summary>
        public static ContextMenu ForParam(IMachine machine, DeviceParam p, Action type, Action changed)
        {
            var core = DeviceEdits.CoreOf(machine);
            var items = new List<Control>
            {
                Item("_Reset to Default", () => { DeviceEdits.Reset(core, p); changed?.Invoke(); }, p.Value != p.Default),
            };
            if (type != null && p.Kind == DeviceKind.Slider) items.Add(Item("_Type a Value...", type));
            if (p.CanPin && core?.PinStore is { } pins)
            {
                bool pinned = pins.IsPinned(machine.Name, PinKey(p));
                items.Add(pinned
                    ? Item("_Unpin from Inspector", () => { pins.Set(machine.Name, PinKey(p), false); changed?.Invoke(); })
                    : Item("_Pin to Inspector", () => { pins.Set(machine.Name, PinKey(p), true); changed?.Invoke(); }, pins.Get(machine.Name).Count < MachinePinStore.Max));
            }
            items.Add(new Separator());
            var bindItems = new List<Control> { Item("Bind...", async () => await BindLearned(p)), new Separator() };
            var controllers = core?.MIDIControllers;
            if (controllers != null && controllers.Count > 0)
                bindItems.AddRange(controllers.Select((name, index) => (Control)Item(name, () => p.Parameter.BindToMIDIController(p.Track, index))));
            else bindItems.Add(Item("(no controllers defined)", () => { }, enabled: false));
            items.Add(new MenuItem { Header = "_Bind to MIDI Controller", ItemsSource = bindItems });
            items.Add(Item("_Unbind All", () => machine.UnbindAllMIDIControllers()));
            items.Add(new MenuItem { Header = "_Attributes", IsEnabled = machine.Attributes.Count > 0, ItemsSource = AttributeItems(machine) });
            int tracks = machine.TrackCount;
            if (machine.DLL.Info.MaxTracks > machine.DLL.Info.MinTracks)
            {
                items.Add(new Separator());
                items.Add(Item("A_dd Track", () => SetTrackCount(machine, tracks + 1), tracks < machine.DLL.Info.MaxTracks));
                items.Add(Item("D_elete Last Track", () => SetTrackCount(machine, tracks - 1), tracks > machine.DLL.Info.MinTracks));
            }
            return new ContextMenu { FontSize = 12, ItemsSource = items };
        }

        static async Task BindLearned(DeviceParam p)
        {
            var c = await ParameterDialogs.AskController();
            if (c is (int channel, int controller)) p.Parameter.BindToMIDIController(p.Track, channel - 1, controller);
        }

        /// <summary>Adds or removes the last track: one undo step.</summary>
        public static void SetTrackCount(IMachine machine, int count)
        {
            count = Math.Clamp(count, machine.DLL.Info.MinTracks, machine.DLL.Info.MaxTracks);
            if (count == machine.TrackCount) return;
            var stack = DeviceEdits.CoreOf(machine)?.SongCore?.ActionStack;
            if (stack != null) stack.Do(new SetTrackCountAction(machine, count));
            else machine.TrackCount = count;
        }

        static List<Control> AttributeItems(IMachine machine)
        {
            var items = new List<Control>();
            foreach (var a in machine.Attributes)
            {
                var children = new List<Control>();
                foreach (int v in a.GetPresentableNumberOfValues())
                {
                    var item = new MenuItem { Header = v.ToString(), ToggleType = MenuItemToggleType.Radio, IsChecked = v == a.Value, FontWeight = v == a.DefValue ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal, StaysOpenOnClick = true };
                    item.Click += (s, e) => a.Value = v;
                    children.Add(item);
                }
                items.Add(new MenuItem { Header = a.Name, ItemsSource = children });
            }
            if (items.Count == 0) items.Add(new MenuItem { Header = "(no attributes)", IsEnabled = false });
            return items;
        }

        // ---------------------------------------------------------------- presets

        /// <summary>The page's Presets menu: apply one, the Presets… dialog, copy and paste.</summary>
        public static ContextMenu Presets(IMachine machine, Visual owner, Action changed)
        {
            var core = DeviceEdits.CoreOf(machine);
            var items = new List<Control>();
            foreach (var name in new[] { DefaultPreset }.Concat(machine.GetPresetNames()))
            {
                string n = name;
                items.Add(Item(n, () => { ParameterEdits.ApplyPreset(core, machine, machine.GetPreset(n)); changed?.Invoke(); }));
            }
            items.Add(new Separator());
            items.Add(Item("_Presets...", () => OpenPresets(machine, owner)));
            var paste = Item("_Paste Preset", async () => await Paste(machine, owner, changed), false);
            items.Add(Item("_Copy Preset (ASCII)", async () => await Copy(machine, owner, false)));
            items.Add(Item("C_opy Preset (XML)", async () => await Copy(machine, owner, true)));
            items.Add(paste);
            var menu = new ContextMenu { FontSize = 12, ItemsSource = items };
            menu.Opening += async (s, e) => paste.IsEnabled = await FromClipboard(machine, owner) != null;
            return menu;
        }

        public static PresetsWindow OpenPresets(IMachine machine, Visual owner)
        {
            var window = new PresetsWindow(machine);
            if (TopLevel.GetTopLevel(owner) is Window w) _ = window.ShowDialog(w);
            else window.Show();
            return window;
        }

        static async Task Copy(IMachine machine, Visual owner, bool xml)
        {
            var preset = new Preset(machine, false, true);
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard != null) await clipboard.SetTextAsync(xml ? preset.ToXml() : preset.ToString());
        }

        static async Task<Preset> FromClipboard(IMachine machine, Visual owner)
        {
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            string text = clipboard != null ? await clipboard.TryGetTextAsync() : null;
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var p = Preset.FromXml(text) ?? new Preset(machine, text);
                return p != null && p.Machine == machine.DLL.Name ? p : null;
            }
            catch (Exception) { return null; }
        }

        static async Task Paste(IMachine machine, Visual owner, Action changed)
        {
            var p = await FromClipboard(machine, owner);
            if (p == null) return;
            ParameterEdits.ApplyPreset(DeviceEdits.CoreOf(machine), machine, p);
            changed?.Invoke();
        }
    }
}
