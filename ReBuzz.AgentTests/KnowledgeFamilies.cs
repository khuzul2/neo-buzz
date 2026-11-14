// Fork (docs/specs/agent-knowledge.md, K2): K2 is done, so every packaged machine except the Neo machines and Dye Essence (whose
// manifests live beside their sources and whose pages K1 and the Neo work wrote) is held to the same standard: a curated manifest
// (exact mappings, plain names, quirks, evidence), Explain text (How and Sound) on every parameter, and a page on the §4 template.
// K2's batches covered the Pedal, Jeskola, FSM, Oomek, Polac and Joachims families, ReBuzz's own machines, Rout 909 and the single-machine
// families (batch d). A machine that has no page or no curated manifest fails CuratedManifestTests, GuideTests, ExplainTextTests and
// KnowledgeBudgetTests; a machine added later that is not a Neo machine needs the same.
namespace ReBuzz.AgentTests
{
    internal static class KnowledgeFamilies
    {
        /// <summary>The machines that are not held to the packaged-machine standard here: the Neo machines (Neo ...) and Dye Essence.</summary>
        public static bool IsNeoOrDye(string gear) =>
            gear.StartsWith("Neo ", StringComparison.OrdinalIgnoreCase) || string.Equals(gear, "Dye Essence", StringComparison.OrdinalIgnoreCase);

        /// <summary>Whether a machine is a packaged machine that is not Neo or Dye: all of them are done (K2).</summary>
        public static bool Covers(string gear) => !IsNeoOrDye(gear);
    }
}
