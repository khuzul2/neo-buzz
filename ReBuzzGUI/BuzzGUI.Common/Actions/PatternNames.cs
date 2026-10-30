using BuzzGUI.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BuzzGUI.Common.Actions
{
    /// <summary>
    /// Fork addition (0.6.3): pattern names for patterns an action makes (again). A machine
    /// doesn't refuse a name that is taken, and pattern editors keep their data by name, so an
    /// undo that brings a pattern back while another took its name makes it under a free one.
    /// </summary>
    public static class PatternNames
    {
        /// <summary><paramref name="name"/> if no pattern of the machine has it, else <see cref="Copy"/>.</summary>
        public static string Free(IMachine machine, string name) =>
            machine.Patterns.Any(p => p.Name == name) ? Copy(machine, name) : name;

        /// <summary>A free name after <paramref name="name"/>: its number counted up ("01" → "02"), or " 2" added.</summary>
        public static string Copy(IMachine machine, string name)
        {
            var taken = new HashSet<string>(machine.Patterns.Select(p => p.Name));
            // ASCII digits only, at most 9 (longer runs overflowed int.Parse).
            var m = Regex.Match(name ?? "", @"^(.*?)([0-9]{1,9})$");
            string stem = m.Success ? m.Groups[1].Value : (name ?? "") + " ";
            long number = m.Success ? long.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) + 1 : 2;
            int width = m.Success ? m.Groups[2].Value.Length : 1;
            string candidate;
            do
            {
                candidate = stem + number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0');
                number++;
            }
            while (taken.Contains(candidate));
            return candidate;
        }

        /// <summary>The pattern of <paramref name="machine"/> that isn't in <paramref name="before"/> (the one just made), or null.</summary>
        public static IPattern Added(IMachine machine, ICollection<IPattern> before) =>
            machine.Patterns.LastOrDefault(p => !before.Contains(p));
    }
}
