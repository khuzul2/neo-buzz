// Fork (docs/specs/agent-knowledge.md, K1): the machines whose Explain text (How and Sound on every parameter) and machine page
// are still being brought up to the K1 standard. Both shared rules treat these as warnings and everything else as failures:
// ExplainTextTests (every parameter has How and Sound) and KnowledgeBudgetTests.ThePagesAgainstTheTemplate (the page template).
// A phase removes its machines' names when its text and pages land (one name per line, so the edits merge cleanly).
namespace ReBuzz.AgentTests
{
    internal static class KnowledgePending
    {
        public static readonly HashSet<string> Gear = new(StringComparer.OrdinalIgnoreCase)
        {
            "Neo Delay",
            "Neo Drums",
            "Neo Ensemble",
            "Neo FM6",
            "Neo Poly",
            "Neo Sampler",
            "Neo Tape",
            "Neo Verb",
            "Neo Wave",
            "Dye Essence",
        };
    }
}
