using BuzzGUI.Interfaces;
using ReBuzz.Core;
using System.Collections.Generic;

namespace ReBuzz.AppViews
{
    /// <summary>
    /// Fork (docs/specs/engine-split.md S7): the engine's view of the Sequence View's pattern
    /// colours, over SequenceEditor.ViewSettings.PatternAssociations, which the engine used to read
    /// and write directly. Null while the sequence editor has no view settings.
    /// </summary>
    internal sealed class SequenceEditorPatternColours : IPatternColours
    {
        static SequenceEditorPatternColours last;
        readonly Dictionary<IPattern, BuzzGUI.SequenceEditor.PatternEx> map;

        SequenceEditorPatternColours(Dictionary<IPattern, BuzzGUI.SequenceEditor.PatternEx> map) => this.map = map;

        internal static IPatternColours Current()
        {
            var map = BuzzGUI.SequenceEditor.SequenceEditor.ViewSettings?.PatternAssociations;
            if (map == null)
                return null;
            var view = last;
            if (view == null || view.map != map)
                last = view = new SequenceEditorPatternColours(map);
            return view;
        }

        public IEnumerable<IPattern> Patterns => map.Keys;

        public bool TryGet(IPattern pattern, out int colorIndex)
        {
            if (map.TryGetValue(pattern, out var ex))
            {
                colorIndex = ex.ColorIndex;
                return true;
            }
            colorIndex = -1;
            return false;
        }

        public void Set(IPattern pattern, int colorIndex)
        {
            if (map.TryGetValue(pattern, out var ex))
                ex.ColorIndex = colorIndex;
            else
                map[pattern] = new BuzzGUI.SequenceEditor.PatternEx { ColorIndex = colorIndex };
        }

        public void Add(IPattern pattern, int colorIndex) => map.Add(pattern, new BuzzGUI.SequenceEditor.PatternEx { ColorIndex = colorIndex });

        public bool Remove(IPattern pattern) => map.Remove(pattern);
    }
}
