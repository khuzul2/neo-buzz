using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ReBuzz.Core
{
    /// <summary>
    /// The theme's colours as an ARGB table (engine-split.md S4): Buzz's defaults, then the
    /// selected theme's .col file. The engine answers IBuzz.GetThemeColour from it; the WPF app
    /// builds IBuzz.ThemeColors (WPF colours) from the same table. Moved from UiUtils.GetThemeColors.
    /// </summary>
    public static class ThemeColours
    {
        /// <summary>A name the table lacks: black, as the WPF colour index answered.</summary>
        public const uint Missing = 0xFF000000;

        public static Dictionary<string, uint> Load(string buzzPath, string selectedThemeName)
        {
            var table = new Dictionary<string, uint>();
            foreach (var (name, argb) in Defaults)
                table[name] = argb;

            string selectedTheme = selectedThemeName == "<default>" ? "Default" : selectedThemeName + ".col";

            if (selectedTheme != "Default")
            {
                string skinPath = buzzPath + "\\Themes\\" + selectedTheme;

                string[] readText = File.ReadAllLines(skinPath);
                string[] delimiterChars = {
                            "\t",
                            "  "
                          };
                foreach (string s in readText)
                {
                    try
                    {
                        if (!s.Trim().StartsWith("#") && s.Trim().Length > 0)
                        {
                            string[] items = s.Trim().Split(delimiterChars, StringSplitOptions.RemoveEmptyEntries);
                            string key = items[0].Trim();
                            uint value = ParseHexColour("#ff" + items.Last().Trim());
                            table[key] = value;
                        }
                    }
                    catch { }
                }
            }

            return table;
        }

        /// <summary>
        /// "#RGB", "#ARGB", "#RRGGBB" or "#AARRGGBB" as ARGB, as WPF's ColorConverter parses them
        /// (a short form repeats each digit). Throws FormatException otherwise.
        /// </summary>
        public static uint ParseHexColour(string s)
        {
            s = s.Trim();
            if (!s.StartsWith("#"))
                throw new FormatException("Colour must start with #: " + s);
            string h = s.Substring(1);
            int Digit(int i)
            {
                char c = h[i];
                if (c >= '0' && c <= '9') return c - '0';
                if (c >= 'a' && c <= 'f') return c - 'a' + 10;
                if (c >= 'A' && c <= 'F') return c - 'A' + 10;
                throw new FormatException("Bad hex digit in colour: " + s);
            }
            uint Short(int i) => (uint)(Digit(i) * 17);
            uint Long(int i) => (uint)(Digit(i) * 16 + Digit(i + 1));
            switch (h.Length)
            {
                case 3: return 0xFF000000 | Short(0) << 16 | Short(1) << 8 | Short(2);
                case 4: return Short(0) << 24 | Short(1) << 16 | Short(2) << 8 | Short(3);
                case 6: return 0xFF000000 | Long(0) << 16 | Long(2) << 8 | Long(4);
                case 8: return Long(0) << 24 | Long(2) << 16 | Long(4) << 8 | Long(6);
                default: throw new FormatException("Bad colour length: " + s);
            }
        }

        /// <summary>Buzz's default colours (ARGB), before a theme's .col file overrides them.</summary>
        public static readonly (string Name, uint Color)[] Defaults =
        [
            ("DC BG", 0xFF000000),
            ("DC Text", 0xFFC0C0C0),
            ("IV BG", 0xFF000000),
            ("IV Text", 0xFF000000),
            ("MV Amp BG", 0xFFFFFFFF),
            ("MV Amp Handle", 0xFF000000),
            ("MV Arrow", 0xFFFFFFFF),
            ("MV Background", 0xFFDAD6C9),
            ("MV Control", 0xFFADADAD),
            ("MV Effect", 0xFFC7ADA9),
            ("MV Effect LED Border", 0xFF000000),
            ("MV Effect LED Off", 0xFF641E1E),
            ("MV Effect LED On", 0xFFFF6464),
            ("MV Effect Mute", 0xFF9F8A87),
            ("MV Effect Pan BG", 0xFF92655F),
            ("MV Generator", 0xFFA9AEC7),
            ("MV Generator LED Border", 0xFF000000),
            ("MV Generator LED Off", 0xFF28288C),
            ("MV Generator LED On", 0xFF6464FF),
            ("MV Generator Mute", 0xFF878B9F),
            ("MV Generator Pan BG", 0xFF5F6792),
            ("MV Line", 0xFF000000),
            ("MV Machine Border", 0xFF000000),
            ("MV Machine Select 1", 0xFFFFFFFF),
            ("MV Machine Select 2", 0xFF000000),
            ("MV Machine Text", 0xFF000000),
            ("MV Master", 0xFFC6BEAA),
            ("MV Master LED Border", 0xFF000000),
            ("MV Master LED Off", 0xFF595922),
            ("MV Master LED On", 0xFFE8E8C1),
            ("MV Pan Handle", 0xFF000000),
            ("PE BG", 0xFFDAD6C9),
            ("PE BG Dark", 0xFFBDB59F),
            ("PE BG Very Dark", 0xFF9F9373),
            ("PE Sel BG", 0xFFF7F7F4),
            ("PE Text", 0xFF303021),
            ("SA Amp BG", 0xFF000000),
            ("SA Amp Line", 0xFF00C800),
            ("SA Freq BG", 0xFF000000),
            ("SA Freq Line", 0xFF00C800),
            ("SE BG", 0xFFDAD6C9),
            ("SE BG Dark", 0xFFBDB59F),
            ("SE BG Very Dark", 0xFF9F9373),
            ("SE Break Box", 0xFFE0B06D),
            ("SE Line", 0xFF000000),
            ("SE Mute Box", 0xFFDD816C),
            ("SE Pattern Box", 0xFFC6BEA9),
            ("SE Sel BG", 0xFFF7F7F4),
            ("SE Song Position", 0xFFFFFF00),
            ("SE Text", 0xFF303021),
            ("SE Off Box", 0xFF606060),
            ("black", 0xFF000000)
        ];
    }
}
