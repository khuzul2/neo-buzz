#nullable disable
// Neo editor kit, toolkit-free core: what a graph surface that also takes the right button, the keyboard and typed text implements (the
// inner canvas of Neo Patch, docs/specs/neo-patch.md §4.5). The toolkit's InteractiveGraph checks for it: a surface that is only an
// IGraphInput behaves exactly as before.

namespace NeoBuzz.Editor
{
    internal interface IGraphInputPlus : IGraphInput
    {
        /// <summary>The right button went down at a point.</summary>
        bool RightDown(double x, double y);

        /// <summary>A key went down. <paramref name="key"/> is the toolkit's key name ("Delete", "Back", "Return", "Escape", "Up", "C"…), ctrl is Ctrl or Command.</summary>
        bool Key(string key, bool ctrl, bool shift);

        /// <summary>Typed text (a search box).</summary>
        bool Text(string text);

        /// <summary>True while the surface wants typed text: the toolkit then takes the keys as text, not as shortcuts.</summary>
        bool WantsText { get; }
    }
}
