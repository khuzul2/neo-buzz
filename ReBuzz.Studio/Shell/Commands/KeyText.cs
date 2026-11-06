// Fork (docs/specs/ux-overhaul.md §4): keys written once ("Shift+Cmd+A") and read for the platform: Cmd is ⌘ on a Mac
// and Ctrl elsewhere, Ctrl is always the control key (Ctrl+Tab). The same text shows in the menus, the shortcuts
// window, the context menus and the tooltips, with the Mac's glyphs (⇧⌘A, ⌫) on a Mac and words elsewhere (Ctrl+Shift+A).

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Input;

namespace ReBuzz.Studio.Shell.Commands
{
    internal static class KeyText
    {
        public static bool IsMac => OperatingSystem.IsMacOS();

        static readonly Dictionary<string, Key> Names = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Space"] = Key.Space, ["Back"] = Key.Back, ["Backspace"] = Key.Back, ["Delete"] = Key.Delete, ["Del"] = Key.Delete,
            ["Enter"] = Key.Enter, ["Return"] = Key.Enter, ["Esc"] = Key.Escape, ["Escape"] = Key.Escape, ["Tab"] = Key.Tab,
            ["Home"] = Key.Home, ["End"] = Key.End, ["PageUp"] = Key.PageUp, ["PageDown"] = Key.PageDown, ["Insert"] = Key.Insert,
            ["Up"] = Key.Up, ["Down"] = Key.Down, ["Left"] = Key.Left, ["Right"] = Key.Right,
            ["Comma"] = Key.OemComma, ["Period"] = Key.OemPeriod, ["Slash"] = Key.Oem2, ["Plus"] = Key.OemPlus, ["Minus"] = Key.OemMinus,
        };

        /// <summary>A key written as "Shift+Cmd+A", for a Mac or not (default: this platform). Null for null or "".</summary>
        public static KeyGesture Parse(string text, bool? mac = null)
        {
            if (string.IsNullOrEmpty(text)) return null;
            bool m = mac ?? IsMac;
            var mods = KeyModifiers.None;
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                mods |= parts[i] switch
                {
                    "Cmd" => m ? KeyModifiers.Meta : KeyModifiers.Control,
                    "Ctrl" => KeyModifiers.Control,
                    "Shift" => KeyModifiers.Shift,
                    "Alt" => KeyModifiers.Alt,
                    _ => throw new ArgumentException("Not a modifier: " + parts[i]),
                };
            }
            return new KeyGesture(KeyOf(parts[^1]), mods);
        }

        static Key KeyOf(string name)
        {
            if (Names.TryGetValue(name, out var k)) return k;
            if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') return Key.A + (name[0] - 'A');
            if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') return Key.D0 + (name[0] - '0');
            if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.Substring(1), out int f) && f >= 1 && f <= 24) return Key.F1 + (f - 1);
            return Enum.Parse<Key>(name);
        }

        /// <summary>A key as the user reads it: "⇧⌘A", "⌫", "F7" on a Mac; "Ctrl+Shift+A", "Backspace" elsewhere.</summary>
        public static string Of(KeyGesture key, bool? mac = null)
        {
            if (key == null) return "";
            bool m = mac ?? IsMac;
            var mods = key.KeyModifiers;
            if (m)
            {
                string s = "";
                if (mods.HasFlag(KeyModifiers.Control)) s += "⌃";
                if (mods.HasFlag(KeyModifiers.Alt)) s += "⌥";
                if (mods.HasFlag(KeyModifiers.Shift)) s += "⇧";
                if (mods.HasFlag(KeyModifiers.Meta)) s += "⌘";
                return s + Name(key.Key, true);
            }
            var parts = new List<string>();
            if (mods.HasFlag(KeyModifiers.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(KeyModifiers.Alt)) parts.Add("Alt");
            if (mods.HasFlag(KeyModifiers.Shift)) parts.Add("Shift");
            if (mods.HasFlag(KeyModifiers.Meta)) parts.Add("Win");
            parts.Add(Name(key.Key, false));
            return string.Join("+", parts);
        }

        /// <summary>Keys written as "Shift+Cmd+A" (several: "R|F7"), as the user reads them, joined with " or ".</summary>
        public static string Of(string keys, bool? mac = null) =>
            string.Join(" or ", Split(keys).Select(k => Of(Parse(k, mac), mac)));

        public static IEnumerable<string> Split(string keys) =>
            string.IsNullOrEmpty(keys) ? Enumerable.Empty<string>() : keys.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        static string Name(Key k, bool mac)
        {
            switch (k)
            {
                case Key.Back: return mac ? "⌫" : "Backspace";
                case Key.Delete: return mac ? "⌦" : "Delete";
                case Key.Enter: return mac ? "↩" : "Enter";
                case Key.Escape: return "Esc";
                case Key.Tab: return mac ? "⇥" : "Tab";
                case Key.Space: return "Space";
                case Key.Up: return "↑";
                case Key.Down: return "↓";
                case Key.Left: return "←";
                case Key.Right: return "→";
                case Key.PageUp: return mac ? "⇞" : "Page Up";
                case Key.PageDown: return mac ? "⇟" : "Page Down";
                case Key.OemComma: return ",";
                case Key.OemPeriod: return ".";
                case Key.Oem2: return "/";
                case Key.OemPlus: return "+";
                case Key.OemMinus: return "−";
            }
            if (k >= Key.D0 && k <= Key.D9) return ((char)('0' + (k - Key.D0))).ToString();
            return k.ToString();
        }
    }
}
