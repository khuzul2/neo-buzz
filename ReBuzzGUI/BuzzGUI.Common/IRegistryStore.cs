using System.Collections.Generic;

namespace BuzzGUI.Common
{
    /// <summary>
    /// Fork (docs/specs/mac-engine.md M1): where RegistryEx and the settings classes keep values
    /// when there is no Windows registry (rebuzz-headless, macOS). Paths are under
    /// Global.RegistryRoot, as the registry's are ("Settings", "BuzzGUI\EngineSettings"); values
    /// are text, as RegistryEx writes them.
    /// </summary>
    public interface IRegistryStore
    {
        string Get(string path, string key);
        void Set(string path, string key, string value);
        IEnumerable<string> Names(string path);
        IReadOnlyDictionary<string, object> Values(string path);
    }
}
