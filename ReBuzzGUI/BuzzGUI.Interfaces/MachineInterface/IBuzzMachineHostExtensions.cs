using System.Collections.Generic;
using BuzzGUI.Interfaces;

namespace Buzz.MachineInterface
{
    // Fork (docs/specs/plugin-host.md §3.1, phase H1): what a managed machine that hosts other things (plugins)
    // needs of the engine, as optional interfaces. A machine implements the ones it uses; the engine checks
    // for them, so every other managed machine is untouched.

    /// <summary>
    /// A managed machine type that stands for many things (one host DLL, every scanned plugin), as a native
    /// "loader" machine does. The machine class also declares
    /// <c>public static IEnumerable&lt;string&gt; GetInstrumentList()</c>: the names the Gear menu lists
    /// ("Vendor/Name (VST3)": a '/' makes submenus). A song stores the chosen name as the machine's instrument.
    /// </summary>
    public interface IBuzzMachineInstrument
    {
        /// <summary>
        /// The instrument the machine was created for, or has in a song. Called once, right after the engine has made the
        /// machine's parameters and before its state is set, and not at all when the Gear menu entry is the machine itself.
        /// </summary>
        void SetInstrument(string instrument);
    }

    /// <summary>
    /// The engine side of a machine's parameters changing after it was made (a bank of slots that takes the names of
    /// the plugin parameters bound to it). The host object a machine is constructed with implements this too:
    /// <c>host as IBuzzMachineHostRuntime</c>. Global parameters only; call from the main thread.
    /// </summary>
    public interface IBuzzMachineHostRuntime
    {
        /// <summary>
        /// Renames a global parameter, gives it another range and default, and shows or hides it. A hidden parameter stays in the
        /// machine and its song data but is not listed by the agent server or the parameter windows that honour
        /// <see cref="IParameterMetadata.IsVisible"/>. The machine sets the values the new range needs itself
        /// (<c>IParameter.SetValue</c>). Does nothing for an index the machine doesn't have.
        /// </summary>
        void SetGlobalParameterInfo(int index, string name, string description, int min, int max, int def, bool visible);
    }

    /// <summary>
    /// A machine that describes its parameters itself, per instance, in the agent server's manifest format
    /// (<c>rebuzz-machine-manifest/1</c>, as the .machine.json files beside machine DLLs): the plugin host builds it from
    /// the loaded plugin, with units. The engine asks again whenever <see cref="ManifestRevision"/> changes.
    /// </summary>
    public interface IBuzzMachineManifest
    {
        /// <summary>The manifest as JSON, or null for none (the manifest beside the DLL, if any, applies).</summary>
        string ManifestJson { get; }

        /// <summary>Changes whenever <see cref="ManifestJson"/> does.</summary>
        int ManifestRevision { get; }
    }
}
