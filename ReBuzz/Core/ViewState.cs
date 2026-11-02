using System;
using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace ReBuzz.Core
{
    /// <summary>
    /// View settings the engine reads (engine-split.md S7). The WPF app answers them from the
    /// machine view's settings (WpfMachineApiHooks); unset, engine threads are not shown and skins
    /// are off.
    /// </summary>
    public static class MachineViewState
    {
        public static Func<bool> ShowEngineThreads { get; set; }
        public static Func<bool> EnableSkins { get; set; }

        internal static bool ShowsEngineThreads => ShowEngineThreads?.Invoke() ?? false;
        internal static bool SkinsEnabled => EnableSkins?.Invoke() ?? false;
    }

    /// <summary>
    /// The Sequence View's pattern colours (engine-split.md S7): a colour index per pattern, kept by
    /// the sequence editor (ViewSettings.PatternAssociations) and saved in the song by it.
    /// <see cref="Current"/> answers null while no sequence editor is up (tests, headless).
    /// </summary>
    public interface IPatternColours
    {
        IEnumerable<IPattern> Patterns { get; }
        bool TryGet(IPattern pattern, out int colorIndex);

        /// <summary>Sets a pattern's colour; an existing entry keeps its identity.</summary>
        void Set(IPattern pattern, int colorIndex);

        /// <summary>Adds an entry; throws if the pattern has one (as Dictionary.Add).</summary>
        void Add(IPattern pattern, int colorIndex);

        bool Remove(IPattern pattern);
    }

    public static class PatternColours
    {
        /// <summary>Set by the WPF app.</summary>
        public static Func<IPatternColours> Source { get; set; }

        /// <summary>The sequence editor's colours, or null while it isn't up.</summary>
        public static IPatternColours Current => Source?.Invoke();
    }
}
