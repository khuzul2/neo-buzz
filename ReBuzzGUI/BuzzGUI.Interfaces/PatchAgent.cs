// Fork (docs/specs/neo-patch.md §4.6, phase NP3): what Neo Patch offers the agent server across assemblies. The patch_* tools
// (ReBuzz.Agent/App/PatchTools.cs) hand each call to the machine as an operation name and a JSON object; the machine does it through the
// same edit model as its inner canvas, so each edit is one step on the song's undo stack, shared with the editor's own.

namespace BuzzGUI.Interfaces
{
    /// <summary>A machine whose patch an agent can edit (Neo Patch).</summary>
    public interface IPatchAgent
    {
        /// <summary>
        /// Runs one operation: describe, add_node, connect, disconnect, set, remove, macro_map, load_template or rename. <paramref name="argsJson"/> is a JSON
        /// object. Returns a JSON object: the result, or {"error": "…"} (what was refused and what to do instead). A refused call changes nothing.
        /// </summary>
        string Invoke(string op, string argsJson);
    }
}
