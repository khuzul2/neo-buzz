// Fork (docs/specs/agent-knowledge.md, K2): the machine families whose knowledge is done, so that every machine in them is
// held to the same standard as the Neo machines: a curated manifest (exact mappings, plain names, quirks, evidence), Explain text
// (How and Sound) on every parameter, and a page on the §4 template. The later batches of K2 add their family here (Jeskola,
// FSM and Oomek, Polac and Joachims, ReBuzz's own machines and the rest); a machine of a family that has no page or no curated
// manifest fails CuratedManifestTests, GuideTests, ExplainTextTests and KnowledgeBudgetTests.
namespace ReBuzz.AgentTests
{
    internal static class KnowledgeFamilies
    {
        /// <summary>Gear name prefixes of the families done so far (K2 batch a: the Pedal machines; batch b: the Jeskola machines).</summary>
        public static readonly string[] Prefixes =
        {
            "Pedal ",
            "Jeskola ",
        };

        /// <summary>Whether a machine belongs to a family that is done.</summary>
        public static bool Covers(string gear) => Prefixes.Any(p => gear.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }
}
