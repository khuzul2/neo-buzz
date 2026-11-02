namespace ReBuzz.Core.Session
{
    /// <summary>
    /// Clip, track and scene colours as ARGB (engine-split.md S7): an index into the theme's
    /// PatternColors, the same list the Sequence View uses. The Session View's palette
    /// (SessionPalette.ColorChoices, WPF colours) keeps this table current; controller pads read it.
    /// </summary>
    internal static class SessionColourChoices
    {
        /// <summary>The Default theme's list.</summary>
        public static readonly (string Name, uint Argb)[] Defaults =
        {
            ("Powder Blue", 0xFF8EB9C2), ("Moss Green", 0xFFC7DBAE), ("Wheat", 0xFFF5DEB3), ("Light Blue", 0xFFA8C5DD),
            ("Ochre", 0xFFC47E29), ("Camouflage Green", 0xFF677665), ("Asparagus", 0xFF678443), ("Slate Blue", 0xFF5A6BA1),
            ("Lilac", 0xFFB97AA3), ("Red", 0xFFD7001C), ("Orange", 0xFFE15E13), ("Yellow", 0xFFF7C12D),
            ("Light Olive", 0xFFA6C64C), ("Kelly Green", 0xFF379C35), ("Sky Blue", 0xFF57B8E8), ("Steel Blue", 0xFF446FB6),
        };

        public static (string Name, uint Argb)[] Choices { get; set; } = Defaults;
    }
}
