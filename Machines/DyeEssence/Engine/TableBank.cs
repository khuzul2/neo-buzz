#nullable disable
// Dye Essence (docs/specs/dye-essence.md §4.1): the built-in wavetables as the renderer reads them.
// The recipes are Neo Wave's (Machines/NeoWave/Engine/Tables.cs, linked as source: generated, no
// binary assets); each becomes an arena once per process, on a background thread when first wanted,
// so the audio thread never builds one.

using System.Threading;
using System.Threading.Tasks;
using Recipes = NeoBuzz.Wave.Tables;

namespace NeoBuzz.DyeEssence
{
    internal static class TableBank
    {
        public static int Count => Recipes.Names.Length;
        public static string[] Names => Recipes.Names;

        static readonly TableArena[] built = new TableArena[Recipes.Names.Length];
        static readonly int[] requested = new int[Recipes.Names.Length];

        /// <summary>The table if built; otherwise null, and a build is started.</summary>
        public static TableArena Get(int index)
        {
            if (index < 0 || index >= Count) return null;
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            if (Interlocked.Exchange(ref requested[index], 1) == 0) Build(index);
            return null;
        }

        // Its own method: a lambda capturing the index in Get would allocate its closure on every call.
        static void Build(int index) => Task.Run(() => Volatile.Write(ref built[index], new TableArena(Recipes.GetNow(index))));

        /// <summary>Whether every built-in table is built (starting the builds not yet asked for). Any thread.</summary>
        public static bool AllBuilt()
        {
            bool all = true;
            for (int i = 0; i < Count; i++)
                if (Get(i) == null) all = false;
            return all;
        }

        /// <summary>Builds now (tests, editors); returns the table.</summary>
        public static TableArena GetNow(int index)
        {
            var t = Volatile.Read(ref built[index]);
            if (t != null) return t;
            t = new TableArena(Recipes.GetNow(index));
            Volatile.Write(ref built[index], t);
            Interlocked.Exchange(ref requested[index], 1);
            return t;
        }
    }
}
