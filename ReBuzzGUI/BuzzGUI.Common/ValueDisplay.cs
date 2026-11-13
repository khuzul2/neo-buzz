#nullable disable
// Fork addition (neo-buzz, docs/specs/ux-overhaul.md P3c, decision 51): how the pattern editor shows the values of byte, word
// and switch parameters: decimal (the default) or hex (Buzz's). One setting (Preferences > General > Pattern values,
// GeneralSettings.PatternValues) and a toolbar toggle in the editor; display and keyboard entry only, songs hold the same
// numbers. The WPF build of the editor never reads it (its code is under #if WINDOWS). Pure, like NoteNames.

using System;

namespace BuzzGUI.Common
{
    /// <summary>The base the pattern editor writes numbers in.</summary>
    public enum ValueBase
    {
        /// <summary>Decimal, zero padded to the digits of the parameter's largest value ("064" of 0-254).</summary>
        [System.ComponentModel.Description("Decimal")]
        Decimal,

        /// <summary>Buzz's hexadecimal ("40").</summary>
        [System.ComponentModel.Description("Hex")]
        Hex,
    }

    public static class ValueDisplay
    {
        public const ValueBase Default = ValueBase.Decimal;

        static ValueBase current = Default;

        /// <summary>The base every pattern editor reads; GeneralSettings.PatternValues sets it.</summary>
        public static ValueBase Base
        {
            get => current;
            set
            {
                if (!Enum.IsDefined(typeof(ValueBase), value) || value == current) return;
                current = value;
                Changed?.Invoke();
            }
        }

        public static bool IsHex => current == ValueBase.Hex;

        /// <summary>After <see cref="Base"/> changes: editors lay their columns out and draw them again.</summary>
        public static event Action Changed;

        /// <summary>How many decimal digits the numbers 0 to <paramref name="max"/> take (at least 1).</summary>
        public static int DecimalDigits(int max)
        {
            int n = 1;
            for (long v = Math.Max(0, max); v >= 10; v /= 10) n++;
            return n;
        }
    }
}
